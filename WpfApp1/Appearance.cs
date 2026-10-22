using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WpfApp1;

internal static class Appearance
{
    public static void UpdateTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int enabled = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    public static void Apply(bool dark, double fontSize)
    {
        var resources = Application.Current.Resources;
        void Brush(string key, string light, string night)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            resources[key] = brush;
        }
        Brush("Ink", "#232735", "#E5E7F0");
        Brush("Muted", "#7B8192", "#ACB2C5");
        Brush("Accent", "#565BE8", "#858AFF");
        Brush("Line", "#E9EAF0", "#373C50");
        Brush("WindowSurface", "#F6F7FB", "#171A24");
        Brush("Surface", "#FFFFFF", "#222633");
        Brush("InputSurface", "#F8F9FC", "#1C202D");
        Brush("SoftSurface", "#EDEFF6", "#1B1F2B");
        Brush("TranslationSurface", "#F0F1FD", "#282B44");
        Brush("TranslationLine", "#E5E6F8", "#3D4164");
        Brush("TranslationInk", "#414777", "#D0D4FF");
        Brush("SubtleInk", "#989DC0", "#A0A7CC");
        Brush("Selection", "#CBCDF9", "#51588D");
        Brush("Focus", "#A4A7F3", "#9EA3FF");
        resources["ReadingFontSize"] = double.IsFinite(fontSize) ? Math.Clamp(fontSize, 12, 30) : 19d;
    }
}
