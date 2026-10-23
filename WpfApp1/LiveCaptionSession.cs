using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WpfApp1;

internal sealed class LiveCaptionSession : IDisposable
{
    private readonly Settings settings;
    private readonly int pid;
    private readonly string application;
    private readonly CancellationTokenSource captureStop = new(), abort = new();
    private sealed record AudioMessage(string Type, double Start = 0, string? Pcm = null, long QueuedAt = 0);
    private readonly Channel<AudioMessage> audio = Channel.CreateBounded<AudioMessage>(600);
    private readonly Channel<CaptionEntry> translations = Channel.CreateBounded<CaptionEntry>(12);
    private readonly TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<(string English, string Chinese)> context = new();
    private Process? worker;
    private ProcessLifetime? lifetime;
    private IAsyncDisposable? lease;
    private readonly CaptionTranscript transcript;
    private readonly Stopwatch timeline;
    private double captureOffset;
    private Task captureTask = Task.CompletedTask, sendTask = Task.CompletedTask, readTask = Task.CompletedTask, translateTask = Task.CompletedTask;
    private Task? stopTask;
    private Task? startTask;
    private readonly object lifecycle = new();
    private long translationBacklog;
    private volatile bool paused, capturing;
    private string failure = "";
    private string speechBackend = "", translationBackend = "";
    private readonly Stopwatch meter = Stopwatch.StartNew();
    private readonly StringBuilder diagnostics = new();
    public event Action<string>? Status, Draft, Failed;
    public event Action<CaptionEntry>? Caption;
    public event Action<CaptionEntry, string>? Preview;
    public event Action<CaptionMetrics>? Timing;
    public event Action<double>? Level;
    public string TranscriptPath => transcript.FilePath;
    public bool Paused => paused;
    public string Failure => failure;

    public LiveCaptionSession(Settings configuration, int processId, string name, CaptionTranscript record, Stopwatch clock)
    {
        settings = configuration; settings.Engine = 2; settings.Offline = true;
        pid = processId; application = name; transcript = record ?? throw new ArgumentNullException(nameof(record)); timeline = clock;
        foreach (var pair in transcript.RecentContext()) context.Enqueue(pair);
    }

