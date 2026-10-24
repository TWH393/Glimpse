using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace WpfApp1;

public partial class CaptionOverlay : Window
{
    private readonly Queue<(string English, string Chinese)> lines = new();
    private (int Id, string English, string Chinese)? preview;
    private CaptionStyle appearance = CaptionStyle.FromSettings(new Settings());
    private bool terminating, ready, updatingStyle, positioning, dragging, resizing;
    private System.Drawing.Rectangle resizeArea;
    private Native.ScreenRect resizeStartRect;
    private Native.ScreenPoint resizeStartPointer;
    private HwndSource? source;
    public bool Locked { get; private set; }
    public bool HotkeyAvailable { get; private set; }
    internal event Action<CaptionStyle>? StyleChanged;
    internal event Action<double, double>? DimensionsChanged;
    public CaptionOverlay()
    {
        InitializeComponent();
        ready = true;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle); source?.AddHook(Hook);
            HotkeyAvailable = RegisterHotKey(handle, 30, 0x4003, 0x4C);
            ApplyLock();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) AppearancePopup.IsOpen = false; };
        Closing += (_, e) => { if (!terminating) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { UnregisterHotKey(new WindowInteropHelper(this).Handle, 30); source?.RemoveHook(Hook); };
    }
    internal void RestoreSize(double width, double height)
    {
        Width = double.IsFinite(width) ? Math.Clamp(width, 320, 7680) : 850;
        Height = double.IsFinite(height) ? Math.Clamp(height, 120, 4320) : 230;
    }
    public void ShowCentered()
    {
        if (IsVisible) return;
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        new WindowInteropHelper(this).EnsureHandle();
        Constrain(center: true, screen: screen);
        Show();
        // Moving between monitors can change DPI. Recenter after WPF has
        // applied the new scale, before the next interactive frame.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (IsVisible && !dragging && !resizing) Constrain(center: true, screen: screen);
        }));
    }
    public new void Hide()
    { AppearancePopup.IsOpen = false; RememberSize(); base.Hide(); }
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
    internal void SetOptions(CaptionStyle options)
    {
        appearance = options.Normalize();
        updatingStyle = true;
        try
        {
            SubtitleText.FontSize = appearance.FontSize;
            SubtitleText.Opacity = appearance.TextOpacity;
            var foreground = CaptionStyle.Parse(appearance.Foreground, Colors.White);
            var background = CaptionStyle.Parse(appearance.Background, Colors.Black);
            SubtitleText.Foreground = new SolidColorBrush(foreground);
            SubtitleSurface.Background = new SolidColorBrush(background) { Opacity = appearance.BackgroundOpacity };
            TextColorSwatch.Fill = new SolidColorBrush(foreground);
            BackgroundColorSwatch.Fill = new SolidColorBrush(background);
            OverlayFontSize.Value = appearance.FontSize;
            OverlayBackgroundOpacity.Value = appearance.BackgroundOpacity;
            OverlayTextOpacity.Value = appearance.TextOpacity;
        }
        finally { updatingStyle = false; }
        Render();
    }
    private void Render()
    {
        var text = new List<string>();
        foreach (var line in lines) text.Add(appearance.Bilingual ? line.English + "\n" + line.Chinese : line.Chinese);
        if (preview is { } current) text.Add((appearance.Bilingual ? current.English + "\n" : "") + current.Chinese + " ▌");
        SubtitleText.Text = text.Count == 0 ? "等待中文字幕…" : string.Join("\n\n", text.TakeLast(2));
    }
    private void ChangeStyle(CaptionStyle next)
    { SetOptions(next); StyleChanged?.Invoke(appearance); }
    private void Style_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || updatingStyle) return;
        ChangeStyle(appearance with { FontSize = OverlayFontSize.Value, BackgroundOpacity = OverlayBackgroundOpacity.Value,
            TextOpacity = OverlayTextOpacity.Value });
    }
    private void TextColor_Click(object sender, RoutedEventArgs e)
    { if (CaptionStyle.ChooseColor(this, appearance.Foreground) is { } color) ChangeStyle(appearance with { Foreground = color }); }
    private void BackgroundColor_Click(object sender, RoutedEventArgs e)
    { if (CaptionStyle.ChooseColor(this, appearance.Background) is { } color) ChangeStyle(appearance with { Background = color }); }
    private void Appearance_Click(object sender, RoutedEventArgs e) => AppearancePopup.IsOpen = !AppearancePopup.IsOpen;
    private void Center_Click(object sender, RoutedEventArgs e) => Constrain(center: true);
    public void ToggleLock() { Locked = !Locked; ApplyLock(); }
    private void ApplyLock()
    {
        AppearancePopup.IsOpen = false;
        Controls.Visibility = Locked ? Visibility.Collapsed : Visibility.Visible;
        ResizeHandles.Visibility = Locked ? Visibility.Collapsed : Visibility.Visible;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr(Locked ? style | 0x20 | 0x08000000 : style & ~0x20 & ~0x08000000));
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x0312 && w.ToInt32() == 30) { ToggleLock(); handled = true; }
        if ((message == 0x007E || message == 0x02E0) && !positioning && !dragging && !resizing)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (IsVisible) Constrain(); }));
        return IntPtr.Zero;
    }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (Locked || e.LeftButton != MouseButtonState.Pressed || e.ClickCount != 1) return;
        // The entire header is draggable, except its actual action buttons.
        for (DependencyObject? item = e.OriginalSource as DependencyObject; item != null;
            item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
            if (item is ButtonBase or Thumb) return;
        AppearancePopup.IsOpen = false;
        e.Handled = true;
        dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) when (Mouse.LeftButton != MouseButtonState.Pressed) { }
        finally { dragging = false; Constrain(); }
    }
    private void Constrain(bool center = false, Forms.Screen? screen = null)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || positioning || !Native.GetWindowRect(handle, out var rect)) return;
        positioning = true;
        try
        {
            var area = (screen ?? Forms.Screen.FromHandle(handle)).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            int maxWidth = Math.Max(1, area.Width - 24), maxHeight = Math.Max(1, area.Height - 24);
            MinWidth = Math.Min(320, maxWidth / dpi.DpiScaleX);
            MinHeight = Math.Min(120, maxHeight / dpi.DpiScaleY);
            int width = Math.Clamp(rect.Right - rect.Left, 1, maxWidth);
            int height = Math.Clamp(rect.Bottom - rect.Top, 1, maxHeight);
            int x = center ? area.Left + (area.Width - width) / 2 : Math.Clamp(rect.Left, area.Left + 12, area.Right - 12 - width);
            int y = center ? area.Bottom - height - Math.Min(48, Math.Max(12, area.Height / 15))
                : Math.Clamp(rect.Top, area.Top + 12, area.Bottom - 12 - height);
            y = Math.Clamp(y, area.Top + 12, area.Bottom - 12 - height);
            Native.SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x0014);
        }
        finally { positioning = false; }
    }
    private void Resize_Started(object sender, DragStartedEventArgs e)
    {
        if (Locked) return;
        Constrain();
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (!Native.GetWindowRect(handle, out resizeStartRect) || !Native.GetCursorPos(out resizeStartPointer)) return;
        resizeArea = Forms.Screen.FromHandle(handle).WorkingArea;
        resizing = true; AppearancePopup.IsOpen = false;
    }
    private void Resize_Delta(object sender, DragDeltaEventArgs e)
    {
        if (!resizing || Locked || sender is not Thumb thumb || thumb.Tag is not string edge) return;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (!Native.GetCursorPos(out var pointer)) return;
        var rect = resizeStartRect;
        var dpi = VisualTreeHelper.GetDpi(this);
        // Use total physical pointer travel from the starting rectangle.
        // Resizing also moves the Thumb; incremental local deltas can jitter.
        int dx = pointer.X - resizeStartPointer.X, dy = pointer.Y - resizeStartPointer.Y;
        int minWidth = Math.Max(1, (int)Math.Ceiling(MinWidth * dpi.DpiScaleX));
        int minHeight = Math.Max(1, (int)Math.Ceiling(MinHeight * dpi.DpiScaleY));
        int left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;
        if (edge.Contains("Left")) left = Math.Clamp(left + dx, resizeArea.Left + 12, Math.Max(resizeArea.Left + 12, right - minWidth));
        if (edge.Contains("Right")) right = Math.Clamp(right + dx, Math.Min(left + minWidth, resizeArea.Right - 12), resizeArea.Right - 12);
        if (edge.Contains("Top")) top = Math.Clamp(top + dy, resizeArea.Top + 12, Math.Max(resizeArea.Top + 12, bottom - minHeight));
        if (edge.Contains("Bottom")) bottom = Math.Clamp(bottom + dy, Math.Min(top + minHeight, resizeArea.Bottom - 12), resizeArea.Bottom - 12);
        Native.SetWindowPos(handle, IntPtr.Zero, left, top, right - left, bottom - top, 0x0014);
        e.Handled = true;
    }
    private void Resize_Completed(object sender, DragCompletedEventArgs e)
    { resizing = false; Constrain(); RememberSize(); }
    private void RememberSize()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && Native.GetWindowRect(handle, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            // Keep WPF's requested size aligned with the native resize result
            // so hiding/showing the same window cannot restore an older width.
            var dpi = VisualTreeHelper.GetDpi(this);
            double width = (rect.Right - rect.Left) / dpi.DpiScaleX, height = (rect.Bottom - rect.Top) / dpi.DpiScaleY;
            Width = width; Height = height;
            DimensionsChanged?.Invoke(width, height);
        }
    }
    internal void CaptureDimensions() => RememberSize();
    private void Lock_Click(object sender, RoutedEventArgs e) => ToggleLock();
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    public void Terminate() { RememberSize(); terminating = true; AppearancePopup.IsOpen = false; Close(); }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
