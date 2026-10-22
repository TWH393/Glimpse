using System;
using System.Diagnostics;
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
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
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
    internal sealed record TextSelection(string Text, ScreenPoint? Anchor, bool AllowCopy = true);
    internal static string SelectedText() => ReadSelection().Text;
    internal static TextSelection ReadSelection()
    {
        GetCursorPos(out var point);
        return ReadSelectionAt(point, GetForegroundWindow());
    }

    internal static TextSelection ReadSelectionAt(ScreenPoint point, IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || GetForegroundWindow() != foreground) return new("", null);
        var budget = Stopwatch.StartNew();
        AutomationElement? focus = null;
        try
        {
            focus = AutomationElement.FocusedElement;
            if (focus?.Current.IsPassword == true) return new("", null, false);
        }
        catch (Exception ex) when (IsAutomationFailure(ex)) { }

        // Chat applications can leave keyboard focus in the composer while the
        // user selects a message. Prefer the element at the selection gesture.
        try
        {
            var hit = WindowFromPoint(point);
            var root = GetAncestor(hit, 2);
            if ((root == foreground || hit == foreground) && budget.ElapsedMilliseconds < 1500)
            {
                var underPointer = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
                var selection = ReadSelectionChain(underPointer, foreground, budget);
                if (selection.Text.Length > 0 || !selection.AllowCopy) return selection;
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex)) { }
        return ReadSelectionChain(focus, foreground, budget);
    }

    private static TextSelection ReadSelectionChain(AutomationElement? element, IntPtr foreground, Stopwatch budget)
    {
        for (int depth = 0; element != null && depth < 24 && budget.ElapsedMilliseconds < 1500; depth++)
        {
            try
            {
                if (element == AutomationElement.RootElement) break;
                if (element.Current.IsPassword) return new("", null, false);
                int handle = element.Current.NativeWindowHandle;
                if (handle != 0)
                {
                    var root = GetAncestor(new IntPtr(handle), 2);
                    if (root != IntPtr.Zero && root != foreground) break;
                }
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var value))
                {
                    var selection = ReadRanges((TextPattern)value);
                    if (selection.Text.Length > 0) return selection;
                }
                if (handle != 0 && new IntPtr(handle) == foreground) break;
                // Raw view retains text/document ancestors omitted by ControlView.
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
            catch (Exception ex) when (IsAutomationFailure(ex)) { break; }
        }
        return new("", null);
    }

    private static TextSelection ReadRanges(TextPattern pattern)
    {
        var builder = new StringBuilder();
        ScreenPoint? anchor = null;
        foreach (var selection in pattern.GetSelection())
        {
            var text = selection.GetText(Math.Max(0, 16001 - builder.Length));
            if (string.IsNullOrEmpty(text)) continue;
            builder.Append(text);
            try
            {
                foreach (var rect in selection.GetBoundingRectangles())
                {
                    if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0
                        || !double.IsFinite(rect.Left) || !double.IsFinite(rect.Bottom)
                        || rect.Left < int.MinValue || rect.Left > int.MaxValue
                        || rect.Bottom < int.MinValue || rect.Bottom > int.MaxValue) continue;
                    int bottom = (int)Math.Ceiling(rect.Bottom);
                    int left = (int)Math.Floor(rect.Left);
                    if (anchor == null || bottom > anchor.Value.Y || bottom == anchor.Value.Y && left < anchor.Value.X)
                        anchor = new ScreenPoint { X = left, Y = bottom };
                }
            }
            catch (Exception ex) when (IsAutomationFailure(ex)) { }
            if (builder.Length >= 16001) break;
        }
        return new(builder.ToString(), anchor);
    }

    private static bool IsAutomationFailure(Exception ex) => ex is InvalidOperationException or NotSupportedException
        or ElementNotAvailableException or System.Runtime.InteropServices.COMException or ArgumentException;
}