    public Task Start()
    { lock (lifecycle) return startTask ??= StartCore(); }
    private async Task StartCore()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) throw new InvalidOperationException("按应用采音初版需要 Windows 11。");
        if (!File.Exists(settings.SpeechPython) || !File.Exists(Path.Combine(settings.SpeechModel, "model.bin")))
            throw new InvalidOperationException("未找到本地语音环境或模型，请运行 tools/install_speech.ps1 或检查路径。");
        Status?.Invoke("正在加载 Hy-MT2，随后加载英语识别模型…");
        lease = await LocalLlm.HoldForLiveSession(settings, abort.Token);
        await LocalLlm.Preload(settings, abort.Token, settings.LiveGpuOnly);
        abort.Token.ThrowIfCancellationRequested();
        string script = FindWorker();
        var info = new ProcessStartInfo(settings.SpeechPython)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = Path.GetDirectoryName(settings.SpeechPython)!
        };
        foreach (string argument in new[] { "-X", "utf8", "-u", script, "--model", settings.SpeechModel,
            "--device", settings.LiveGpuOnly ? "gpu-only" : settings.SpeechDeviceMode == LocalDeviceMode.Cpu ? "cpu" : settings.SpeechDeviceMode == LocalDeviceMode.Auto ? "auto" : "gpu" })
            info.ArgumentList.Add(argument);
        info.Environment.Remove("PYTHONHOME"); info.Environment.Remove("PYTHONPATH");
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        worker = new Process { StartInfo = info };
        worker.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (diagnostics) { diagnostics.AppendLine(e.Data); if (diagnostics.Length > 4000) diagnostics.Remove(0, diagnostics.Length - 4000); }
        };
        worker.Start(); lifetime = new ProcessLifetime(worker); worker.BeginErrorReadLine();
        readTask = ReadResults();
        string backend = await ready.Task.WaitAsync(TimeSpan.FromMinutes(2), abort.Token);
        abort.Token.ThrowIfCancellationRequested();
        transcript.Mark($"ASR：{backend}；翻译：Hy-MT2（实际设备见状态）；语音模型：{Path.GetFileName(settings.SpeechModel)}");
        translateTask = TranslateResults();
        foreach (var pending in transcript.Pending())
        {
            pending.QueuedAt = Stopwatch.GetTimestamp();
            Interlocked.Add(ref translationBacklog, Math.Max(1, (long)((pending.End - pending.Start) * 1000)));
            await translations.Writer.WriteAsync(pending, abort.Token);
        }
        abort.Token.ThrowIfCancellationRequested();
        if (failure.Length > 0) throw new InvalidOperationException(failure);
        sendTask = SendAudio();
        capturing = true;
        captureTask = Task.Run(async () =>
        {
            try { await ApplicationAudioCapture.Run(pid, ReceiveAudio, () => paused,
                () =>
                {
                    captureOffset = timeline.IsRunning ? timeline.Elapsed.TotalSeconds : 0;
                    if (!timeline.IsRunning) timeline.Start();
                    transcript.Mark($"开始采音，当前音频时间轴 {captureOffset:F2} 秒");
                }, captureStop.Token); }
            catch (OperationCanceledException) when (captureStop.IsCancellationRequested) { }
            catch (Exception ex) { Fail("采音失败：" + ex.Message); }
            finally { audio.Writer.TryComplete(); }
        });
        Status?.Invoke($"实时字幕已启动 · 英语识别 {backend} · 翻译 Hy-MT2 · "
            + (pid == 0 ? "全局声音（包含系统声音，不含 Glimpse 自身）" : $"仅捕获 {application}"));
    }
    private static string FindWorker()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "worker", "live_asr.py");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到 worker/live_asr.py。");
    }
    private void ReceiveAudio(ApplicationAudioCapture.Packet packet)
    {
        if (paused || captureStop.IsCancellationRequested) return;
        if (!audio.Writer.TryWrite(new("audio", packet.Start + captureOffset, Convert.ToBase64String(packet.Pcm), Stopwatch.GetTimestamp())))
            throw new InvalidOperationException("识别积压过多，已停止采音；请降低负载后重试。");
        if (meter.ElapsedMilliseconds >= 250)
        {
            meter.Restart(); double peak = 0;
            for (int i = 0; i + 1 < packet.Pcm.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((double)BitConverter.ToInt16(packet.Pcm, i)) / 32768);
            Level?.Invoke(peak);
        }
    }
    private async Task SendAudio()
    {
        try
        {
            await foreach (var message in audio.Reader.ReadAllAsync(abort.Token))
            {
                var data = new { type = message.Type, start = message.Start, pcm = message.Pcm,
                    transport_s = message.QueuedAt == 0 ? 0 : Stopwatch.GetElapsedTime(message.QueuedAt).TotalSeconds };
                await worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data).AsMemory(), abort.Token);
                await worker.StandardInput.FlushAsync(abort.Token);
            }
            await worker!.StandardInput.WriteLineAsync("{\"type\":\"stop\"}");
            await worker.StandardInput.FlushAsync();
            worker.StandardInput.Close();
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
        catch (Exception ex) { if (!abort.IsCancellationRequested) Fail("语音进程通信中断：" + ex.Message); }
    }
    private async Task ReadResults()
    {
        bool done = false;
        try
        {
            while (await worker!.StandardOutput.ReadLineAsync(abort.Token) is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "ready":
                        speechBackend = root.GetProperty("backend").GetString()!;
                        ready.TrySetResult(speechBackend); break;
                    case "status":
                        string message = root.GetProperty("text").GetString()!;
                        transcript.Mark(message); Status?.Invoke(message); break;
                    case "draft": Draft?.Invoke(root.GetProperty("text").GetString()!); break;
                    case "segment":
                        speechBackend = root.GetProperty("backend").GetString()!;
                        var entry = transcript.Add(root.GetProperty("start").GetDouble(), root.GetProperty("end").GetDouble(), root.GetProperty("text").GetString()!,
                            root.GetProperty("buffer_s").GetDouble(), root.GetProperty("asr_s").GetDouble(), root.GetProperty("audio_queue_s").GetDouble());
                        Draft?.Invoke("");
                        long duration = Math.Max(1, (long)((entry.End - entry.Start) * 1000));
                        long backlog = Interlocked.Add(ref translationBacklog, duration);
                        if (backlog > 30000 || !translations.Writer.TryWrite(entry))
                        {
                            Interlocked.Add(ref translationBacklog, -duration);
                            transcript.Mark($"片段 #{entry.Id} 积压，保留英文等待恢复翻译。");
                            Fail("翻译队列已满，正在停止采音；已确认英文仍保存在记录中。");
                        }
                        else Status?.Invoke($"英语识别 {root.GetProperty("backend").GetString()} · 待翻译 {translations.Reader.Count} 段");
                        break;
                    case "error": throw new InvalidOperationException(root.GetProperty("text").GetString());
                    case "done": done = true; break;
                }
            }
            if (!done && !abort.IsCancellationRequested)
            {
                string details; lock (diagnostics) details = diagnostics.ToString();
                throw new InvalidOperationException("语音进程意外退出。" + details);
            }
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
        catch (Exception ex) { ready.TrySetException(ex); Fail(ex.Message); }
        finally
        {
            ready.TrySetException(new InvalidOperationException("语音进程在就绪前退出。"));
            translations.Writer.TryComplete();
        }
    }
    private async Task TranslateResults()
    {
        await foreach (var entry in translations.Reader.ReadAllAsync())
        {
            if (abort.IsCancellationRequested || failure.Length > 0)
            {
                Interlocked.Add(ref translationBacklog, -Math.Max(1, (long)((entry.End - entry.Start) * 1000)));
                continue;
            }
            try
            {
                string history = string.Join("\n", context.Select(pair => "EN: " + pair.English + "\nZH: " + pair.Chinese));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                double queued = Stopwatch.GetElapsedTime(entry.QueuedAt).TotalSeconds;
                var watch = Stopwatch.StartNew();
                double firstToken = 0, firstSubtitle = 0;
                bool received = false;
                CaptionMetrics Metrics() => new(entry.BufferSeconds, entry.AsrSeconds, entry.AudioQueueSeconds, queued,
                    firstToken, watch.Elapsed.TotalSeconds, firstSubtitle, Math.Max(0, timeline.Elapsed.TotalSeconds - entry.End));
                string chinese = await LocalLlm.Translate(entry.English, "en", "zh", settings, timeout.Token, null, history, text =>
                {
                    if (!received) { firstToken = watch.Elapsed.TotalSeconds; firstSubtitle = Math.Max(0, timeline.Elapsed.TotalSeconds - entry.End); received = true; }
                    Preview?.Invoke(entry, text); Timing?.Invoke(Metrics());
                }, settings.LiveGpuOnly);
                var metrics = Metrics();
                entry.Chinese = chinese; entry.Metrics = metrics;
                Caption?.Invoke(entry); Timing?.Invoke(metrics);
                transcript.Complete(entry, chinese);
                if (LocalLlm.LastTiming is { } timing && translationBackend != timing.Backend)
                {
                    translationBackend = timing.Backend;
                    transcript.Mark($"翻译实际设备：{translationBackend}；{timing.RouteNote}");
                }
                context.Enqueue((entry.English, chinese));
                while (context.Count > 4 || context.Sum(pair => pair.English.Length + pair.Chinese.Length) > 1000) context.Dequeue();
                Status?.Invoke($"英语识别 {speechBackend} · 翻译 {translationBackend} · 待翻译 {translations.Reader.Count} 段");
            }
            catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
            catch (Exception ex)
            {
                transcript.Mark($"片段 #{entry.Id} 翻译失败，半句未定稿：{ex.Message}");
                Fail("字幕翻译失败，英文已保留：" + ex.Message);
            }
            finally { Interlocked.Add(ref translationBacklog, -Math.Max(1, (long)((entry.End - entry.Start) * 1000))); }
        }
    }
    public void TogglePause()
    {
        if (!capturing || captureStop.IsCancellationRequested) return;
        paused = !paused;
        if (!audio.Writer.TryWrite(new("pause"))) Fail("暂停指令队列已满，正在停止会话。");
        transcript.Mark(paused ? "暂停采音" : "恢复采音");
        Level?.Invoke(0);
        Status?.Invoke(paused ? "已暂停采音；正在完成已收集的片段。" : "已恢复采音。");
    }
    private void Fail(string message)
    {
        if (Interlocked.CompareExchange(ref failure, message, "") != "") return;
        captureStop.Cancel();
        Status?.Invoke(message); Failed?.Invoke(message);
    }
    public void CancelAttempt()
    { captureStop.Cancel(); abort.Cancel(); KillWorker(); }
    public Task Stop()
    { lock (lifecycle) return stopTask ??= StopCore(); }
    private async Task StopCore()
    {
        captureStop.Cancel();
        if (!capturing)
        {
            abort.Cancel(); KillWorker();
            if (startTask != null) { try { await startTask; } catch (Exception) { } }
        }
        try
        {
            await captureTask;
            audio.Writer.TryComplete();
            await Task.WhenAll(sendTask, readTask).WaitAsync(TimeSpan.FromSeconds(30));
            await translateTask.WaitAsync(TimeSpan.FromSeconds(90));
        }
        catch (Exception ex)
        {
            failure = "停止时中断：" + ex.Message;
            abort.Cancel(); KillWorker();
            try { await translateTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        }
        finally
        {
            abort.Cancel(); KillWorker();
            try { await Task.WhenAll(sendTask, readTask).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            if (lease != null) { await lease.DisposeAsync(); lease = null; }
            context.Clear();
            capturing = false;
            worker?.Dispose(); lifetime?.Dispose();
        }
    }
    private void KillWorker()
    {
        try { if (worker is { HasExited: false }) worker.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public void Dispose()
    {
        captureStop.Cancel(); abort.Cancel(); KillWorker(); lifetime?.Dispose();
        // Stop is normally awaited by the UI; this also handles application exit.
        _ = Stop();
    }
}
