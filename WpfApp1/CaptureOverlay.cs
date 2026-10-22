using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
namespace WpfApp1;

public sealed class CaptureOverlay : Window
{
    private readonly Canvas canvas = new();
    private readonly Rectangle outline = new() { Stroke = Brushes.DodgerBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(35, 90, 170, 255)) };
    private readonly System.Drawing.Bitmap screenshot;
    private Point? start;
    public string? ImagePath { get; private set; }
    public CaptureOverlay()
    {
        int x = Native.GetSystemMetrics(76), y = Native.GetSystemMetrics(77), width = Native.GetSystemMetrics(78), height = Native.GetSystemMetrics(79);
        screenshot = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(screenshot)) graphics.CopyFromScreen(x, y, 0, 0, screenshot.Size);
        using var memory = new MemoryStream(); screenshot.Save(memory, System.Drawing.Imaging.ImageFormat.Png); memory.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = memory; bitmap.EndInit(); bitmap.Freeze();
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Background = new ImageBrush(bitmap) { Stretch = Stretch.Fill, Opacity = 0.72 }; Cursor = Cursors.Cross;
        Content = canvas; canvas.Background = Brushes.Transparent; canvas.Children.Add(outline);
        var label = new TextBlock { Text = "拖动框选文字 · Esc 取消", Foreground = Brushes.White, Background = Brushes.Black, Padding = new Thickness(12), IsHitTestVisible = false };
        canvas.Children.Add(label); Canvas.SetTop(label, 20); Canvas.SetLeft(label, 20);
        SourceInitialized += (_, _) => Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), x, y, width, height, 0x0040);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        canvas.MouseLeftButtonDown += (_, e) => { start = e.GetPosition(canvas); canvas.CaptureMouse(); Update(start.Value); };
        canvas.MouseMove += (_, e) => { if (start.HasValue) Update(e.GetPosition(canvas)); };
        canvas.MouseLeftButtonUp += (_, e) => Finish(e.GetPosition(canvas));
        Closed += (_, _) => screenshot.Dispose();
    }
    private void Update(Point end)
    {
        var rect = new Rect(start!.Value, end); Canvas.SetLeft(outline, rect.Left); Canvas.SetTop(outline, rect.Top); outline.Width = rect.Width; outline.Height = rect.Height;
    }
    private void Finish(Point end)
    {
        if (!start.HasValue) return;
        canvas.ReleaseMouseCapture(); var rect = new Rect(start.Value, end); start = null;
        if (rect.Width < 4 || rect.Height < 4) return;
        try
        {
            int left = Math.Clamp((int)(rect.Left / canvas.ActualWidth * screenshot.Width), 0, screenshot.Width - 1);
            int top = Math.Clamp((int)(rect.Top / canvas.ActualHeight * screenshot.Height), 0, screenshot.Height - 1);
            int right = Math.Clamp((int)Math.Ceiling(rect.Right / canvas.ActualWidth * screenshot.Width), left + 1, screenshot.Width);
            int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom / canvas.ActualHeight * screenshot.Height), top + 1, screenshot.Height);
            using var crop = screenshot.Clone(new System.Drawing.Rectangle(left, top, right - left, bottom - top), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            ImagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "glimpse-" + Guid.NewGuid().ToString("N") + ".png");
            crop.Save(ImagePath, System.Drawing.Imaging.ImageFormat.Png); DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, "截图失败：" + ex.Message); DialogResult = false; }
    }
}
