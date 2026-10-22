using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Input;
namespace WpfApp1;

internal static class Native
{
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out ScreenRect rectangle);
    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    internal static string Register(IntPtr handle, string selection, string capture)
    {
        Unregister(handle);
        if (!Enum.TryParse<Key>(selection, true, out var firstKey) || !Enum.TryParse<Key>(capture, true, out var secondKey)
            || !Allowed(firstKey) || !Allowed(secondKey)) return "快捷键请填写 A–Z 或 F1–F12，例如 T、O、F8。";
        bool first = RegisterHotKey(handle, 1, 0x4003, (uint)KeyInterop.VirtualKeyFromKey(firstKey));
        bool second = RegisterHotKey(handle, 2, 0x4003, (uint)KeyInterop.VirtualKeyFromKey(secondKey));
        return first && second ? $"快捷键已启用：Ctrl+Alt+{selection} 取词；Ctrl+Alt+{capture} 框选。"
            : $"快捷键占用：{(!first ? "取词 Ctrl+Alt+" + selection + " " : "")}{(!second ? "框选 Ctrl+Alt+" + capture : "")}；请在设置中换键。";
    }
    private static bool Allowed(Key key) => key >= Key.A && key <= Key.Z || key >= Key.F1 && key <= Key.F12;
    internal static void Unregister(IntPtr handle) { UnregisterHotKey(handle, 1); UnregisterHotKey(handle, 2); }
    internal static string SelectedText()
    {
        var focus = AutomationElement.FocusedElement;
        if (focus == null || focus.Current.IsPassword) return "";
        for (var current = focus; current != null; current = TreeWalker.ControlViewWalker.GetParent(current))
        {
            if (current.TryGetCurrentPattern(TextPattern.Pattern, out var value))
            {
                var builder = new StringBuilder();
                foreach (var selection in ((TextPattern)value).GetSelection()) builder.Append(selection.GetText(16001));
                if (builder.Length > 0) return builder.ToString();
            }
            if (current == AutomationElement.RootElement) break;
        }
        return "";
    }
}
