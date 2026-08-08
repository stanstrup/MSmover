using System.Drawing.Drawing2D;
using MSmover.Core.Engine;

namespace MSmover.App;

/// <summary>
/// Tray icons drawn at runtime rather than shipped as .ico resources, so the repository stays
/// free of binary assets and the icon scales to whatever DPI the tray asks for.
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
        _ => Color.FromArgb(0x6B, 0x72, 0x80)
    };

    /// <summary>A filled disc with a white arrow: "things move from here to there".</summary>
    public static Icon Build(Color color, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var disc = new SolidBrush(color);
            g.FillEllipse(disc, 1, 1, size - 3, size - 3);

            using var rim = new Pen(Color.FromArgb(70, 0, 0, 0), Math.Max(1f, size / 24f));
            g.DrawEllipse(rim, 1, 1, size - 3, size - 3);

            var s = size / 32f;
            using var arrow = new SolidBrush(Color.White);
            g.FillPolygon(arrow, new[]
            {
                new PointF(9 * s, 13 * s), new PointF(18 * s, 13 * s), new PointF(18 * s, 9 * s),
                new PointF(25 * s, 16 * s), new PointF(18 * s, 23 * s), new PointF(18 * s, 19 * s),
                new PointF(9 * s, 19 * s)
            });
        }

        var handle = bmp.GetHicon();
        using var temp = Icon.FromHandle(handle);
        // Clone so the icon survives the HICON being destroyed by the GC finaliser.
        return (Icon)temp.Clone();
    }
}
