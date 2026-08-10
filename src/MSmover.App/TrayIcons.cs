using System.Drawing.Drawing2D;
using MSmover.Core.Engine;

namespace MSmover.App;

/// <summary>
/// The MSmover mark: a mass spectrum whose baseline is an arrow.
///
/// The four state colours are drawn at runtime rather than shipped as four .ico files, so each is
/// rendered natively at whatever size the tray asks for instead of being scaled from one bitmap.
/// (msmover.ico, the executable's own icon, is generated from this same code.)
///
/// The badge is a solid rounded square because its colour is the status indicator — you have to be
/// able to read "red" out of the corner of your eye at 16 pixels.
/// </summary>
public static class TrayIcons
{
    private static readonly Dictionary<ServiceHealth, Icon> Cache = new();

    public static Icon For(ServiceHealth health)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(health, out var cached)) return cached;
            var icon = Build(ColorFor(health));
            Cache[health] = icon;
            return icon;
        }
    }

    public static Color ColorFor(ServiceHealth health) => health switch
    {
        ServiceHealth.Working => Color.FromArgb(0x2E, 0xA0, 0x43),
        ServiceHealth.Paused => Color.FromArgb(0xE0, 0x9B, 0x13),
        ServiceHealth.Error => Color.FromArgb(0xC9, 0x2A, 0x2A),
        _ => Color.FromArgb(0x3B, 0x82, 0xF6)
    };

    public static Icon Build(Color color, int size = 32)
    {
        using var bmp = Render(color, size);
        var handle = bmp.GetHicon();
        using var temp = Icon.FromHandle(handle);
        // Clone so the icon survives the HICON being released.
        return (Icon)temp.Clone();
    }

    /// <summary>The mark on its own, for anywhere a bitmap is wanted instead of an icon.</summary>
    public static Bitmap Render(Color color, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Everything below is expressed on a 64x64 grid and scaled, so the proportions hold at
        // 16px in the tray and at 256px in a dialog.
        var s = size / 64f;
        float X(float v) => v * s;

        using (var badge = RoundedRect(X(2), X(2), X(60), X(60), X(15)))
        using (var fill = new SolidBrush(color))
            g.FillPath(fill, badge);

        // A darker rim keeps the badge from dissolving into a light taskbar.
        using (var badge = RoundedRect(X(2), X(2), X(60), X(60), X(15)))
        using (var rim = new Pen(Color.FromArgb(45, 0, 0, 0), Math.Max(1f, X(1.5f))))
            g.DrawPath(rim, badge);

        using var stroke = new Pen(Color.White, X(5f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        // Spectrum peaks rising from the baseline. The heights are deliberately uneven — an even
        // comb reads as a bar chart — and tall/short/tall hints at an M without being cute about it.
        const float baseline = 45f;
        g.DrawLine(stroke, X(16), X(baseline), X(16), X(16));
        g.DrawLine(stroke, X(25), X(baseline), X(25), X(29));
        g.DrawLine(stroke, X(34), X(baseline), X(34), X(21));

        // The baseline doubles as the shaft of an arrow: the data is going somewhere.
        g.DrawLine(stroke, X(14), X(baseline), X(41), X(baseline));

        using var white = new SolidBrush(Color.White);
        g.FillPolygon(white, new[]
        {
            new PointF(X(40), X(baseline - 7.5f)),
            new PointF(X(52), X(baseline)),
            new PointF(X(40), X(baseline + 7.5f))
        });

        return bmp;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
