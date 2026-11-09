using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Atoll.Shell;

/// <summary>
/// Damped spring (slight overshoot, settles by t = 1). Gives the island its Apple-like bounce.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    private const double Frequency = 10;
    private readonly double Damping;

    /// <param name="damping">Below 1 overshoots; 1 settles without bouncing.</param>
    public SpringEase(double damping = 0.72)
    {
        Damping = Math.Clamp(damping, 0.1, 1);
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

    protected override Freezable CreateInstanceCore() => new SpringEase(Damping);
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
        BorderThickness = Notch ? new Thickness(1, 0, 1, 1) : new Thickness(1); // no rim along the screen edge
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
