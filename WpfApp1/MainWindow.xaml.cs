using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
namespace WpfApp1;

public partial class MainWindow : Window
{
    private CancellationTokenSource? pending;
    private CancellationTokenSource? preload;
    private CancellationTokenSource? captureRequest;
    private bool shuttingDown;
    private bool initialLoad = true;
    private bool exitRequested;
    private ClosePrompt? closePrompt;
    internal bool IsDarkMode => DarkModeCheck.IsChecked == true;
    private int generation;
    private bool capturing;
    private System.Windows.Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private HwndSource? source;
    private SelectionMonitor? selectionMonitor;
    private SelectionPopup? selectionPopup;
    public MainWindow()
    {
        Appearance.Apply(false, 19);
        InitializeComponent();
        try
        {
            var s = Settings.Load();
            DarkModeCheck.IsChecked = s.DarkMode;
            TextSizeSlider.Value = double.IsFinite(s.ReadingFontSize) ? Math.Clamp(s.ReadingFontSize, 12, 30) : 19;
            Appearance.Apply(s.DarkMode, TextSizeSlider.Value);
            ApiUrlBox.Text = s.ApiUrl; ModelBox.Text = s.Model; KeyBox.Password = s.ApiKey;
            OnlineUrlBox.Text = s.OnlineUrl; OnlineKeyBox.Password = s.OnlineKey;
            PythonBox.Text = s.Python; OcrBox.Text = s.Tesseract; OcrLanguagesBox.Text = s.OcrLanguages;
            OcrEngineBox.SelectedIndex = Math.Clamp(s.OcrEngine, 0, 2); OcrPythonBox.Text = s.OcrPython;
            OcrDeviceModeBox.SelectedIndex = Math.Clamp((int)s.OcrDeviceMode, 0, 2); OcrGpuPythonBox.Text = s.OcrGpuPython;
            SelectionKeyBox.Text = s.SelectionKey; CaptureKeyBox.Text = s.CaptureKey;
            LlamaServerBox.Text = s.LlamaServer; LocalModelBox.Text = s.LocalModel;
            CpuLlamaServerBox.Text = s.CpuLlamaServer;
            DeviceModeBox.SelectedIndex = (int)s.EffectiveDeviceMode; IdleMinutesBox.Text = s.IdleMinutes.ToString();
            AutoPopupCheck.IsChecked = s.AutoSelectionPopup;
            ClipboardFallbackCheck.IsChecked = s.ClipboardSelectionFallback;
            EngineBox.SelectedIndex = Math.Clamp(s.Engine, 0, 3); OfflineCheck.IsChecked = s.Offline;
        }
        catch (Exception) { StatusText.Text = "设置读取失败，请重新配置并保存。"; }
        RefreshStartupState();
        SourceInitialized += (_, _) => InitializeNative();
        EngineBox.SelectionChanged += Engine_Changed;
        DeviceModeBox.SelectionChanged += DeviceMode_Changed;
        DarkModeCheck.Click += (_, _) => UpdateAppearance();
        TextSizeSlider.ValueChanged += (_, _) => UpdateAppearance();
        LocalLlm.ResidencyChanged += ModelResidencyChanged;
        Loaded += async (_, _) =>
        {
            if (initialLoad)
            {
                initialLoad = false;
                if (StartupRegistration.LaunchedAtLogin) Hide();
            }
            await PreloadSelectedModel();
        };
        Closed += (_, _) => { shuttingDown = true; captureRequest?.Cancel(); preload?.Cancel(); LocalLlm.ResidencyChanged -= ModelResidencyChanged; selectionMonitor?.Dispose(); selectionPopup?.Close(); pending?.Cancel(); LocalLlm.Shutdown(); tray?.Dispose(); trayIcon?.Dispose(); source?.RemoveHook(WindowHook); Native.Unregister(new WindowInteropHelper(this).Handle); };
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!exitRequested)
        {
            if (closePrompt != null) { e.Cancel = true; return; }
            var prompt = new ClosePrompt { Owner = this };
            closePrompt = prompt;
            try { prompt.ShowDialog(); }
            finally { closePrompt = null; }
            if (!exitRequested)
            {
                if (prompt.Decision == CloseDecision.Exit) exitRequested = true;
                else
                {
                    e.Cancel = true;
                    if (prompt.Decision == CloseDecision.Minimize)
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!shuttingDown)
                            {
                                ShowInTaskbar = true;
                                WindowState = WindowState.Minimized;
                            }
                        }));
                }
            }
        }
        base.OnClosing(e);
    }
    private void RequestExit()
    {
        exitRequested = true;
        if (closePrompt != null) closePrompt.Close();
        else Close();
    }
    internal void AllowSystemExit()
    {
        exitRequested = true;
        closePrompt?.Close();
    }
    private void RefreshStartupState()
    {
        try
        {
            StartupCheck.IsChecked = StartupRegistration.IsEnabled();
            StartupStatusText.Text = (StartupCheck.IsChecked == true ? "已登记当前用户的自启动项。" : "自启动未开启。")
                + "登录时收起到托盘，双击托盘图标打开；切换立即生效。移动程序后请重新关闭再开启此选项。";
        }
        catch (Exception ex)
        {
            StartupCheck.IsChecked = false;
            StartupStatusText.Text = "无法读取启动项：" + ex.Message;
        }
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = StartupCheck.IsChecked == true;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            RefreshStartupState();
            StatusText.Text = enabled ? "已开启开机自启动，下次登录 Windows 时生效。" : "已关闭开机自启动。";
        }
        catch (Exception ex)
        {
            RefreshStartupState();
            StatusText.Text = "修改自启动失败：" + ex.Message;
        }
    }
    private void InitializeNative()
    {
        Appearance.UpdateTitleBar(this, DarkModeCheck.IsChecked == true);
        var handle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(handle); source?.AddHook(WindowHook);
        StatusText.Text = Native.Register(handle, SelectionKeyBox.Text, CaptureKeyBox.Text);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开 Glimpse", null, (_, _) => Dispatcher.Invoke(Reveal));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(RequestExit));
        using (var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/glimpse.ico")).Stream)
        using (var resourceIcon = new System.Drawing.Icon(iconStream, System.Windows.Forms.SystemInformation.SmallIconSize))
            trayIcon = (System.Drawing.Icon)resourceIcon.Clone();
        tray = new System.Windows.Forms.NotifyIcon { Icon = trayIcon, Text = "Glimpse", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(Reveal);
        try
        {
            selectionMonitor = new SelectionMonitor(Dispatcher)
            {
                Enabled = AutoPopupCheck.IsChecked == true,
                UseClipboardFallback = ClipboardFallbackCheck.IsChecked == true
            };
            selectionMonitor.IsPopupPoint = point => selectionPopup?.ContainsAtPointerDown(point) == true;
            selectionMonitor.SelectionReady += (text, point, pointerDownAt) =>
            {
                if (!capturing && (selectionPopup?.CanReplaceSelection(pointerDownAt) ?? true)) PresentSelection(text, point);
            };
            selectionMonitor.PointerPressed += (target, pointerDownAt, insidePopup) =>
            {
                if (!insidePopup && target != IntPtr.Zero && selectionPopup is { IsVisible: true }
                    && !selectionPopup.OwnsWindow(target)) selectionPopup.DismissFromOutside(pointerDownAt);
            };
        }
        catch (System.ComponentModel.Win32Exception ex) { StatusText.Text = ex.Message; }
    }
    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x0312) { handled = true; if (w.ToInt32() == 1) _ = CaptureSelection(); else _ = CaptureRegion(); }
        return IntPtr.Zero;
    }
    private void Reveal() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void Pin_Click(object sender, RoutedEventArgs e) => Topmost = PinCheck.IsChecked == true;
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Cancel_Click(object sender, RoutedEventArgs e) { generation++; pending?.Cancel(); captureRequest?.Cancel(); TranslateButton.IsEnabled = true; StatusText.Text = "已取消。"; }
    private void Paste_Click(object sender, RoutedEventArgs e) { try { if (Clipboard.ContainsText()) SourceText.Text = Clipboard.GetText(); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void Copy_Click(object sender, RoutedEventArgs e) { try { if (ResultText.Text.Length > 0) Clipboard.SetText(ResultText.Text); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void ClearText_Click(object sender, RoutedEventArgs e)
    {
        generation++; pending?.Cancel(); captureRequest?.Cancel(); TranslateButton.IsEnabled = true;
        SourceText.Clear(); ResultText.Clear(); SourceText.Focus();
        StatusText.Text = "已清空原文和译文。"; StatusText.ToolTip = null;
    }
    private void SmallerText_Click(object sender, RoutedEventArgs e) => TextSizeSlider.Value = Math.Max(TextSizeSlider.Minimum, TextSizeSlider.Value - 1);
    private void LargerText_Click(object sender, RoutedEventArgs e) => TextSizeSlider.Value = Math.Min(TextSizeSlider.Maximum, TextSizeSlider.Value + 1);
    private void UpdateAppearance()
    {
        bool dark = DarkModeCheck.IsChecked == true;
        Appearance.Apply(dark, TextSizeSlider.Value);
        Appearance.UpdateTitleBar(this, dark);
        try
        {
            var saved = Settings.Load();
            saved.DarkMode = dark; saved.ReadingFontSize = TextSizeSlider.Value; saved.Save();
        }
        catch (Exception ex) { StatusText.Text = "外观已应用，保存失败：" + ex.Message; }
    }
    private async void Translate_Click(object sender, RoutedEventArgs e) => await Translate();
    private async void Capture_Click(object sender, RoutedEventArgs e) => await CaptureRegion();
    private Settings ReadSettings() => new()
    {
        ApiUrl = ApiUrlBox.Text.Trim(),
        Model = ModelBox.Text.Trim(),
        ApiKey = KeyBox.Password,
        OnlineUrl = OnlineUrlBox.Text.Trim(),
        OnlineKey = OnlineKeyBox.Password,
        Python = PythonBox.Text.Trim(),
        Tesseract = OcrBox.Text.Trim(),
        OcrLanguages = OcrLanguagesBox.Text.Trim(),
        OcrEngine = OcrEngineBox.SelectedIndex,
        OcrPython = OcrPythonBox.Text.Trim(),
        OcrGpuPython = OcrGpuPythonBox.Text.Trim(),
        OcrDeviceMode = (LocalDeviceMode)Math.Clamp(OcrDeviceModeBox.SelectedIndex, 0, 2),
        Engine = EngineBox.SelectedIndex,
        Offline = OfflineCheck.IsChecked == true,
        SelectionKey = SelectionKeyBox.Text.Trim().ToUpperInvariant(),
        CaptureKey = CaptureKeyBox.Text.Trim().ToUpperInvariant(),
        LlamaServer = LlamaServerBox.Text.Trim(),
        CpuLlamaServer = CpuLlamaServerBox.Text.Trim(),
        LocalModel = LocalModelBox.Text.Trim(),
        DeviceMode = (LocalDeviceMode)Math.Clamp(DeviceModeBox.SelectedIndex, 0, 2),
        UseGpu = DeviceModeBox.SelectedIndex != 2,
        IdleMinutes = int.TryParse(IdleMinutesBox.Text, out var idle) ? Math.Clamp(idle, 0, 60) : 5,
        AutoSelectionPopup = AutoPopupCheck.IsChecked == true,
        ClipboardSelectionFallback = ClipboardFallbackCheck.IsChecked == true,
        DarkMode = DarkModeCheck.IsChecked == true,
        ReadingFontSize = TextSizeSlider.Value
    };
    private async void DeviceMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || DeviceModeBox.SelectedIndex < 0) return;
        try
        {
            // Persist only this switch, leaving other unsaved form fields alone.
            var saved = Settings.Load();
            saved.DeviceMode = (LocalDeviceMode)DeviceModeBox.SelectedIndex;
            saved.UseGpu = DeviceModeBox.SelectedIndex != 2;
            saved.Save();
            preload?.Cancel();
            if (EngineBox.SelectedIndex != 3)
            {
                StatusText.Text = "本地运行模式已保存，选择 Hy-MT2 时生效。";
                return;
            }
            selectionPopup?.Dismiss(force: true);
            generation++; pending?.Cancel(); TranslateButton.IsEnabled = true;
            StatusText.Text = "运行模式已保存，正在切换模型设备…";
            await LocalLlm.UpdatePolicy(ReadSettings());
            await PreloadSelectedModel();
        }
        catch (Exception ex) { StatusText.Text = "切换运行模式失败：" + ex.Message; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            preload?.Cancel();
            var s = ReadSettings(); s.Save(); await LocalLlm.UpdatePolicy(s);
            StatusText.Text = "设置已保存。" + Native.Register(new WindowInteropHelper(this).Handle, s.SelectionKey, s.CaptureKey);
            await PreloadSelectedModel();
        }
        catch (Exception ex) { StatusText.Text = "保存失败：" + ex.Message; }
    }
    private void ModelResidencyChanged(string message)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!Dispatcher.HasShutdownStarted) { StatusText.Text = message; StatusText.ToolTip = LocalLlm.GpuFailureDetails; } }));
    }
    private async Task PreloadSelectedModel()
    {
        preload?.Cancel();
        if (EngineBox.SelectedIndex != 3 || Dispatcher.HasShutdownStarted) return;
        using var loading = new CancellationTokenSource();
        preload = loading;
        try { await LocalLlm.Preload(ReadSettings(), loading.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!loading.IsCancellationRequested && !Dispatcher.HasShutdownStarted)
            {
                StatusText.Text = "模型预加载失败：" + ex.Message;
                StatusText.ToolTip = LocalLlm.GpuFailureDetails;
            }
        }
        finally { if (ReferenceEquals(preload, loading)) preload = null; }
    }
    private async Task Translate()
    {
        if (string.IsNullOrWhiteSpace(SourceText.Text)) { StatusText.Text = "请先输入、粘贴或获取文字。"; return; }
        pending?.Cancel(); pending = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        int current = ++generation; var token = pending.Token; var s = ReadSettings(); var text = SourceText.Text;
        var from = ((ComboBoxItem)SourceBox.SelectedItem).Tag.ToString()!;
        var to = ((ComboBoxItem)TargetBox.SelectedItem).Tag.ToString()!;
        TranslateButton.IsEnabled = false; ResultText.Clear(); StatusText.Text = "正在翻译…"; StatusText.ToolTip = null; var watch = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<string>(message => { if (current == generation) { StatusText.Text = message; StatusText.ToolTip = s.Engine == 3 ? LocalLlm.GpuFailureDetails : null; } });
            var result = await Translation.Translate(text, from, to, s, token, progress);
            if (current != generation) return;
            StatusText.ToolTip = s.Engine == 3 ? LocalLlm.GpuFailureDetails : null;
            ResultText.Text = result; StatusText.Text = $"完成 · {watch.Elapsed.TotalSeconds:F1} 秒 · {((ComboBoxItem)EngineBox.Items[s.Engine]).Content}";
            if (s.Engine == 3 && LocalLlm.LastTiming is { } timing)
                StatusText.Text = $"完成 · {timing.Backend} · 加载 {timing.LoadSeconds:F1} 秒 · 翻译 {timing.TranslationSeconds:F1} 秒 · Hy-MT2 · "
                    + (timing.Backend == "CPU" || s.IdleMinutes == 0 ? "保持加载，可手动卸载" : $"保持加载；仅显存紧张且闲置 {s.IdleMinutes} 分钟时卸载")
                    + (timing.RouteNote.Length > 0 ? $" · {timing.RouteNote}" : "");
        }
        catch (OperationCanceledException) { if (current == generation) StatusText.Text = "已取消或请求超时。"; }
        catch (Exception ex) { if (current == generation) StatusText.Text = "翻译失败：" + ex.Message; }
        finally { if (current == generation) TranslateButton.IsEnabled = true; }
    }
    private async Task CaptureSelection()
    {
        Native.GetCursorPos(out var point);
        var foreground = Native.GetForegroundWindow();
        try
        {
            Native.TextSelection selection;
            try { selection = await Task.Run(() => Native.ReadSelectionAt(point, foreground)).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) when (ClipboardFallbackCheck.IsChecked == true) { selection = new("", null); }
            if (ClipboardFallbackCheck.IsChecked == true && selection.AllowCopy && string.IsNullOrWhiteSpace(selection.Text))
                selection = await ClipboardSelection.Read(point, foreground, () => !shuttingDown && Native.GetForegroundWindow() == foreground);
            if (Native.GetForegroundWindow() != foreground) return;
            if (string.IsNullOrWhiteSpace(selection.Text))
            {
                PresentSelection("", point, "未能读取选中文字，请确认选区可复制，或使用框选识字。不会自动翻译。");
                return;
            }
            PresentSelection(selection.Text, selection.Anchor ?? point);
        }
        catch (Exception ex) { PresentSelection("", point, "取词失败：" + ex.Message); }
    }
    private void PresentSelection(string text, Native.ScreenPoint point, string? message = null)
    {
        selectionPopup ??= new SelectionPopup();
        var from = ((ComboBoxItem)SourceBox.SelectedItem).Tag.ToString()!;
        var to = ((ComboBoxItem)TargetBox.SelectedItem).Tag.ToString()!;
        string engine = ((ComboBoxItem)EngineBox.SelectedItem).Content.ToString()!;
        selectionPopup.ShowSelection(text, point, ReadSettings(), from, to, engine, message);
    }
    private void AutoPopup_Click(object sender, RoutedEventArgs e)
    {
        if (selectionMonitor != null) selectionMonitor.Enabled = AutoPopupCheck.IsChecked == true;
        if (AutoPopupCheck.IsChecked != true) selectionPopup?.Dismiss(force: true);
    }
    private void ClipboardFallback_Click(object sender, RoutedEventArgs e)
    {
        if (selectionMonitor != null) selectionMonitor.UseClipboardFallback = ClipboardFallbackCheck.IsChecked == true;
    }
    private async Task CaptureRegion()
    {
        if (capturing) return; capturing = true; string? image = null;
        OcrSession? ocr = null;
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        captureRequest = request;
        int current = ++generation; pending?.Cancel();
        TranslateButton.IsEnabled = false;
        try
        {
            var settings = ReadSettings();
            Hide(); await Task.Delay(180, request.Token);
            // Initialize alongside the user's selection, without waiting before showing the overlay.
            if (settings.OcrEngine == 0) ocr = new OcrSession(settings, request.Token);
            var overlay = new CaptureOverlay();
            if (overlay.ShowDialog() != true) { StatusText.Text = "已取消框选。"; return; }
            image = overlay.ImagePath; Reveal(); StatusText.Text = "正在本机识别文字 · "
                + (settings.OcrEngine switch { 0 => "RapidOCR…", 1 => "Windows OCR…", _ => "Tesseract…" });
            var progress = new Progress<string>(message => { if (!shuttingDown && current == generation) StatusText.Text = message; });
            var text = ocr != null ? await ocr.Recognize(image!, request.Token, progress)
                : await LocalTools.Ocr(image!, settings, request.Token);
            string ocrStatus = ocr != null ? $" · OCR {ocr.Backend}" + (ocr.Note.Length > 0 ? "（回退 CPU）" : "") : "";
            string ocrNote = ocr?.Note ?? "";
            ocr?.Dispose(); ocr = null; // Release OCR RAM/VRAM before translation begins.
            if (shuttingDown || current != generation || request.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(text)) { StatusText.Text = "没有识别到文字，请重新框选清晰的文字区域或检查 OCR 语言。"; return; }
            SourceText.Text = text;
            await Translate();
            if (!shuttingDown && !request.IsCancellationRequested && generation == current + 1)
            {
                StatusText.Text += ocrStatus;
                if (ocrNote.Length > 0) StatusText.ToolTip = (StatusText.ToolTip as string ?? "") + "\nOCR：" + ocrNote;
            }
        }
        catch (OperationCanceledException) { if (!shuttingDown && current == generation) StatusText.Text = "识字已取消或超时。"; }
        catch (Exception ex) { if (!shuttingDown && current == generation) StatusText.Text = "截图识字失败：" + ex.Message; }
        finally
        {
            ocr?.Dispose();
            if (image != null) try { File.Delete(image); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (ReferenceEquals(captureRequest, request)) captureRequest = null;
            capturing = false;
            if (!shuttingDown) { TranslateButton.IsEnabled = true; Reveal(); }
        }
    }
    private async void CheckLocal_Click(object sender, RoutedEventArgs e)
    {
        try { StatusText.Text = "正在检查…"; StatusText.Text = await LocalTools.Worker(ReadSettings(), "check", null, CancellationToken.None); }
        catch (Exception ex) { StatusText.Text = "检查失败：" + ex.Message; }
    }
    private async void Engine_Changed(object sender, SelectionChangedEventArgs e)
    {
        selectionPopup?.Dismiss(force: true);
        preload?.Cancel();
        int current = ++generation; pending?.Cancel(); TranslateButton.IsEnabled = true;
        if (EngineBox.SelectedIndex == 3) { await PreloadSelectedModel(); return; }
        await LocalLlm.Unload();
        if (current == generation) StatusText.Text = "已切换引擎；Hy-MT2 如已加载，则已释放。";
    }
    private async void UnloadLocal_Click(object sender, RoutedEventArgs e)
    {
        preload?.Cancel();
        selectionPopup?.Dismiss(force: true);
        int current = ++generation; pending?.Cancel(); TranslateButton.IsEnabled = true;
        await LocalLlm.Unload();
        if (current == generation) StatusText.Text = "Hy-MT2 已卸载，模型文件保留；下次翻译时重新加载。";
    }
    private async void ImportModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Argos Translate 模型|*.argosmodel" };
        if (dialog.ShowDialog() != true) return;
        try { StatusText.Text = "正在导入…"; StatusText.Text = await LocalTools.Worker(ReadSettings(), "install", dialog.FileName, CancellationToken.None); }
        catch (Exception ex) { StatusText.Text = "导入失败：" + ex.Message; }
    }
}
