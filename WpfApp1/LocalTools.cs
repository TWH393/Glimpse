using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace WpfApp1;

public static class LocalTools
{
    public static async Task<string> Run(string executable, string[] args, string? input, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (Path.GetFileNameWithoutExtension(executable).StartsWith("python", StringComparison.OrdinalIgnoreCase))
        {
            info.Environment.Remove("PYTHONHOME"); info.Environment.Remove("PYTHONPATH");
            info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        }
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try { process.Start(); } catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("找不到或无法启动本地程序，请在设置中检查路径及依赖。"); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        if (input != null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        var result = await output; var diagnostic = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("本地引擎失败：" + (diagnostic.Length > 400 ? diagnostic[..400] : diagnostic));
        return result.Trim();
    }
    public static async Task<string> Ocr(string image, Settings settings, CancellationToken token)
    {
        if (settings.OcrEngine == 0)
        {
            using var session = new OcrSession(settings, token);
            return await session.Recognize(image, token);
        }
        if (settings.OcrEngine == 1) return await WindowsOcr(image, settings, token);
        if (settings.OcrEngine != 2) throw new InvalidOperationException("请选择有效的 OCR 引擎。");
        string executable = settings.Tesseract.Trim().Trim('"');
        string? resolved = File.Exists(executable) ? Path.GetFullPath(executable) : null;
        if (resolved == null && !string.IsNullOrWhiteSpace(executable) && Path.GetFileName(executable) == executable)
        {
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                var candidate = Path.Combine(folder.Trim().Trim('"'), executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable : executable + ".exe");
                if (File.Exists(candidate)) { resolved = candidate; break; }
            }
        }
        if (resolved != null)
            return await Run(resolved, new[] { image, "stdout", "-l", settings.OcrLanguages, "--psm", "6" }, null, token);
        throw new InvalidOperationException("找不到 Tesseract，请配置完整路径，或切换到本地 RapidOCR。");
    }
    private static async Task<string> WindowsOcr(string image, Settings settings, CancellationToken token)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "worker", "ocr.ps1");
        if (!File.Exists(script)) throw new InvalidOperationException("Windows OCR 脚本缺失，请重新生成项目以复制 worker/ocr.ps1。");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var output = await Run(powershell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-STA", "-ExecutionPolicy", "Bypass", "-File", script,
            "-ImagePath", Path.GetFullPath(image), "-Languages", settings.OcrLanguages }, null, token);
        using var result = JsonDocument.Parse(output);
        if (!result.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("Windows OCR 不可用：" + result.RootElement.GetProperty("error").GetString());
        return result.RootElement.GetProperty("text").GetString() ?? "";
    }
}
