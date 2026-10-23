using System.Drawing;
using System.Drawing.Drawing2D;

namespace WpfApp1;

internal static class PowerModeIcons
{
    // Native tray menus need bitmaps; match the window's line icons.
    public static Bitmap Create(bool leaf)
    {
        var image = new Bitmap(24, 24);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(leaf ? Color.FromArgb(57, 164, 107) : Color.FromArgb(102, 93, 245), 1.7f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var path = new GraphicsPath();
        if (leaf)
        {
            path.AddBezier(20, 3, 10, 2, 3, 6, 4, 13);
            path.AddBezier(4, 13, 5, 20, 18, 21, 20, 3);
            path.CloseFigure();
            graphics.DrawPath(pen, path);
            path.Reset();
            path.AddBezier(3, 22, 6, 15, 10, 11, 15, 8);
        }
        else
        {
            path.AddPolygon(new[] { new Point(13, 2), new Point(4, 14), new Point(11, 14),
                new Point(10, 22), new Point(21, 9), new Point(13, 9) });
        }
        graphics.DrawPath(pen, path);
        return image;
    }
}
