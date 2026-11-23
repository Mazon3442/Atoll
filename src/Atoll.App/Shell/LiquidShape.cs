using System.Windows;
using System.Windows.Media;
using Atoll.Modules.Liquid;

namespace Atoll.Shell;

/// <summary>
/// The island outline with a liquid lower edge: the same pill or notch, but the bottom runs along a ripple from
/// <see cref="LiquidEdge"/> instead of a straight line. Used both to clip the island and to draw its rim, so the two always match.
/// </summary>
internal static class LiquidShape
{
    private const double Step = 4;

    /// <param name="notch">Hang from the top edge with concave shoulders, instead of a plain pill.</param>
    /// <param name="outline">For a notch, leave out the top edge (it sits on the screen edge). A pill is stroked all the way round.</param>
    public static Geometry Build(double w, double h, double radius, bool notch, bool outline, double phase, double amplitude, IReadOnlyList<double>? bands)
    {
        if (!notch) outline = false; // a pill floats free, so its rim goes all the way round
        var s = notch ? Math.Max(0, Math.Min(PillBorder.MaxShoulder, Math.Min(h / 3, w / 8))) : 0;
        var r = Math.Max(0, Math.Min(radius, Math.Min((w - 2 * s) / 2, h - s)));
        var left = s + r;
        var right = w - s - r;
        var wave = LiquidEdge.Sample(left, right, Step, phase, amplitude, bands).Select(point => new Point(point.X, h - point.Rise)).ToList();

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            if (!notch)
            {
                // A plain pill: round all four corners.
                if (outline) context.BeginFigure(new Point(0, r), false, false);
                else
                {
                    context.BeginFigure(new Point(r, 0), true, true);
                    context.LineTo(new Point(w - r, 0), true, false);
                    if (r > 0) context.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                }
            }
            else
            {
                context.BeginFigure(new Point(0, 0), !outline, !outline);
                if (!outline) context.LineTo(new Point(w, 0), true, false);
            }

            if (outline)
            {
                // Down the left side, along the ripple, up the right side.
                if (notch)
                {
                    if (s > 0) context.ArcTo(new Point(s, s), new Size(s, s), 0, false, SweepDirection.Clockwise, true, false);
                    context.LineTo(new Point(s, h - r), true, false);
                }
                else context.LineTo(new Point(0, h - r), true, false);
                if (r > 0) context.ArcTo(new Point(left, h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                if (wave.Count > 0) context.PolyLineTo(wave, true, true);
                if (r > 0) context.ArcTo(new Point(notch ? w - s : w, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                if (notch)
                {
                    context.LineTo(new Point(w - s, s), true, false);
                    if (s > 0) context.ArcTo(new Point(w, 0), new Size(s, s), 0, false, SweepDirection.Clockwise, true, false);
                }
                else context.LineTo(new Point(w, r), true, false);
            }
            else
            {
                // Down the right side, back along the ripple (right to left), up the left side.
                if (notch)
                {
                    if (s > 0) context.ArcTo(new Point(w - s, s), new Size(s, s), 0, false, SweepDirection.Counterclockwise, true, false);
                    context.LineTo(new Point(w - s, h - r), true, false);
                }
                else context.LineTo(new Point(w, h - r), true, false);
                if (r > 0) context.ArcTo(new Point(right, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                if (wave.Count > 0)
                {
                    var back = new List<Point>(wave);
                    back.Reverse();
                    context.PolyLineTo(back, true, true);
                }
                if (r > 0) context.ArcTo(new Point(notch ? s : 0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                if (notch)
                {
                    context.LineTo(new Point(s, s), true, false);
                    if (s > 0) context.ArcTo(new Point(0, 0), new Size(s, s), 0, false, SweepDirection.Counterclockwise, true, false);
                }
                else
                {
                    context.LineTo(new Point(0, r), true, false);
                    if (r > 0) context.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
