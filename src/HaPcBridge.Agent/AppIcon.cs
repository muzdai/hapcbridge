using System.Drawing.Drawing2D;

namespace HaPcBridge.Agent;

/// <summary>The monitor glyph used for the tray, the settings window and its header.</summary>
internal static class AppIcon
{
    public static readonly Color Blue = Color.FromArgb(3, 169, 244); // Home Assistant blue

    public static Bitmap Bitmap(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float s = size / 32f;
        using var body = new SolidBrush(Blue);
        using var screen = new SolidBrush(Color.FromArgb(225, 245, 254));
        g.FillRectangle(body, 3 * s, 4 * s, 26 * s, 18 * s);
        g.FillRectangle(screen, 6 * s, 7 * s, 20 * s, 12 * s);
        g.FillRectangle(body, 13 * s, 22 * s, 6 * s, 4 * s);
        g.FillRectangle(body, 8 * s, 26 * s, 16 * s, 3 * s);
        return bmp;
    }

    public static Icon Icon(int size = 32)
    {
        using var bmp = Bitmap(size);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Small round status dot for menus and the header.</summary>
    public static Bitmap Dot(Color color, int size = 16, int diameter = 9)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var b = new SolidBrush(color);
        var o = (size - diameter) / 2f;
        g.FillEllipse(b, o, o, diameter, diameter);
        return bmp;
    }

    public static readonly Color Ok = Color.FromArgb(46, 160, 67);
    public static readonly Color Warn = Color.FromArgb(219, 130, 0);
    public static readonly Color Off = Color.FromArgb(150, 150, 150);
}
