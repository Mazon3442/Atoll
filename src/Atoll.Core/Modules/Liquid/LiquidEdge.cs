namespace Atoll.Modules.Liquid;

/// <summary>
/// The wavy lower edge of the island. A sine ripple that travels along the edge and grows with the music, fading to
/// nothing at the corners so the shape still meets the sides cleanly. Pure maths: how far the edge rises at each x.
/// </summary>
public static class LiquidEdge
{
    /// <summary>Distance between ripple crests, in layout units.</summary>
    public const double Wavelength = 30;

    /// <summary>The ripple never rises more than this, however loud the music, so text above it is never clipped.</summary>
    public const double MaxRise = 6;

    /// <summary>
    /// How far above the bottom the edge sits at <paramref name="x"/>: zero at both ends, a ripple of
    /// <paramref name="amplitude"/> plus the local music level times <paramref name="boost"/> in between.
    /// </summary>
    /// <param name="x">Position along the edge.</param>
    /// <param name="left">Where the straight bottom starts.</param>
    /// <param name="right">Where the straight bottom ends.</param>
    /// <param name="phase">Radians; advancing it moves the ripple.</param>
    /// <param name="bands">Music levels 0 to 1, low frequencies first, spread along the edge; null for none.</param>
    public static double Rise(double x, double left, double right, double phase, double amplitude, IReadOnlyList<double>? bands = null, double boost = 4)
    {
        var span = right - left;
        if (span <= 0 || x <= left || x >= right) return 0;
        var edge = Math.Min(x - left, right - x);
        var fade = Math.Clamp(edge / Math.Min(24, span / 2), 0, 1);
        fade = fade * fade * (3 - 2 * fade); // smoothstep
        var ripple = 0.5 + 0.5 * Math.Sin(x / Wavelength * 2 * Math.PI + phase);
        var level = Level(bands, (x - left) / span);
        return Math.Min(MaxRise, fade * ripple * (Math.Max(0, amplitude) + level * boost));
    }

    /// <summary>The music level at <paramref name="at"/> (0 left, 1 right), interpolating between bands.</summary>
    public static double Level(IReadOnlyList<double>? bands, double at)
    {
        if (bands is null || bands.Count == 0) return 0;
        if (bands.Count == 1) return Math.Clamp(bands[0], 0, 1);
        var position = Math.Clamp(at, 0, 1) * (bands.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(bands.Count - 1, lower + 1);
        var fraction = position - lower;
        return Math.Clamp(bands[lower] * (1 - fraction) + bands[upper] * fraction, 0, 1);
    }

    /// <summary>The edge as points from <paramref name="left"/> to <paramref name="right"/>, every <paramref name="step"/> units, as (x, rise).</summary>
    public static IReadOnlyList<(double X, double Rise)> Sample(double left, double right, double step, double phase, double amplitude, IReadOnlyList<double>? bands = null, double boost = 4)
    {
        var points = new List<(double, double)>();
        if (right <= left || step <= 0) return points;
        for (var x = left; x < right; x += step) points.Add((x, Rise(x, left, right, phase, amplitude, bands, boost)));
        points.Add((right, 0));
        return points;
    }
}
