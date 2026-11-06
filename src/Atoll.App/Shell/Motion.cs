using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Atoll.Shell;

/// <summary>
/// Damped spring (slight overshoot, settles by t = 1). Gives the island its Apple-like bounce.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    private const double Damping = 0.72;
    private const double Frequency = 10;

    public SpringEase() => EasingMode = EasingMode.EaseIn; // EaseIn = use EaseInCore as-is

    protected override double EaseInCore(double t)
    {
        if (t >= 1) return 1;
        var damped = Frequency * Math.Sqrt(1 - Damping * Damping);
        var decay = Math.Exp(-Damping * Frequency * t);
        return 1 - decay * (Math.Cos(damped * t) + Damping * Frequency / damped * Math.Sin(damped * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}

/// <summary>Border whose corner radius is a single animatable double.</summary>
public sealed class PillBorder : Border
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(PillBorder),
        new PropertyMetadata(0d, (d, e) => ((Border)d).CornerRadius = new CornerRadius(Math.Max(0, (double)e.NewValue))));

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
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
