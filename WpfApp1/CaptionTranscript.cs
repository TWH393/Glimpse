using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WpfApp1;

internal sealed record CaptionMetrics(double BufferSeconds, double AsrSeconds, double AudioQueueSeconds,
    double TranslationQueueSeconds, double FirstTokenSeconds, double TranslationSeconds,
    double FirstSubtitleDelaySeconds, double FinalDelaySeconds)
{
    public override string ToString() => $"分句收集 {BufferSeconds:F2}s · 识别 {AsrSeconds:F2}s · 音频排队 {AudioQueueSeconds:F2}s · 翻译排队 {TranslationQueueSeconds:F2}s"
        + $" · 翻译首字 {FirstTokenSeconds:F2}s · 翻译总计 {TranslationSeconds:F2}s · 距句末首字 {FirstSubtitleDelaySeconds:F2}s · 定稿 {FinalDelaySeconds:F2}s";
}

internal sealed class CaptionEntry
{
    public int Id { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public string English { get; init; } = "";
    public string? Chinese { get; set; }
    public double BufferSeconds { get; init; }
    public double AsrSeconds { get; init; }
    public double AudioQueueSeconds { get; init; }
    public long QueuedAt { get; set; } = Stopwatch.GetTimestamp();
    public CaptionMetrics? Metrics { get; set; }
}

// UI/inference only enqueue immutable updates. Exactly one background writer owns
// the durable snapshot; checkpoints wait for all earlier operations, including fsync.
internal sealed class CaptionTranscript
{
    private sealed record Update(Action Apply, TaskCompletionSource<bool>? Barrier = null, bool Save = true);
    private readonly object sync = new();
    private readonly List<CaptionEntry> entries = new(), persisted = new();
    private readonly List<string> markers = new();
    private readonly Channel<Update> writes = Channel.CreateUnbounded<Update>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task writerTask;
    private readonly string header;
    private Exception? writeError;
    private Task? finishTask;
    private bool closed;
    public event Action<string>? WriteFailed;
    public string FilePath { get; }
    public CaptionTranscript(string folder, string application, string? sourceDetails = null, string? fileStem = null, string? fileHeader = null)
    {
        Directory.CreateDirectory(folder);
        var now = DateTimeOffset.Now;
        string name = string.Concat(application.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim().TrimEnd('.');
        if (name.Length > 80) name = name[..80].TrimEnd(' ', '.');
        if (name.Length == 0) name = "应用";
        FilePath = ReservePath(folder, fileStem ?? $"{now:yyyy-MM-dd_HHmmss}_{name}");
        header = fileHeader ?? $"Glimpse 实时字幕\n开始时间：{now:yyyy-MM-dd HH:mm:ss zzz}\n应用：{application}\n来源：{sourceDetails ?? application}\n方向：英语 → 简体中文\n时间戳：相对首次开始采音，暂停/重启期间继续计时\n默认不保存录音；流式半句不作为正式译文保存\n";
        writerTask = Task.Run(WriteLoop);
        Enqueue(new(() => { }));
    }
    private static string ReservePath(string folder, string stem)
    {
        for (int suffix = 1; ; suffix++)
        {
            string candidate = Path.Combine(folder, stem + (suffix == 1 ? "" : $"_{suffix}") + ".txt");
            try { using var reservation = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None); return candidate; }
            catch (IOException) when (File.Exists(candidate)) { }
        }
    }
    private void Enqueue(Update update)
    {
        if (writeError != null) throw new IOException("文字记录写入失败。", writeError);
        if (!writes.Writer.TryWrite(update)) throw new IOException("文字记录已经关闭。");
    }
    public CaptionEntry Add(double start, double end, string english, double bufferSeconds, double asrSeconds, double audioQueueSeconds)
    {
        lock (sync)
        {
            if (closed) throw new InvalidOperationException("会话记录已结束。");
            var entry = new CaptionEntry { Id = entries.Count + 1, Start = start, End = end, English = english,
                BufferSeconds = bufferSeconds, AsrSeconds = asrSeconds, AudioQueueSeconds = audioQueueSeconds };
            entries.Add(entry);
            Enqueue(new(() => persisted.Add(new CaptionEntry { Id = entry.Id, Start = start, End = end, English = english })));
            return entry;
        }
    }
    public void Complete(CaptionEntry entry, string chinese)
    {
        lock (sync)
        {
            if (closed) return;
            entry.Chinese = chinese;
            Enqueue(new(() => { var saved = persisted.First(item => item.Id == entry.Id); saved.Chinese = chinese; }));
        }
    }
    public void Mark(string text)
    {
        string marker = $"[{DateTimeOffset.Now:HH:mm:ss zzz}] {text}";
        lock (sync) { if (!closed) Enqueue(new(() => markers.Add(marker))); }
    }
    public IReadOnlyList<CaptionEntry> Pending()
    { lock (sync) return entries.Where(entry => entry.Chinese == null).ToArray(); }
    public IReadOnlyList<(string English, string Chinese)> RecentContext()
    {
        lock (sync)
        {
            var history = entries.Where(entry => entry.Chinese != null).TakeLast(4).Select(entry => (entry.English, entry.Chinese!)).ToList();
            while (history.Sum(pair => pair.English.Length + pair.Item2.Length) > 1000) history.RemoveAt(0);
            return history;
        }
    }
    public Task Checkpoint()
    {
        lock (sync)
        {
            if (closed) return finishTask ?? writerTask;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Enqueue(new(() => { }, completion, Save: false));
            return completion.Task;
        }
    }
    public Task Finish(string reason)
    {
        lock (sync)
        {
            if (closed) return finishTask ?? writerTask;
            closed = true;
            string missing = "[翻译未完成：" + reason + "]";
            foreach (var entry in entries.Where(entry => entry.Chinese == null)) entry.Chinese = missing;
            Enqueue(new(() =>
            {
                foreach (var entry in persisted.Where(entry => entry.Chinese == null)) entry.Chinese = missing;
                markers.Add($"结束时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} · {reason}");
            }));
            writes.Writer.TryComplete();
            finishTask = writerTask;
            return finishTask;
        }
    }
    private async Task WriteLoop()
    {
        try
        {
            await foreach (var update in writes.Reader.ReadAllAsync())
            {
                try { update.Apply(); if (update.Save) SaveSnapshot(); update.Barrier?.TrySetResult(true); }
                catch (Exception ex) { update.Barrier?.TrySetException(ex); throw; }
            }
        }
        catch (Exception ex)
        {
            writeError = ex;
            writes.Writer.TryComplete(ex);
            while (writes.Reader.TryRead(out var pending)) pending.Barrier?.TrySetException(ex);
            WriteFailed?.Invoke("文字记录写入失败：" + ex.Message);
            throw;
        }
    }
    private void SaveSnapshot()
    {
        var text = new StringBuilder(header).AppendLine();
        foreach (var entry in persisted)
        {
            text.AppendLine($"[{Stamp(entry.Start)} → {Stamp(entry.End)}] #{entry.Id:0000}")
                .AppendLine("EN: " + entry.English).AppendLine("ZH: " + (entry.Chinese ?? "[等待翻译/恢复]"));
            text.AppendLine();
        }
        if (markers.Count > 0) text.AppendLine("会话事件：").AppendJoin(Environment.NewLine, markers).AppendLine();
        string temporary = FilePath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(text.ToString()); writer.Flush(); stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, FilePath, overwrite: true);
    }
    private static string Stamp(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }
}
