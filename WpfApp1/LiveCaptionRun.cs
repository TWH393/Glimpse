using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WpfApp1;

// One user session, one transcript, at most one automatic restart across ASR,
// translation and capture. Failed partial translations are never made final.
internal sealed class LiveCaptionRun : IDisposable
{
    private readonly Settings settings;
    private readonly int pid;
    private readonly string application;
    private readonly Stopwatch timeline = new();
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> stopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> writeFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile LiveCaptionSession? current;
    private CaptionTranscript? transcript;
    private Task? runTask;
    private volatile bool stopping;
    private volatile bool pauseRequested;
    public event Action<string>? Status, Draft, Failed, Notice;
    public event Action<CaptionEntry>? Caption;
    public event Action<CaptionEntry, string>? Preview;
    public event Action<CaptionMetrics>? Timing;
    public event Action? ResetPreview;
    public event Action<double>? Level;
    public string? TranscriptPath => transcript?.FilePath;
    public string Failure { get; private set; } = "";
    public bool Saved { get; private set; }
    public bool Recovering { get; private set; }
    public bool Paused => pauseRequested;
    public LiveCaptionRun(Settings configuration, int processId, string name)
    { settings = configuration; pid = processId; application = name; }

    public Task Start()
    {
        runTask ??= Task.Run(RunAttempts);
        return ready.Task;
    }
    private async Task RunAttempts()
    {
        int failures = 0;
        IDisposable? reservation = null;
        try
        {
            reservation = SpeechOperation.Acquire("实时字幕");
            transcript = new CaptionTranscript(settings.SpeechSessionsFolder, application,
                pid == 0 ? "全局输出（包含系统声音，排除 Glimpse 自身及子进程）" : $"应用进程树 · PID {pid}");
            transcript.WriteFailed += text => writeFault.TrySetResult(text);
            await transcript.Checkpoint().WaitAsync(TimeSpan.FromSeconds(15));
            while (!stopping)
            {
                var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var attempt = new LiveCaptionSession(settings, pid, application, transcript, timeline);
                current = attempt;
                attempt.Status += text => Status?.Invoke(text);
                attempt.Draft += text => Draft?.Invoke(text);
                attempt.Level += value => Level?.Invoke(value);
                attempt.Caption += entry => Caption?.Invoke(entry);
                attempt.Preview += (entry, text) => Preview?.Invoke(entry, text);
                attempt.Timing += metrics => Timing?.Invoke(metrics);
                attempt.Failed += text => failed.TrySetResult(text);
                string error = "";
                try
                {
                    if (stopping) break;
                    await attempt.Start();
                    if (stopping) break;
                    Recovering = false;
                    if (pauseRequested && !attempt.Paused) attempt.TogglePause();
                    ready.TrySetResult(true);
                    var winner = await Task.WhenAny(failed.Task, stopSignal.Task, writeFault.Task);
                    if (winner == writeFault.Task) throw new IOException(await writeFault.Task);
                    if (winner == stopSignal.Task) break;
                    error = await failed.Task;
                }
                catch (Exception ex)
                {
                    if (stopping) break;
                    error = ex.Message;
                }
                if (stopping) break;
                failures++;
                Recovering = true;
                string notice = $"[第 {failures} 次失败] {error}";
                Notice?.Invoke(notice);
                ResetPreview?.Invoke();
                transcript.Mark(notice + "；流式半句未定稿。");
                attempt.CancelAttempt();
                await attempt.Stop();
                attempt.Dispose();
                current = null;
                transcript.Mark("本次中断期间未识别的音频不能补录；已确认英文保留。");
                await transcript.Checkpoint().WaitAsync(TimeSpan.FromSeconds(15));
                if (stopping) break;
                if (failures >= 2)
                {
                    Failure = "本次会话累计两次失败，已停止：" + error;
                    Notice?.Invoke("[停止并保存] " + Failure);
                    break;
                }
                Status?.Invoke("首次失败的文字已保存，正在重启识别和翻译进程（仅重试一次）…");
                transcript.Mark("已完成保存检查点，自动重启识别和翻译进程；继续原文件和时间轴。");
                await LocalLlm.Unload();
            }
        }
        catch (Exception ex)
        {
            Failure = "会话无法继续：" + ex.Message;
            Notice?.Invoke("[停止] " + Failure);
            try { transcript?.Mark(Failure); } catch (IOException) { }
        }
        finally
        {
            if (current is { } attempt)
            {
                try { await attempt.Stop(); if (Failure.Length == 0 && attempt.Failure.Length > 0) Failure = attempt.Failure; }
                catch (Exception ex) { Failure = "停止进程失败：" + ex.Message; Notice?.Invoke(Failure); }
                attempt.Dispose(); current = null;
            }
            ResetPreview?.Invoke();
            try
            {
                if (transcript != null)
                {
                    await transcript.Finish(Failure.Length == 0 ? "正常停止" : Failure).WaitAsync(TimeSpan.FromSeconds(20));
                    Saved = true;
                }
            }
            catch (Exception ex)
            {
                Failure += "；文字记录保存未完成：" + ex.Message;
                Notice?.Invoke(Failure);
            }
            if (Failure.Length > 0)
            {
                ready.TrySetException(new InvalidOperationException(Failure));
                Failed?.Invoke(Failure);
            }
            else ready.TrySetCanceled();
            reservation?.Dispose();
        }
    }
    public void TogglePause()
    {
        pauseRequested = !pauseRequested;
        if (current is { } attempt && attempt.Paused != pauseRequested) attempt.TogglePause();
    }
    public Task Checkpoint() => transcript?.Checkpoint() ?? Task.CompletedTask;
    public Task Stop()
    {
        stopping = true; stopSignal.TrySetResult(true);
        if (current != null) _ = current.Stop();
        return runTask ?? Task.CompletedTask;
    }
    public void Dispose()
    {
        stopping = true; stopSignal.TrySetResult(true);
        current?.CancelAttempt();
        _ = Stop();
    }
}
