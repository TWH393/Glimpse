using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WpfApp1;

public partial class SelectionPopup : Window
{
    private Settings configuration = new();
    private string sourceLanguage = "en", targetLanguage = "zh", engineName = "";
    private CancellationTokenSource? pending;
    private int revision;
    private Native.ScreenPoint anchor;
    internal SelectionPopup()
    {
        InitializeComponent();
        Closed += (_, _) => { revision++; pending?.Cancel(); pending?.Dispose(); };
        SizeChanged += (_, _) => { if (IsVisible) Constrain(); };
    }

    internal bool IsPinned => IsVisible && PinPopup.IsChecked == true;

    internal void ShowSelection(string text, Native.ScreenPoint point, Settings settings, string from, string to, string engine, string? message = null)
    {
        revision++; pending?.Cancel();
        configuration = settings; sourceLanguage = from; targetLanguage = to; engineName = engine; anchor = point;
        SelectedText.Text = text; TranslatedText.Clear(); TranslationPanel.Visibility = Visibility.Collapsed;
        PopupTranslate.Content = "翻译 →"; PopupTranslate.IsEnabled = !string.IsNullOrWhiteSpace(text); PopupCopy.IsEnabled = false; PopupCancel.Visibility = Visibility.Collapsed;
        PopupStatus.Text = message ?? $"{engineName} · 点击翻译后显示译文";
        PopupStatus.ToolTip = null;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(handle, new IntPtr(-1), point.X + 12, point.Y + 18, 0, 0, 0x0011);
        Show(); UpdateLayout(); Constrain();
    }

    internal bool Contains(Native.ScreenPoint point)
    {
        if (!IsVisible) return false;
        Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect);
        return point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    internal void Dismiss(bool force = false)
    {
        if (!force && PinPopup.IsChecked == true) return;
        revision++; pending?.Cancel(); Hide();
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

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        pending?.Cancel(); pending?.Dispose(); pending = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        int current = ++revision; var token = pending.Token;
        PopupTranslate.IsEnabled = false; PopupCancel.Visibility = Visibility.Visible; PopupCopy.IsEnabled = false;
        TranslatedText.Clear(); TranslationPanel.Visibility = Visibility.Collapsed; PopupStatus.Text = "正在翻译…";
        var watch = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<string>(message => { if (current == revision) { PopupStatus.Text = message; PopupStatus.ToolTip = configuration.Engine == 3 ? LocalLlm.GpuFailureDetails : null; } });
            string result = await Translation.Translate(SelectedText.Text, sourceLanguage, targetLanguage, configuration, token, progress);
            if (current != revision) return;
            PopupStatus.ToolTip = configuration.Engine == 3 ? LocalLlm.GpuFailureDetails : null;
            TranslatedText.Text = result; TranslationPanel.Visibility = Visibility.Visible; PopupCopy.IsEnabled = true;
            PopupTranslate.Content = "重新翻译";
            PopupStatus.Text = $"{engineName} · {watch.Elapsed.TotalSeconds:F1} 秒";
            if (configuration.Engine == 3 && LocalLlm.LastTiming is { } timing)
                PopupStatus.Text = $"Hy-MT2 · {timing.Backend} · 加载 {timing.LoadSeconds:F1} 秒 · 翻译 {timing.TranslationSeconds:F1} 秒"
                    + (timing.RouteNote.Length > 0 ? $" · {timing.RouteNote}" : "");
        }
        catch (OperationCanceledException) { if (current == revision) PopupStatus.Text = "已取消或请求超时。"; }
        catch (Exception ex) { if (current == revision) PopupStatus.Text = "翻译失败：" + ex.Message; }
        finally
        {
            if (current == revision) { PopupTranslate.IsEnabled = true; PopupCancel.Visibility = Visibility.Collapsed; }
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        revision++; pending?.Cancel(); PopupTranslate.IsEnabled = true; PopupCancel.Visibility = Visibility.Collapsed; PopupStatus.Text = "已取消。";
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
