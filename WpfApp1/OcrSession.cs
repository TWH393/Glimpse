using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp1;

/// <summary>Owns a single capture's OCR process, including initialization during selection.</summary>
public sealed class OcrSession : IDisposable
{
    private readonly Process process;
    private readonly ProcessLifetime lifetime;
    private readonly CancellationTokenRegistration cancellation;
    private readonly Task<string?> ready;
    private readonly Task<string> diagnostics;
    private bool disposed;
    private readonly string startupNote;
    public string Backend { get; private set; } = "";
    public string Note { get; private set; } = "";

    public OcrSession(Settings settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool gpu = settings.OcrDeviceMode != LocalDeviceMode.Cpu && File.Exists(settings.OcrGpuPython);
        string python = gpu ? settings.OcrGpuPython : settings.OcrPython;
        startupNote = settings.OcrDeviceMode != LocalDeviceMode.Cpu && !gpu ? "未安装 OCR GPU 环境，已使用 CPU" : "";
        if (!File.Exists(python)) throw new InvalidOperationException("OCR 环境不存在，请运行 tools/install_ocr.ps1，GPU 环境运行 tools/install_ocr.ps1 -Gpu。");
        var script = Path.Combine(AppContext.BaseDirectory, "worker", "ocr_local.py");
        if (!File.Exists(script)) throw new InvalidOperationException("本地 OCR 脚本缺失，请重新生成项目。");
        var info = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(python)!
        };
        foreach (var argument in new[] { "-X", "utf8", "-u", script, "--device", gpu ? "gpu" : "cpu" }) info.ArgumentList.Add(argument);
        process = new Process { StartInfo = info };
        try
        {
            process.Start();
            lifetime = new ProcessLifetime(process);
            diagnostics = process.StandardError.ReadToEndAsync();
            _ = diagnostics.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            ready = process.StandardOutput.ReadLineAsync(token).AsTask();
            // Selection can be cancelled before anyone awaits the ready message.
            _ = ready.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            cancellation = token.Register(Kill);
        }
        catch
        {
            Kill(); process.Dispose(); lifetime?.Dispose();
            throw;
        }
    }

    public async Task<string> Recognize(string image, CancellationToken token, IProgress<string>? progress = null)
    {
        using (var initialized = await ReadMessage(await ready.WaitAsync(token), token))
        {
            EnsureSuccess(initialized);
            Backend = initialized.RootElement.GetProperty("backend").GetString() ?? "CPU";
            string workerNote = initialized.RootElement.GetProperty("note").GetString() ?? "";
            Note = workerNote.Length > 0 ? "GPU 初始化失败，已回退 CPU：" + workerNote : startupNote;
        }
        progress?.Report($"正在识别 · RapidOCR · {Backend}" + (Note.Length > 0 ? "（已回退 CPU）" : ""));
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { image = Path.GetFullPath(image) }).AsMemory(), token);
        process.StandardInput.Close();
        using var result = await ReadMessage(await process.StandardOutput.ReadLineAsync(token), token);
        EnsureSuccess(result);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException("本地 OCR 进程异常退出：" + await diagnostics.WaitAsync(token));
        return result.RootElement.GetProperty("text").GetString() ?? "";
    }

    private async Task<JsonDocument> ReadMessage(string? line, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(line)) return JsonDocument.Parse(line);
        await process.WaitForExitAsync(token);
        string detail = await diagnostics.WaitAsync(token);
        throw new InvalidOperationException("OCR 进程未返回结果：" + (detail.Length > 2000 ? detail[^2000..] : detail));
    }

    private static void EnsureSuccess(JsonDocument message)
    {
        if (!message.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("本地 OCR 失败：" + message.RootElement.GetProperty("error").GetString());
    }

    private void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Dispose(); Kill(); lifetime.Dispose(); process.Dispose();
    }
}
