using System.Drawing.Drawing2D;

namespace Exora.Core;

// Draws the Exora "X" logo with the current accent colour, so the window and taskbar icon follow the theme.
// Geometry matches the SVG in wwwroot/index.html (1024 x 1024 canvas).
public static class AppIcon
{
    public static Icon Create(string accentHex, int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            var k = size / 1024f;
            g.ScaleTransform(k, k);

            using (var bg = RoundedRect(new RectangleF(0, 0, 1024, 1024), 230))
            using (var brush = new SolidBrush(Color.FromArgb(0x13, 0x14, 0x1A)))
                g.FillPath(brush, bg);

            using var white = new Pen(Color.FromArgb(0xEC, 0xED, 0xF1), 92) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using (var hook = new GraphicsPath())
            {
                hook.AddLine(760, 266, 410, 636);
                hook.AddBezier(410, 636, 350, 698, 305, 736, 262, 736);
                hook.AddBezier(262, 736, 214, 736, 206, 680, 246, 642);
                g.DrawPath(white, hook);
            }

            using var accent = new Pen(ParseColor(accentHex), 92) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(accent, 287, 266, 758, 778);
        }
        var handle = bmp.GetHicon();
        // Clone so the icon owns its data; the original handle is released right away.
        using var tmp = Icon.FromHandle(handle);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    private static Color ParseColor(string hex)
    {
        if (hex == "mono") return Color.FromArgb(0xF2, 0xF3, 0xF5);
        try
        {
            return ColorTranslator.FromHtml(hex);
        }
        catch
        {
            return Color.FromArgb(0x4F, 0x8C, 0xFF);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
