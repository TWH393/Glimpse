using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace WpfApp1;

public enum LocalDeviceMode { Auto, PreferGpu, Cpu }

public sealed class Settings
{
    public string ApiUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string OnlineUrl { get; set; } = "";
    public string Python { get; set; } = FindLocalPython();
    public string Tesseract { get; set; } = "tesseract";
    public string OcrLanguages { get; set; } = "eng";
    public int OcrEngine { get; set; }
    public string OcrPython { get; set; } = FindProjectAsset(".runtime", "ocr-env", "Scripts", "python.exe");
    public string OcrGpuPython { get; set; } = FindProjectAsset(".runtime", "ocr-gpu-env", "Scripts", "python.exe");
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
    public bool AutoSelectionPopup { get; set; } = true;
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
    private static string FindLocalPython()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            var executable = Path.Combine(folder.FullName, ".venv", "Scripts", "python.exe");
            if (File.Exists(executable)) return executable;
        }
        return "python";
    }
    public static Settings Load()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = new Settings { Offline = true };
            defaults.Engine = File.Exists(defaults.LocalModel) && (File.Exists(defaults.LlamaServer) || File.Exists(defaults.CpuLlamaServer)) ? 3 : 2;
            return defaults;
        }
        var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
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
