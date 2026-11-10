using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Atoll.Settings;

namespace Atoll.Shell;

/// <summary>
/// Damped spring (slight overshoot, settles by t = 1). Gives the island its Apple-like bounce.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    private readonly double Damping;
    private readonly double Frequency;

    /// <param name="damping">Below 1 overshoots; 1 settles without bouncing.</param>
    /// <param name="frequency">Stiffness: higher settles sooner, with smaller and quicker waves.</param>
    public SpringEase(double damping = 0.72, double frequency = 10)
    {
        Damping = Math.Clamp(damping, 0.1, 1);
        Frequency = Math.Max(frequency, 0.1);
        EasingMode = EasingMode.EaseIn; // EaseIn = use EaseInCore as-is
    }

    protected override double EaseInCore(double t)
    {
        if (t >= 1) return 1;
        if (Damping >= 1) return 1 - Math.Exp(-Frequency * t) * (1 + Frequency * t); // critically damped
        var damped = Frequency * Math.Sqrt(1 - Damping * Damping);
        var decay = Math.Exp(-Damping * Frequency * t);
        return 1 - decay * (Math.Cos(damped * t) + Damping * Frequency / damped * Math.Sin(damped * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase(Damping, Frequency);
}

/// <summary>
/// The numbers behind each <see cref="MotionStyle"/>. Bouncy and Smooth keep the tuning the island
/// shipped with, so an existing settings file animates exactly as before; Snappy is shorter and tighter.
/// </summary>
internal sealed class MotionPreset
{
    private MotionPreset(double damping, double frequency, int morphMs, int fadeMs)
    {
        var ease = new SpringEase(damping, frequency);
        ease.Freeze(); // one shared instance; WPF clones it per animation
        Ease = ease;
        MorphMs = morphMs;
        FadeMs = fadeMs;
    }

    internal static MotionPreset Bouncy { get; } = new(0.72, 10, 320, 140);
    internal static MotionPreset Smooth { get; } = new(1, 10, 320, 140);
    internal static MotionPreset Snappy { get; } = new(0.82, 16, 220, 110);

    internal IEasingFunction Ease { get; }
    internal int MorphMs { get; }
    internal int FadeMs { get; }

    /// <summary>Off snaps instead of animating, so it never reads these numbers.</summary>
    internal static MotionPreset For(MotionStyle style) => style switch
    {
        MotionStyle.Snappy => Snappy,
        MotionStyle.Smooth => Smooth,
        _ => Bouncy,
    };
}

/// <summary>Border whose corner radius is a single animatable double. As a notch it hangs from the top edge.</summary>
public sealed class PillBorder : Border
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(PillBorder), new PropertyMetadata(0d, (d, _) => ((PillBorder)d).UpdateShape()));

    public static readonly DependencyProperty NotchProperty = DependencyProperty.Register(
        nameof(Notch), typeof(bool), typeof(PillBorder), new PropertyMetadata(false, (d, _) => ((PillBorder)d).UpdateShape()));

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public bool Notch
    {
        get => (bool)GetValue(NotchProperty);
        set => SetValue(NotchProperty, value);
    }

    private void UpdateShape()
    {
        var r = Math.Max(0, Radius);
        CornerRadius = Notch ? new CornerRadius(0, 0, r, r) : new CornerRadius(r);
        // A notch draws its own rim along the shoulders (see OnRender); the border's would be cut off by the clip.
        BorderThickness = Notch ? new Thickness(0) : new Thickness(1);
        UpdateClip();
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateClip();
    }

    /// <summary>How far each shoulder flares: a visible curve on a tall panel, still tidy on a short pill.</summary>
    internal const double MaxShoulder = 14;

    private static double ShoulderFor(double w, double h) => Math.Max(0, Math.Min(MaxShoulder, Math.Min(h / 3, w / 8)));

    /// <summary>
    /// The notch outline. The top corners flare out to meet the screen edge in a smooth concave curve, like a laptop
    /// notch, and the bottom corners are round. <paramref name="outline"/> leaves the top edge out, for the rim.
    /// </summary>
    internal static Geometry NotchGeometry(double w, double h, double radius, bool outline)
    {
        var s = ShoulderFor(w, h);
        var r = Math.Max(0, Math.Min(radius, Math.Min((w - 2 * s) / 2, h - s)));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            if (outline) context.BeginFigure(new Point(0, 0), false, false);
            else
            {
                context.BeginFigure(new Point(0, 0), true, true);
                context.LineTo(new Point(w, 0), true, false);
            }
            if (outline)
            {
                // Left shoulder, down the left side, round the bottom, up the right side, right shoulder.
                if (s > 0) context.ArcTo(new Point(s, s), new Size(s, s), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(s, h - r), true, false);
                if (r > 0) context.ArcTo(new Point(s + r, h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                context.LineTo(new Point(w - s - r, h), true, false);
                if (r > 0) context.ArcTo(new Point(w - s, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                context.LineTo(new Point(w - s, s), true, false);
                if (s > 0) context.ArcTo(new Point(w, 0), new Size(s, s), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                if (s > 0) context.ArcTo(new Point(w - s, s), new Size(s, s), 0, false, SweepDirection.Counterclockwise, true, false);
                context.LineTo(new Point(w - s, h - r), true, false);
                if (r > 0) context.ArcTo(new Point(w - s - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(s + r, h), true, false);
                if (r > 0) context.ArcTo(new Point(s, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(s, s), true, false);
                if (s > 0) context.ArcTo(new Point(0, 0), new Size(s, s), 0, false, SweepDirection.Counterclockwise, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void UpdateClip()
    {
        if (!Notch || ActualWidth <= 0 || ActualHeight <= 0)
        {
            Clip = null;
            return;
        }
        Clip = NotchGeometry(ActualWidth, ActualHeight, Radius, outline: false);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!Notch || ActualWidth <= 0 || ActualHeight <= 0 || BorderBrush is not { } brush) return;
        // Two pixels centred on the edge: the clip keeps the inner one, so the rim is one pixel along the whole outline.
        dc.DrawGeometry(null, new Pen(brush, 2), NotchGeometry(ActualWidth, ActualHeight, Radius, outline: true));
    }
}

/// <summary>
/// Finds a module's DataTemplate by convention: "{ViewModelTypeName}.{Mode}" where Mode is
/// Compact, Peek, Mini or Panel. Peek falls back to Compact; Mini falls back to "Fallback.Mini".
/// </summary>
public static class Templates
{
    public static DataTemplate? Find(FrameworkElement scope, object content, string mode)
    {
        foreach (var m in mode == "Peek" ? ["Peek", "Compact"] : new[] { mode })
        {
            for (var t = content.GetType(); t is not null && t != typeof(object); t = t.BaseType)
            {
                if (scope.TryFindResource($"{t.Name}.{m}") is DataTemplate found) return found;
            }
        }
        return scope.TryFindResource($"Fallback.{mode}") as DataTemplate;
    }
}
