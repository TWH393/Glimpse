using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace WpfApp1;

public enum LocalDeviceMode { Auto, PreferGpu, Cpu }
public enum ModelPowerMode { Performance, PowerSaving }

public sealed class Settings
{
    public string ApiUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string OnlineUrl { get; set; } = "";
    public string Tesseract { get; set; } = "tesseract";
    public string OcrLanguages { get; set; } = "eng";
    public int OcrEngine { get; set; }
    public string OcrPython { get; set; } = FindPython(".runtime", "ocr-env");
    public string OcrGpuPython { get; set; } = FindPython(".runtime", "ocr-gpu-env");
    public LocalDeviceMode OcrDeviceMode { get; set; } = LocalDeviceMode.Auto;
    public string SelectionKey { get; set; } = "T";
    public string CaptureKey { get; set; } = "O";
    public int Engine { get; set; }
    public bool Offline { get; set; }
    public string LlamaServer { get; set; } = FindProjectAsset(".runtime", "llama", "llama-server.exe");
    public string CpuLlamaServer { get; set; } = FindProjectAsset(".runtime", "llama-cpu", "llama-server.exe");
    public string LocalModel { get; set; } = FindProjectAsset("models", "hy-mt2", "Hy-MT2-1.8B-Q4_K_M.gguf");
    public bool UseGpu { get; set; } = true;
    public LocalDeviceMode? DeviceMode { get; set; }
    [JsonIgnore] public LocalDeviceMode EffectiveDeviceMode => DeviceMode is { } mode && Enum.IsDefined(mode)
        ? mode : UseGpu ? LocalDeviceMode.Auto : LocalDeviceMode.Cpu;
    public int IdleMinutes { get; set; } = 5;
    public ModelPowerMode PowerMode { get; set; } = ModelPowerMode.Performance;
    public string SpeechPython { get; set; } = FindPython(".runtime", "speech-env");
    public string SpeechModel { get; set; } = FindProjectFolder("models", "speech", "distil-large-v3.5");
    public LocalDeviceMode SpeechDeviceMode { get; set; } = LocalDeviceMode.PreferGpu;
    public bool LiveGpuOnly { get; set; } = true;
    public bool MediaGpuOnly { get; set; } = true;
    public string MediaOutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Glimpse", "Transcripts");
    public bool CaptionBilingual { get; set; }
    public double CaptionFontSize { get; set; } = 28;
    public double CaptionBackgroundOpacity { get; set; } = 0.35;
    public double CaptionTextOpacity { get; set; } = 1;
    public string CaptionForegroundColor { get; set; } = "#FFFFFF";
    public string CaptionBackgroundColor { get; set; } = "#000000";
    public double CaptionWindowWidth { get; set; } = 850;
    public double CaptionWindowHeight { get; set; } = 230;
    public string SpeechSessionsFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Glimpse", "Sessions");
    public bool AutoSelectionPopup { get; set; } = true;
    public bool ClipboardSelectionFallback { get; set; } = true;
    public bool DarkMode { get; set; }
    public double ReadingFontSize { get; set; } = 19;
    [JsonIgnore] public string ApiKey { get; set; } = "";
    [JsonIgnore] public string OnlineKey { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
    public string ProtectedOnlineKey { get; set; } = "";
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimpse", "settings.json");
    private static string FindProjectAsset(params string[] parts)
    {
        var relative = Path.Combine(parts);
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, relative);
            if (File.Exists(path)) return path;
        }
        return Path.Combine(AppContext.BaseDirectory, relative);
    }
    private static string FindProjectFolder(params string[] parts)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, Path.Combine(parts));
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, Path.Combine(parts));
    }
    private static string FindPython(params string[] parts)
    {
        var portable = FindProjectAsset(Path.Combine(parts), "python.exe");
        return File.Exists(portable) ? portable : FindProjectAsset(Path.Combine(parts), "Scripts", "python.exe");
    }
    private static void ResolveInstalledAssets(Settings result)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "bundle.json"))) return;
        var bundled = new Settings();
        static string Resolve(string configured, string packaged)
        {
            // Keep explicit custom locations; remap the known packaged asset
            // names when importing settings from a development/older install.
            string normalized = configured.Replace('/', '\\');
            bool managed = string.IsNullOrWhiteSpace(configured) || !File.Exists(configured)
                || normalized.Contains("\\.runtime\\", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("\\models\\hy-mt2\\Hy-MT2-1.8B-Q4_K_M.gguf", StringComparison.OrdinalIgnoreCase);
            return managed && File.Exists(packaged) ? packaged : configured;
        }
        result.OcrPython = Resolve(result.OcrPython, bundled.OcrPython);
        result.OcrGpuPython = Resolve(result.OcrGpuPython, bundled.OcrGpuPython);
        result.SpeechPython = Resolve(result.SpeechPython, bundled.SpeechPython);
        result.LlamaServer = Resolve(result.LlamaServer, bundled.LlamaServer);
        result.CpuLlamaServer = Resolve(result.CpuLlamaServer, bundled.CpuLlamaServer);
        result.LocalModel = Resolve(result.LocalModel, bundled.LocalModel);
    }
    public static Settings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new Settings { Offline = true, Engine = 2 };
        }
        var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        ResolveInstalledAssets(result);
        result.ApiKey = Unprotect(result.ProtectedApiKey);
        result.OnlineKey = Unprotect(result.ProtectedOnlineKey);
        return result;
    }
    public void Save()
    {
        ProtectedApiKey = Protect(ApiKey); ProtectedOnlineKey = Protect(OnlineKey);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
    private static string Protect(string text) => text.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));
    private static string Unprotect(string text) => text.Length == 0 ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser));
}
