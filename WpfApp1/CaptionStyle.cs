using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WpfApp1;

internal sealed record CaptionStyle(bool Bilingual, double FontSize, double BackgroundOpacity,
    double TextOpacity, string Foreground, string Background)
{
    public static CaptionStyle FromSettings(Settings s) => new CaptionStyle(s.CaptionBilingual, s.CaptionFontSize,
        s.CaptionBackgroundOpacity, s.CaptionTextOpacity, s.CaptionForegroundColor, s.CaptionBackgroundColor).Normalize();
    public CaptionStyle Normalize() => this with
    {
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 16, 72) : 28,
        BackgroundOpacity = double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, 0, 1) : 0.35,
        TextOpacity = double.IsFinite(TextOpacity) ? Math.Clamp(TextOpacity, 0.25, 1) : 1,
        Foreground = Hex(Parse(Foreground, Colors.White)), Background = Hex(Parse(Background, Colors.Black))
    };
    public void CopyTo(Settings s)
    {
        s.CaptionBilingual = Bilingual; s.CaptionFontSize = FontSize;
        s.CaptionBackgroundOpacity = BackgroundOpacity; s.CaptionTextOpacity = TextOpacity;
        s.CaptionForegroundColor = Foreground; s.CaptionBackgroundColor = Background;
    }
    public static Color Parse(string? value, Color fallback)
    {
        try { return ColorConverter.ConvertFromString(value ?? "") is Color color ? Color.FromRgb(color.R, color.G, color.B) : fallback; }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException) { return fallback; }
    }
    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    public static string? ChooseColor(Window owner, string current)
    {
        var color = Parse(current, Colors.White);
        using var picker = new System.Windows.Forms.ColorDialog
        { FullOpen = true, AnyColor = true, Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B) };
        return picker.ShowDialog(new DialogOwner(new WindowInteropHelper(owner).Handle)) == System.Windows.Forms.DialogResult.OK
            ? $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}" : null;
    }
    private sealed class DialogOwner(IntPtr handle) : System.Windows.Forms.IWin32Window
    { public IntPtr Handle => handle; }
}
