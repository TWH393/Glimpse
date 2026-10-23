using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WpfApp1;

public partial class MediaTranslationPanel : UserControl, IDisposable
{
    private MediaTranslationJob? job;
    private string? lastOutput;
    private bool disposed;
    private readonly System.Windows.Threading.DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    public Func<Settings>? TranslationSettings { get; set; }
    public MediaTranslationPanel()
    {
        InitializeComponent();
        var s = new Settings();
        try { s = Settings.Load(); }
        catch (Exception ex) { JobStatus.Text = "读取设置失败：" + ex.Message; }
        DeviceBox.SelectedIndex = s.MediaGpuOnly ? 0 : 1;
        OutputFolder.Text = s.MediaOutputFolder;
        refresh.Tick += (_, _) => { if (job is { } current) RenderProgress(current.Snapshot, current.OutputPath); };
    }
    public void CopyPreferences(Settings settings)
    { settings.MediaGpuOnly = DeviceBox.SelectedIndex == 0; settings.MediaOutputFolder = OutputFolder.Text; }
    private void OnUi(Action action)
    { if (!disposed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if (!disposed) action(); })); }
    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入音频或视频", Multiselect = false,
            Filter = "音频与视频|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.m4v;*.ts|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true) InputPath.Text = dialog.FileName;
    }
    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择文本保存目录" };
        if (dialog.ShowDialog() == true) OutputFolder.Text = dialog.FolderName;
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (job != null || disposed) return;
        if (!File.Exists(InputPath.Text)) { JobStatus.Text = "请先选择一个本地音频或视频文件。"; return; }
        MediaTranslationJob? current = null;
        try
        {
            var settings = TranslationSettings?.Invoke() ?? Settings.Load();
            CopyPreferences(settings); settings.Save();
            current = new MediaTranslationJob(settings, InputPath.Text, OutputFolder.Text, DeviceBox.SelectedIndex == 0);
            job = current;
            ConfigurationPanel.IsEnabled = false; StartButton.IsEnabled = false;
            PauseButton.IsEnabled = true; CancelButton.IsEnabled = true; PauseButton.Content = "暂停";
            TranscriptPreview.Clear(); StreamingText.Text = ""; OutputText.Text = "";
            JobProgress.Value = 0; JobProgress.IsIndeterminate = true;
            ProgressText.Text = "正在准备…"; TimingText.Text = "";
            refresh.Start();
            current.Status += text => OnUi(() => { if (job == current) JobStatus.Text = text; });
            current.Notice += text => OnUi(() => { if (job == current) Append(text + Environment.NewLine); });
            current.Preview += text => OnUi(() => { if (job == current) StreamingText.Text = text.Length > 0 ? "正在生成：" + text : ""; });
            current.Caption += entry => OnUi(() =>
            {
                if (job != current) return;
                Append($"[{Stamp(entry.Start)} → {Stamp(entry.End)}]\nEN: {entry.English}\nZH: {entry.Chinese}\n\n");
            });
            current.Progress += progress => OnUi(() =>
            {
                if (job != current) return;
                RenderProgress(progress, current.OutputPath);
            });
            await current.Start();
            lastOutput = current.OutputPath ?? lastOutput;
            JobProgress.IsIndeterminate = false;
            if (current.Completed) JobProgress.Value = 100;
            JobStatus.Text = (current.Failure.Length > 0 ? "处理停止：" + current.Failure : current.Completed ? "转译完成。" : "已取消处理。")
                + (current.OutputPath == null ? "" : current.Saved ? " 文本已保存。" : " 未完成文本保存。");
            OutputText.Text = current.OutputPath ?? "";
        }
        catch (Exception ex) { JobStatus.Text = "无法开始转译：" + ex.Message; }
        finally
        {
            refresh.Stop(); current?.Dispose(); job = null; StreamingText.Text = "";
            ConfigurationPanel.IsEnabled = true; StartButton.IsEnabled = true;
            PauseButton.IsEnabled = false; CancelButton.IsEnabled = false; JobProgress.IsIndeterminate = false;
            try { await LocalLlm.UpdatePolicy(TranslationSettings?.Invoke() ?? Settings.Load()); }
            catch (Exception ex) { if (!disposed) JobStatus.Text += "；恢复模型设置失败：" + ex.Message; }
        }
    }
    private void RenderProgress(MediaProgress progress, string? path)
    {
        JobProgress.IsIndeterminate = progress.Duration <= 0;
        JobProgress.Value = progress.Duration > 0 ? Math.Clamp(progress.Position / progress.Duration * 100, 0, 100) : 0;
        ProgressText.Text = $"已识别位置 {Stamp(progress.Position)} / {(progress.Duration > 0 ? Stamp(progress.Duration) : "时长未知")} · 当前会话用时 {TimeSpan.FromSeconds(progress.Elapsed):hh\\:mm\\:ss}";
        TimingText.Text = $"最近一块识别 {progress.Asr:F2} 秒 · 最近一段翻译 {progress.Translation:F2} 秒（仅显示，不写入文本）";
        OutputText.Text = path ?? "";
    }
    private void Append(string text)
    {
        TranscriptPreview.AppendText(text);
        if (TranscriptPreview.Text.Length > 30000) TranscriptPreview.Text = TranscriptPreview.Text[^24000..];
        TranscriptPreview.ScrollToEnd();
    }
    private static string Stamp(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}";
    }
    private void Pause_Click(object sender, RoutedEventArgs e)
    { job?.TogglePause(); PauseButton.Content = job?.Paused == true ? "继续" : "暂停"; }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    { CancelButton.IsEnabled = false; PauseButton.IsEnabled = false; JobStatus.Text = "正在取消并保存…"; job?.Cancel(); }
    private async Task<string?> SavedPath()
    {
        if (job != null) await job.Checkpoint();
        string? path = job?.OutputPath ?? lastOutput;
        if (path == null || !File.Exists(path)) { JobStatus.Text = "还没有生成文本。"; return null; }
        return path;
    }
    private async void OpenText_Click(object sender, RoutedEventArgs e)
    {
        try { if (await SavedPath() is { } path) Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false, ArgumentList = { path } }); }
        catch (Exception ex) { JobStatus.Text = "打开失败：" + ex.Message; }
    }
    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await SavedPath() is not { } path) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "文本记录|*.txt", FileName = Path.GetFileName(path), DefaultExt = ".txt" };
            if (dialog.ShowDialog() == true && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase))
            { File.Copy(path, dialog.FileName, true); JobStatus.Text = "已另存为：" + dialog.FileName; }
        }
        catch (Exception ex) { JobStatus.Text = "另存为失败：" + ex.Message; }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(OutputFolder.Text); Process.Start(new ProcessStartInfo(OutputFolder.Text) { UseShellExecute = true }); }
        catch (Exception ex) { JobStatus.Text = "打开目录失败：" + ex.Message; }
    }
    public void Dispose() { disposed = true; refresh.Stop(); job?.Cancel(); }
}
