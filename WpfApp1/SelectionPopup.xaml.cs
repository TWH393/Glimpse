using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WpfApp1;

public partial class SelectionPopup : Window
{
    private Settings configuration = new();
    private string sourceLanguage = "en", targetLanguage = "zh", engineName = "";
    private string selectedText = "";
    private CancellationTokenSource? pending;
    private int revision;
    private Native.ScreenPoint anchor;
    private bool translating;
    private long dismissNotBefore;
    private HwndSource? source;
    private readonly Queue<string> diagnostics = new();
    public SelectionPopup()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(PopupWindowHook);
        };
        Closed += (_, _) => { revision++; pending?.Cancel(); pending?.Dispose(); source?.RemoveHook(PopupWindowHook); };
        SizeChanged += (_, _) => { if (IsVisible) Constrain(); };
    }

    internal bool IsPinned => IsVisible && PinPopup.IsChecked == true;
    internal bool OwnsWindow(IntPtr handle) => handle != IntPtr.Zero && handle == new WindowInteropHelper(this).Handle;
    internal bool ContainsAtPointerDown(Native.ScreenPoint point)
    {
        if (!IsVisible) return false;
        var handle = new WindowInteropHelper(this).Handle;
        return handle != IntPtr.Zero && Native.GetWindowRect(handle, out var rect)
            && point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    private IntPtr PopupWindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021 && LauncherButton.Visibility == Visibility.Visible) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE: deliver the click without activating over the browser.
        }
        return IntPtr.Zero;
    }
    // Compare the original click time, so delayed hook callbacks from the
    // protected interval cannot dismiss the result after that interval ends.
    private bool CanDismissAt(long pointerDownAt) => !IsPinned && !translating && pointerDownAt >= dismissNotBefore;
    internal bool CanReplaceSelection(long pointerDownAt) => !IsVisible || CanDismissAt(pointerDownAt);
    internal void DismissFromOutside(long pointerDownAt)
    {
        if (CanDismissAt(pointerDownAt)) Dismiss();
    }

    internal void ShowSelection(string text, Native.ScreenPoint point, Settings settings, string from, string to, string engine, string? message = null)
    {
        revision++; pending?.Cancel();
        translating = false; dismissNotBefore = 0;
        configuration = settings; sourceLanguage = from; targetLanguage = to; engineName = engine; anchor = point;
        selectedText = text; TranslatedText.Clear(); PinPopup.IsChecked = false;
        SetExpanded(message != null);
        LauncherButton.IsEnabled = !string.IsNullOrWhiteSpace(text);
        PopupTranslate.Content = "翻译 →"; PopupTranslate.IsEnabled = !string.IsNullOrWhiteSpace(text); PopupCopy.IsEnabled = false; PopupCancel.Visibility = Visibility.Collapsed;
        PopupStatus.Text = message ?? "";
        PopupStatus.ToolTip = null;
        new WindowInteropHelper(this).EnsureHandle();
        Show(); UpdateLayout(); PositionAtAnchor();
        Trace("selection-shown");
    }

    private void SetExpanded(bool expanded)
    {
        SizeToContent = SizeToContent.Height;
        MinHeight = expanded ? 180 : 0;
        Width = expanded ? 390 : 118;
        PopupSurface.Padding = expanded ? new Thickness(17, 14, 17, 14) : new Thickness(5);
        PopupSurface.CornerRadius = new CornerRadius(expanded ? 15 : 12);
        LauncherButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PositionAtAnchor()
    {
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), anchor.X - 9, anchor.Y + 4, 0, 0, 0x0011);
        Constrain();
    }

    internal void Dismiss(bool force = false)
    {
        if (!force && !CanDismissAt(Stopwatch.GetTimestamp())) return;
        Trace(force ? "close-explicit" : "close-outside");
        revision++; pending?.Cancel(); selectedText = ""; TranslatedText.Clear(); Hide();
        translating = false; dismissNotBefore = 0;
    }

    private void Constrain()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        Native.GetWindowRect(handle, out var rect);
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(anchor.X, anchor.Y)).WorkingArea;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        int x = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - width));
        int y = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - height));
        if (x != rect.Left || y != rect.Top) Native.SetWindowPos(handle, new IntPtr(-1), x, y, 0, 0, 0x0011);
    }

    private void Launcher_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Translate_Click(sender, e);
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        Trace("translate-pressed");
        if (translating || string.IsNullOrWhiteSpace(selectedText)) return;
        pending?.Cancel(); pending?.Dispose(); pending = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        int current = ++revision; var token = pending.Token;
        translating = true; dismissNotBefore = 0;
        string text = selectedText;
        var watch = Stopwatch.StartNew();
        try
        {
            SetExpanded(true);
            PopupTranslate.IsEnabled = false; PopupCancel.Visibility = Visibility.Visible; PopupCopy.IsEnabled = false;
            TranslatedText.Clear(); PopupStatus.Text = "正在翻译…";
            UpdateLayout(); PositionAtAnchor();
            Trace("card-expanded");
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Render);
            if (current != revision || token.IsCancellationRequested) return;
            var progress = new Progress<string>(message => { if (current == revision) { PopupStatus.Text = message; PopupStatus.ToolTip = configuration.Engine == 3 ? LocalLlm.GpuFailureDetails : null; } });
            string result = await Translation.Translate(text, sourceLanguage, targetLanguage, configuration, token, progress);
            if (current != revision) return;
            PopupStatus.ToolTip = configuration.Engine == 3 ? LocalLlm.GpuFailureDetails : null;
            TranslatedText.Text = result; PopupCopy.IsEnabled = true;
            Trace("translation-displayed");
            PopupTranslate.Content = "重新翻译";
            PopupStatus.Text = $"{engineName} · {watch.Elapsed.TotalSeconds:F1} 秒";
            if (configuration.Engine == 3 && LocalLlm.LastTiming is { } timing)
                PopupStatus.Text = $"Hy-MT2 · {timing.Backend} · 加载 {timing.LoadSeconds:F1} 秒 · 翻译 {timing.TranslationSeconds:F1} 秒"
                    + (timing.RouteNote.Length > 0 ? $" · {timing.RouteNote}" : "");
        }
        catch (OperationCanceledException) { if (current == revision) PopupStatus.Text = "已取消或请求超时。"; }
        catch (Exception ex) { Trace("error-" + ex.GetType().Name); if (current == revision) PopupStatus.Text = "翻译失败：" + ex.Message; }
        finally
        {
            if (current == revision)
            {
                translating = false;
                dismissNotBefore = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                PopupTranslate.IsEnabled = true; PopupCancel.Visibility = Visibility.Collapsed;
            }
        }
    }
    private void Trace(string phase)
    {
        // UI state only: never record selected text, translations, keys or prompts.
        try
        {
            diagnostics.Enqueue($"{DateTimeOffset.Now:O} {phase} revision={revision} visible={IsVisible} expanded={ExpandedPanel.Visibility == Visibility.Visible} size={ActualWidth:F0}x{ActualHeight:F0}");
            while (diagnostics.Count > 60) diagnostics.Dequeue();
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimpse");
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "selection-popup.log"), diagnostics);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        revision++; pending?.Cancel(); PopupTranslate.IsEnabled = true; PopupCancel.Visibility = Visibility.Collapsed; PopupStatus.Text = "已取消。";
        translating = false; dismissNotBefore = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Dismiss(force: true);
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { if (TranslatedText.Text.Length > 0) { Clipboard.SetText(TranslatedText.Text); PopupStatus.Text = "译文已复制。"; } }
        catch (Exception ex) { PopupStatus.Text = "复制失败：" + ex.Message; }
    }
    private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Dismiss(force: true); }
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBlock && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
