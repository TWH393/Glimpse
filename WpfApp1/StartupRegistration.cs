using System;
using System.IO;
using Microsoft.Win32;

namespace WpfApp1;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Glimpse";
    internal static bool LaunchedAtLogin => Array.Exists(Environment.GetCommandLineArgs(),
        argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));

    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var appHost = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        string command;
        if (Environment.ProcessPath is string current && File.Exists(current)
            && string.Equals(Path.GetFileNameWithoutExtension(current), "Glimpse", StringComparison.OrdinalIgnoreCase))
            command = $"\"{current}\" --startup";
        else if (File.Exists(appHost)) command = $"\"{appHost}\" --startup";
        else if (Environment.ProcessPath is string host && File.Exists(host)
            && string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase)
            && File.Exists(typeof(App).Assembly.Location))
            command = $"\"{host}\" \"{typeof(App).Assembly.Location}\" --startup";
        else throw new InvalidOperationException("无法确定程序路径，请从生成的 Glimpse.exe 打开后再启用自启动。");

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的启动项设置。");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
