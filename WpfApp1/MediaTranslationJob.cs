using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp1;

internal sealed record MediaProgress(double Position, double Duration, double Elapsed, double Asr, double Translation);

internal sealed class MediaTranslationJob : IDisposable
{
    private readonly Settings settings;
    private readonly string input, folder;
    private readonly CancellationTokenSource cancel = new();
    private readonly object sync = new(), diagnosticsLock = new();
    private readonly StringBuilder diagnostics = new();
    private readonly Stopwatch elapsed = new();
    private TaskCompletionSource<bool>? resume;
    private Task? runTask;
    private Process? worker;
    private CaptionTranscript? transcript;
    private double position, duration, asrSeconds, translationSeconds;
    public bool Paused { get { lock (sync) return resume != null; } }
    public string? OutputPath => transcript?.FilePath;
    public string Failure { get; private set; } = "";
    public bool Completed { get; private set; }
    public bool Saved { get; private set; }
    public event Action<string>? Status, Notice, Preview;
    public event Action<CaptionEntry>? Caption;
    public event Action<MediaProgress>? Progress;

    public MediaTranslationJob(Settings configuration, string file, string outputFolder, bool gpuOnly)
    {
        settings = configuration; input = file; folder = outputFolder;
        settings.Engine = 2; settings.Offline = true; settings.LiveGpuOnly = gpuOnly;
        settings.DeviceMode = gpuOnly ? LocalDeviceMode.PreferGpu : LocalDeviceMode.Cpu;
    }
    public Task Start()
    { lock (sync) return runTask ??= Task.Run(Run); }
    public MediaProgress Snapshot => new(position, duration, elapsed.Elapsed.TotalSeconds, asrSeconds, translationSeconds);
    private void Report() => Progress?.Invoke(Snapshot);
    public void TogglePause()
    {
        lock (sync)
        {
            if (resume == null) resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            else { resume.TrySetResult(true); resume = null; }
        }
        Status?.Invoke(Paused ? "暂停已请求；当前识别或翻译片段完成并保存后暂停。" : "继续处理…");
    }
    private async Task WaitIfPaused()
    {
        cancel.Token.ThrowIfCancellationRequested();
        Task? wait;
        lock (sync) wait = resume?.Task;
        if (wait != null)
        {
            Status?.Invoke("已暂停；可继续，或取消并保存已完成部分。");
            await wait.WaitAsync(cancel.Token);
        }
    }
    private async Task Run()
    {
        IDisposable? reservation = null;
        elapsed.Start();
        try
        {
            reservation = SpeechOperation.Acquire("音视频转译");
            if (!File.Exists(input)) throw new FileNotFoundException("找不到导入文件。", input);
            if (!File.Exists(settings.SpeechPython) || !File.Exists(Path.Combine(settings.SpeechModel, "model.bin")))
                throw new InvalidOperationException("未找到本地语音环境或模型，请先在实时字幕设置中配置路径。");
            string stem = string.Concat(Path.GetFileNameWithoutExtension(input).Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim().TrimEnd('.');
            if (stem.Length > 100) stem = stem[..100];
            if (stem.Length == 0) stem = "音视频";
            transcript = new CaptionTranscript(folder, Path.GetFileName(input), fileStem: stem + "_中英翻译",
                fileHeader: $"Glimpse 音视频转译\n文件：{input}\n开始时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n方向：英语 → 简体中文\n设备策略：{(settings.LiveGpuOnly ? "仅 GPU" : "仅 CPU")}\n时间戳：相对原文件起点\n仅保存定稿译文；耗时只在界面显示\n");
            await transcript.Checkpoint();
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    await Attempt();
                    Completed = true;
                    break;
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    string message = $"[第 {attempt} 次失败] {ex.Message}";
                    Notice?.Invoke(message); Preview?.Invoke(""); transcript.Mark(message);
                    await transcript.Checkpoint().WaitAsync(TimeSpan.FromSeconds(20), cancel.Token);
                    if (attempt == 2) throw new InvalidOperationException("重试后再次失败，停止并保留已有结果。" + ex.Message, ex);
                    Status?.Invoke("进度已保存，正在重启识别和翻译进程（仅重试一次）…");
                    await LocalLlm.Unload(cancel.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        { Notice?.Invoke("[取消] 保留已完成译文和已提取的英文。"); }
        catch (Exception ex)
        { Failure = ex.Message; Notice?.Invoke("[停止] " + Failure); }
        finally
        {
            KillWorker(); Preview?.Invoke("");
            try
            {
                if (transcript != null)
                {
                    await transcript.Finish(Completed ? "处理完成" : Failure.Length > 0 ? Failure : "用户取消").WaitAsync(TimeSpan.FromSeconds(20));
                    Saved = true;
                }
            }
            catch (Exception ex) { Failure += "；保存未完成：" + ex.Message; Notice?.Invoke(Failure); }
            elapsed.Stop(); Report(); reservation?.Dispose();
        }
    }
    private async Task Attempt()
    {
        await WaitIfPaused();
        Status?.Invoke("正在加载本地翻译模型…");
        await using var lease = await LocalLlm.HoldForLiveSession(settings, cancel.Token);
        await LocalLlm.Preload(settings, cancel.Token, settings.LiveGpuOnly);
        await TranslatePending();
        await WaitIfPaused();
        Status?.Invoke("正在打开音轨、加载英语识别模型…");
        var info = new ProcessStartInfo(settings.SpeechPython)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-X", "utf8", "-u", FindWorker(), "--model", settings.SpeechModel,
            "--file", input, "--device", settings.LiveGpuOnly ? "gpu-only" : "cpu", "--resume", position.ToString("R", CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(argument);
        info.Environment.Remove("PYTHONHOME"); info.Environment.Remove("PYTHONPATH");
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var process = new Process { StartInfo = info };
        worker = process;
        lock (diagnosticsLock) diagnostics.Clear();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (diagnosticsLock) { diagnostics.AppendLine(e.Data); if (diagnostics.Length > 3500) diagnostics.Remove(0, diagnostics.Length - 3500); }
        };
        try
        {
            process.Start();
            using var lifetime = new ProcessLifetime(process);
            using var cancelRegistration = cancel.Token.Register(KillWorker);
            process.BeginErrorReadLine();
            using (var ready = await ReadMessage(process, TimeSpan.FromMinutes(3)))
            {
                var root = ready.RootElement;
                if (root.GetProperty("type").GetString() != "ready") throw new IOException("语音进程未就绪。");
                duration = root.GetProperty("duration").GetDouble();
                string details = $"音轨 #{root.GetProperty("track").GetInt32()} · 语言标记 {root.GetProperty("language").GetString()} · 英语识别 {root.GetProperty("backend").GetString()}";
                transcript!.Mark(details); Status?.Invoke(details); Report();
            }
            while (true)
            {
                await WaitIfPaused();
                string history = string.Join(" ", transcript!.RecentContext().Select(pair => pair.English));
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "next", context = history }).AsMemory(), cancel.Token);
                await process.StandardInput.FlushAsync(cancel.Token);
                Status?.Invoke($"正在识别 {TimeSpan.FromSeconds(position):hh\\:mm\\:ss} 附近的英文…");
                using var message = await ReadMessage(process, TimeSpan.FromMinutes(10));
                var root = message.RootElement;
                string? type = root.GetProperty("type").GetString();
                if (type == "done")
                { position = root.GetProperty("position").GetDouble(); Report(); return; }
                if (type != "batch") throw new IOException("语音进程返回未知消息。");
                asrSeconds = root.GetProperty("asr_s").GetDouble();
                foreach (var item in root.GetProperty("segments").EnumerateArray())
                {
                    transcript.Add(item.GetProperty("start").GetDouble(), item.GetProperty("end").GetDouble(), item.GetProperty("text").GetString()!, 0, 0, 0);
                }
                // Advance once all English is queued. Recovery translates Pending()
                // first, then resumes at this boundary without decoding it again.
                position = root.GetProperty("position").GetDouble();
                await transcript.Checkpoint().WaitAsync(TimeSpan.FromSeconds(20), cancel.Token);
                Report();
                await TranslatePending();
                await transcript.Checkpoint().WaitAsync(TimeSpan.FromSeconds(20), cancel.Token);
                Report();
            }
        }
        finally
        {
            KillWorker();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            worker = null;
        }
    }
    private async Task TranslatePending()
    {
        foreach (var entry in transcript!.Pending())
        {
            await WaitIfPaused();
            Status?.Invoke($"正在翻译片段 #{entry.Id}…");
            string history = string.Join("\n", transcript.RecentContext().Select(pair => "EN: " + pair.English + "\nZH: " + pair.Chinese));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var watch = Stopwatch.StartNew();
            string chinese = await LocalLlm.Translate(entry.English, "en", "zh", settings, timeout.Token, null,
                history, text => Preview?.Invoke(text), settings.LiveGpuOnly);
            translationSeconds = watch.Elapsed.TotalSeconds;
            entry.Chinese = chinese;
            Caption?.Invoke(entry); Preview?.Invoke("");
            transcript.Complete(entry, chinese);
            await transcript.Checkpoint().WaitAsync(TimeSpan.FromSeconds(20), cancel.Token);
            Report();
        }
    }
    private async Task<JsonDocument> ReadMessage(Process process, TimeSpan timeout)
    {
        string? line = await process.StandardOutput.ReadLineAsync(cancel.Token).AsTask().WaitAsync(timeout, cancel.Token);
        if (line == null)
        {
            string detail; lock (diagnosticsLock) detail = diagnostics.ToString();
            throw new IOException("语音进程意外退出。" + detail);
        }
        var message = JsonDocument.Parse(line);
        if (message.RootElement.GetProperty("type").GetString() == "error")
        {
            string error = message.RootElement.GetProperty("text").GetString() ?? "识别失败";
            message.Dispose(); throw new InvalidOperationException(error);
        }
        return message;
    }
    private static string FindWorker()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "worker", "media_asr.py");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到 worker/media_asr.py。");
    }
    private void KillWorker()
    {
        try { if (worker is { HasExited: false } process) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public Task Checkpoint() => transcript?.Checkpoint() ?? Task.CompletedTask;
    public void Cancel() { cancel.Cancel(); KillWorker(); }
    public void Dispose() => Cancel();
}
