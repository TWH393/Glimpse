using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WpfApp1;

public partial class LiveCaptionsPanel : UserControl, IDisposable
{
    private LiveCaptionRun? session;
    private CaptionOverlay? overlay;
    private bool initialized, stopping, starting, disposed, refreshing;
    private string? lastTranscript;
    private string captionForeground = "#FFFFFF", captionBackground = "#000000";
    private double captionWindowWidth = 850, captionWindowHeight = 230;
    private readonly System.Windows.Threading.DispatcherTimer preferencesSave = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string sessionsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Glimpse", "Sessions");
    public Func<Settings>? TranslationSettings { get; set; }
    public bool Running => session != null;
    public LiveCaptionsPanel()
    {
        InitializeComponent();
        preferencesSave.Tick += (_, _) =>
        {
            preferencesSave.Stop();
            try { SavePreferences(); } catch (Exception ex) { SessionStatus.Text = "保存字幕设置失败：" + ex.Message; }
        };
        try
        {
            var s = Settings.Load();
            SpeechPythonBox.Text = s.SpeechPython; SpeechModelBox.Text = s.SpeechModel;
            SpeechDeviceBox.SelectedIndex = Math.Clamp((int)s.SpeechDeviceMode, 0, 2);
            GpuOnlyCheck.IsChecked = s.LiveGpuOnly;
            SpeechDeviceBox.IsEnabled = !s.LiveGpuOnly;
            ApplyCaptionStyle(CaptionStyle.FromSettings(s));
            captionWindowWidth = double.IsFinite(s.CaptionWindowWidth) ? Math.Clamp(s.CaptionWindowWidth, 320, 7680) : 850;
            captionWindowHeight = double.IsFinite(s.CaptionWindowHeight) ? Math.Clamp(s.CaptionWindowHeight, 120, 4320) : 230;
            if (!string.IsNullOrWhiteSpace(s.SpeechSessionsFolder)) sessionsFolder = s.SpeechSessionsFolder;
        }
        catch (Exception ex) { SessionStatus.Text = "读取字幕设置失败：" + ex.Message; }
        initialized = true;
        if (File.Exists(SpeechPythonBox.Text) && File.Exists(Path.Combine(SpeechModelBox.Text, "model.bin")))
            SessionStatus.Text = "已找到本地语音环境和英语模型；选择应用后开始字幕。";
        Loaded += async (_, _) => { if (ApplicationsBox.ItemsSource == null) await RefreshApplications(); };
    }
    public void CopyPreferences(Settings s)
    {
        s.SpeechPython = SpeechPythonBox.Text.Trim(); s.SpeechModel = SpeechModelBox.Text.Trim();
        s.SpeechDeviceMode = (LocalDeviceMode)Math.Clamp(SpeechDeviceBox.SelectedIndex, 0, 2);
        s.LiveGpuOnly = GpuOnlyCheck.IsChecked == true;
        ReadCaptionStyle().CopyTo(s);
        s.CaptionWindowWidth = captionWindowWidth; s.CaptionWindowHeight = captionWindowHeight;
        s.SpeechSessionsFolder = sessionsFolder;
    }
    private void SavePreferences() { preferencesSave.Stop(); var s = Settings.Load(); CopyPreferences(s); s.Save(); }
    private void QueueSavePreferences() { preferencesSave.Stop(); preferencesSave.Start(); }
    private CaptionStyle ReadCaptionStyle() => new CaptionStyle(BilingualCheck.IsChecked == true, CaptionSize.Value,
        CaptionOpacity.Value, CaptionTextOpacity.Value, captionForeground, captionBackground).Normalize();
    private void ApplyCaptionStyle(CaptionStyle style)
    {
        bool wasInitialized = initialized; initialized = false;
        try
        {
            BilingualCheck.IsChecked = style.Bilingual; CaptionSize.Value = style.FontSize;
            CaptionOpacity.Value = style.BackgroundOpacity; CaptionTextOpacity.Value = style.TextOpacity;
            captionForeground = style.Foreground; captionBackground = style.Background;
            CaptionTextColorSwatch.Fill = new System.Windows.Media.SolidColorBrush(CaptionStyle.Parse(captionForeground, System.Windows.Media.Colors.White));
            CaptionBackgroundColorSwatch.Fill = new System.Windows.Media.SolidColorBrush(CaptionStyle.Parse(captionBackground, System.Windows.Media.Colors.Black));
        }
        finally { initialized = wasInitialized; }
    }
    private async Task RefreshApplications()
    {
        if (refreshing || Running || disposed) return;
        refreshing = true;
        RefreshApplicationsButton.IsEnabled = false;
        int? selected = (ApplicationsBox.SelectedItem as AudioApplication)?.Id;
        var applications = new System.Collections.Generic.List<AudioApplication> { AudioApplication.GlobalOutput };
        try
        {
            applications.AddRange(await Task.Run(AudioApplications.Enumerate));
            if (disposed || Running) return;
            if (applications.Count == 1)
                SessionStatus.Text = "尚未发现应用音频会话。请先让应用播放声音再刷新，或主动选择全局识别。";
        }
        catch (Exception ex)
        {
            if (!disposed && !Running) SessionStatus.Text = "读取音频会话失败：" + ex.Message + "；仍可主动选择全局识别。";
        }
        finally
        {
            if (!disposed && !Running)
            {
                ApplicationsBox.ItemsSource = applications;
                // Never silently switch a disappeared application to global capture.
                ApplicationsBox.SelectedItem = applications.FirstOrDefault(item => item.Id == selected);
            }
            refreshing = false;
            RefreshApplicationsButton.IsEnabled = true;
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshApplications();
    private async void Applications_Opened(object sender, EventArgs e) => await RefreshApplications();
    private CaptionOverlay GetOverlay()
    {
        if (overlay == null)
        {
            overlay = new CaptionOverlay();
            overlay.RestoreSize(captionWindowWidth, captionWindowHeight);
            overlay.SetOptions(ReadCaptionStyle());
            overlay.StyleChanged += style =>
            {
                if (disposed) return;
                ApplyCaptionStyle(style); QueueSavePreferences();
            };
            overlay.DimensionsChanged += (width, height) =>
            {
                if (disposed) return;
                captionWindowWidth = width; captionWindowHeight = height; QueueSavePreferences();
            };
        }
        return overlay;
    }
    private void OnUi(Action action)
    { if (!disposed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if (!disposed) action(); })); }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (session != null || starting || disposed) return;
        if (refreshing) { SessionStatus.Text = "正在刷新音频应用，请稍候。"; return; }
        if (ApplicationsBox.SelectedItem is not AudioApplication application) { SessionStatus.Text = "请先选择一个音频应用，或选择全局识别。"; return; }
        starting = true; StartButton.IsEnabled = false; StopButton.IsEnabled = true;
        ApplicationsBox.IsEnabled = false;
        var configuration = TranslationSettings?.Invoke() ?? Settings.Load();
        CopyPreferences(configuration);
        var current = new LiveCaptionRun(configuration, application.Id, application.Name);
        session = current;
        try
        {
            SavePreferences(); SessionPreview.Clear(); StreamingText.Text = ""; TimingText.Text = "耗时：等待第一条字幕。"; DraftText.Text = "正在加载模型…";
            current.Status += text => OnUi(() => { if (session == current) SessionStatus.Text = text; });
            current.Draft += text => OnUi(() => { if (session == current) DraftText.Text = text; });
            current.Level += level => OnUi(() => { if (session == current) AudioLevel.Value = level; });
            current.Preview += (entry, text) => OnUi(() =>
            {
                if (session != current || current.Recovering) return;
                StreamingText.Text = "正在生成：" + text; GetOverlay().UpdatePreview(entry, text);
            });
            current.ResetPreview += () => OnUi(() => { if (session == current) { StreamingText.Text = ""; overlay?.ClearPreview(); } });
            current.Timing += metrics => OnUi(() => { if (session == current) TimingText.Text = metrics.ToString(); });
            current.Notice += text => OnUi(() =>
            {
                if (session != current) return;
                SessionPreview.AppendText(text + Environment.NewLine); SessionPreview.ScrollToEnd();
            });
            current.Caption += entry => OnUi(() =>
            {
                if (session != current) return;
                string text = $"[{TimeSpan.FromSeconds(entry.Start):hh\\:mm\\:ss}]\nEN: {entry.English}\nZH: {entry.Chinese}\n\n";
                SessionPreview.AppendText(text);
                if (SessionPreview.Text.Length > 12000) SessionPreview.Text = SessionPreview.Text[^10000..];
                SessionPreview.ScrollToEnd(); StreamingText.Text = ""; GetOverlay().Add(entry);
            });
            current.Failed += text => OnUi(async () =>
            {
                SessionStatus.Text = text;
                if (session == current && !starting) await StopSession();
            });
            await current.Start();
            if (session != current || stopping || disposed) return;
            lastTranscript = current.TranscriptPath;
            GetOverlay().Reset(); GetOverlay().ShowCentered(); PauseButton.IsEnabled = true;
            if (!overlay!.HotkeyAvailable) SessionStatus.Text += "；Ctrl+Alt+L 已被占用，请从托盘解锁字幕。";
        }
        catch (Exception ex)
        {
            SessionStatus.Text = "启动失败：" + ex.Message;
            try { await current.Stop(); }
            catch (Exception cleanupError) { SessionStatus.Text += "；清理失败：" + cleanupError.Message; }
            lastTranscript = current.TranscriptPath ?? lastTranscript;
            current.Dispose();
            if (session == current) session = null;
        }
        finally
        {
            starting = false;
            if (session == null) ResetControls();
        }
    }
    public async Task StopSession()
    {
        if (session == null || stopping) return;
        stopping = true; StopButton.IsEnabled = false; PauseButton.IsEnabled = false;
        var current = session;
        SessionStatus.Text = "正在停止采音，完成已确认片段并保存记录…";
        try
        {
            await current.Stop();
            lastTranscript = current.TranscriptPath ?? lastTranscript;
            SessionStatus.Text = (current.Failure.Length == 0 ? "会话已停止" : "会话已停止：" + current.Failure)
                + (lastTranscript == null ? "。" : current.Saved ? "；记录已保存：" + lastTranscript : "；保存未完成，请检查已有记录：" + lastTranscript);
        }
        catch (Exception ex) { SessionStatus.Text = "停止会话时发生错误：" + ex.Message; }
        finally
        {
            current.Dispose(); session = null; stopping = false;
            AudioLevel.Value = 0; DraftText.Text = "会话已结束"; overlay?.Hide(); ResetControls();
            try { await LocalLlm.UpdatePolicy(TranslationSettings?.Invoke() ?? Settings.Load()); }
            catch (Exception ex) { SessionStatus.Text += "；恢复模型设置失败：" + ex.Message; }
        }
    }
    private void ResetControls()
    { StartButton.IsEnabled = !starting; StopButton.IsEnabled = false; PauseButton.IsEnabled = false; PauseButton.Content = "暂停"; ApplicationsBox.IsEnabled = true; }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopSession();
    public void TogglePause()
    { session?.TogglePause(); PauseButton.Content = session?.Paused == true ? "继续" : "暂停"; }
    private void Pause_Click(object sender, RoutedEventArgs e) => TogglePause();
    public void ToggleOverlay() { var window = GetOverlay(); if (window.IsVisible) window.Hide(); else window.ShowCentered(); }
    public void ToggleLock() { GetOverlay().ShowCentered(); overlay!.ToggleLock(); }
    private void Overlay_Click(object sender, RoutedEventArgs e) => ToggleOverlay();
    private void Lock_Click(object sender, RoutedEventArgs e) => ToggleLock();
    private void Options_Click(object sender, RoutedEventArgs e) => OptionsChanged();
    private void GpuOnly_Click(object sender, RoutedEventArgs e)
    { SpeechDeviceBox.IsEnabled = GpuOnlyCheck.IsChecked != true; OptionsChanged(); }
    private void Options_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => OptionsChanged();
    private void OptionsChanged()
    {
        if (!initialized) return;
        overlay?.SetOptions(ReadCaptionStyle());
        QueueSavePreferences();
    }
    private void CaptionTextColor_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } owner && CaptionStyle.ChooseColor(owner, captionForeground) is { } color)
        { captionForeground = color; ApplyCaptionStyle(ReadCaptionStyle()); OptionsChanged(); }
    }
    private void CaptionBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } owner && CaptionStyle.ChooseColor(owner, captionBackground) is { } color)
        { captionBackground = color; ApplyCaptionStyle(ReadCaptionStyle()); OptionsChanged(); }
    }
    private void SaveOptions_Click(object sender, RoutedEventArgs e)
    { try { SavePreferences(); SessionStatus.Text = "字幕设置已保存；模型与设备在下次会话生效。"; } catch (Exception ex) { SessionStatus.Text = ex.Message; } }
    private void BrowsePython_Click(object sender, RoutedEventArgs e)
    { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Python|python.exe|程序|*.exe" }; if (dialog.ShowDialog() == true) SpeechPythonBox.Text = dialog.FileName; }
    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    { var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择包含 model.bin 的语音模型目录" }; if (dialog.ShowDialog() == true) SpeechModelBox.Text = dialog.FolderName; }
    private async void OpenTranscript_Click(object sender, RoutedEventArgs e)
    {
        string? path = session?.TranscriptPath ?? lastTranscript;
        try { if (session != null) await session.Checkpoint(); if (path != null && File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false }); else SessionStatus.Text = "还没有会话记录。"; }
        catch (Exception ex) { SessionStatus.Text = ex.Message; }
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    { try { Directory.CreateDirectory(sessionsFolder); Process.Start(new ProcessStartInfo(sessionsFolder) { UseShellExecute = true }); } catch (Exception ex) { SessionStatus.Text = ex.Message; } }
    private async void SaveTranscript_Click(object sender, RoutedEventArgs e)
    {
        string? path = session?.TranscriptPath ?? lastTranscript;
        if (path == null || !File.Exists(path)) { SessionStatus.Text = "还没有会话记录。"; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "文本记录|*.txt", FileName = Path.GetFileName(path), DefaultExt = ".txt" };
        try
        {
            if (session != null) await session.Checkpoint();
            if (dialog.ShowDialog() == true && !string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            { File.Copy(path, dialog.FileName, overwrite: true); SessionStatus.Text = "已导出当前文字记录：" + dialog.FileName; }
        }
        catch (Exception ex) { SessionStatus.Text = "导出失败：" + ex.Message; }
    }
    public void Dispose()
    {
        if (disposed) return;
        overlay?.CaptureDimensions();
        bool save = preferencesSave.IsEnabled;
        preferencesSave.Stop();
        disposed = true; session?.Dispose(); overlay?.Terminate();
        if (save)
        {
            try { SavePreferences(); } catch (Exception ex) { SessionStatus.Text = "保存字幕设置失败：" + ex.Message; }
        }
    }
}
