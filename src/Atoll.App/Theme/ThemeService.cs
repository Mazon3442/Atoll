using System.Windows;
using System.Windows.Media;
using Atoll.Settings;

namespace Atoll.Theme;

/// <summary>Pushes theme settings into app-level resources that the island uses via DynamicResource.</summary>
public static class ThemeService
{
    public static IReadOnlyList<ThemePreset> Presets => ThemePresets.All;

    /// <summary>Tint for the frosted-glass backdrop (null when the theme is not glass).</summary>
    public static Color? GlassTint { get; private set; }

    public static void Apply(ThemeSettings theme)
    {
        var defaults = new ThemeSettings();
        var bg = ParseColor(theme.Background, defaults.Background);
        var fg = ParseColor(theme.Foreground, defaults.Foreground);
        var accent = ParseColor(theme.Accent, defaults.Accent);
        var opacity = Math.Clamp(theme.Opacity, 0.3, 1);

        var res = Application.Current.Resources;
        if (theme.Glass)
        {
            // The blur sits behind the pill; the tint is a wash over it. Hold the wash back or the frost
            // disappears behind the background color and the island reads as a solid block.
            var tint = GlassTintFor(bg, opacity);
            GlassTint = tint;
            res["Island.Background"] = Frozen(new SolidColorBrush(tint));
            res["Island.Rim"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.18 });
        }
        else
        {
            GlassTint = null;
            res["Island.Background"] = Frozen(new SolidColorBrush(bg) { Opacity = opacity });
            res["Island.Rim"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.12 });
        }
        if (!theme.Outline) res["Island.Rim"] = Brushes.Transparent;
        res["Island.Menu"] = Frozen(new SolidColorBrush(Color.FromRgb(bg.R, bg.G, bg.B)));
        res["Island.Foreground"] = Frozen(new SolidColorBrush(fg));
        res["Island.Subtle"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.6 });
        res["Island.Hover"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.12 });
        res["Island.Accent"] = Frozen(new SolidColorBrush(accent));
        res["Island.AccentText"] = Frozen(new SolidColorBrush(AccentTextColor(accent, bg, fg)));
        res["Island.Surface"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.08 });
        res["Island.Raised"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.16 });
        res["Island.SelectedBackground"] = Frozen(new SolidColorBrush(Color.FromRgb(fg.R, fg.G, fg.B)));
        res["Island.SelectedForeground"] = Frozen(new SolidColorBrush(Color.FromRgb(bg.R, bg.G, bg.B)));
    }

    /// <summary>Share of the background opacity a glass tint keeps, so the blur stays visible through it.</summary>
    private const double GlassWash = 0.6;

    /// <summary>
    /// The translucent tint painted over the blurred backdrop. Full background opacity would hide the blur,
    /// so glass keeps a fraction of it; the color itself is unchanged.
    /// </summary>
    internal static Color GlassTintFor(Color background, double opacity) =>
        Color.FromArgb((byte)Math.Round(background.A * Math.Clamp(opacity, 0.3, 1) * GlassWash),
            background.R, background.G, background.B);

    internal static Color AccentTextColor(Color accent, Color background, Color foreground) =>
        accent.A == 255 && background.A == 255 && Contrast(accent, background) >= 4.5 ? accent : foreground;

    internal static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c) => 0.2126 * c.ScR + 0.7152 * c.ScG + 0.0722 * c.ScB;
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(text.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Color ParseColor(string text, string fallback) =>
        TryParseColor(text, out var c) ? c : (Color)ColorConverter.ConvertFromString(fallback);

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
