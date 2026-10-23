using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace WpfApp1;

public partial class CaptionOverlay : Window
{
    private readonly Queue<(string English, string Chinese)> lines = new();
    private (int Id, string English, string Chinese)? preview;
    private bool bilingual, terminating;
    private HwndSource? source;
    public bool Locked { get; private set; }
    public bool HotkeyAvailable { get; private set; }
    public CaptionOverlay()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle); source?.AddHook(Hook);
            HotkeyAvailable = RegisterHotKey(handle, 30, 0x4003, 0x4C);
            ApplyLock();
        };
        Closing += (_, e) => { if (!terminating) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { UnregisterHotKey(new WindowInteropHelper(this).Handle, 30); source?.RemoveHook(Hook); };
    }
    internal void Add(CaptionEntry entry)
    {
        if (preview?.Id == entry.Id) preview = null;
        lines.Enqueue((entry.English, entry.Chinese ?? ""));
        while (lines.Count > 2) lines.Dequeue();
        Render();
    }
    internal void UpdatePreview(CaptionEntry entry, string text)
    { preview = (entry.Id, entry.English, text); Render(); }
    public void ClearPreview() { preview = null; Render(); }
    public void Reset() { lines.Clear(); preview = null; SubtitleText.Text = "等待中文字幕…"; }
    public void SetOptions(bool showEnglish, double fontSize, double opacity)
    {
        bilingual = showEnglish;
        SubtitleText.FontSize = Math.Clamp(fontSize, 16, 60);
        SubtitleSurface.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(opacity, 0, 0.85) * 255), 0, 0, 0));
        Render();
    }
    private void Render()
    {
        var text = new List<string>();
        foreach (var line in lines) text.Add(bilingual ? line.English + "\n" + line.Chinese : line.Chinese);
        if (preview is { } current) text.Add((bilingual ? current.English + "\n" : "") + current.Chinese + " ▌");
        SubtitleText.Text = text.Count == 0 ? "等待中文字幕…" : string.Join("\n\n", text.TakeLast(2));
    }
    public void ToggleLock() { Locked = !Locked; ApplyLock(); }
    private void ApplyLock()
    {
        Controls.Visibility = Locked ? Visibility.Collapsed : Visibility.Visible;
        ResizeMode = Locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr(Locked ? style | 0x20 | 0x08000000 : style & ~0x20 & ~0x08000000));
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x0312 && w.ToInt32() == 30) { ToggleLock(); handled = true; }
        return IntPtr.Zero;
    }
    private void DragHeader(object sender, MouseButtonEventArgs e) { if (!Locked && e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Lock_Click(object sender, RoutedEventArgs e) => ToggleLock();
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    public void Terminate() { terminating = true; Close(); }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
