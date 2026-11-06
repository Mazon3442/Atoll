using System.Windows;
using System.Windows.Media;
using Atoll.Settings;

namespace Atoll.Theme;

/// <summary>Pushes theme settings into app-level resources that the island uses via DynamicResource.</summary>
public static class ThemeService
{
    public static IReadOnlyList<ThemePreset> Presets { get; } =
    [
        new("Midnight", "#000000", "#FFFFFF", "#0A84FF"),
        new("Graphite", "#1C1C1E", "#F2F2F7", "#FF9F0A"),
        new("Ocean", "#0B1E33", "#E6F1FF", "#64D2FF"),
        new("Forest", "#0F1F17", "#E8F5EC", "#30D158"),
        new("Paper", "#F5F5F7", "#1C1C1E", "#007AFF"),
        new("Frosted Glass", "#59161618", "#FFFFFF", "#7ECEFD", Glass: true),
        new("Frosted Light", "#8CF4F4F8", "#0B132B", "#2E6BEF", Glass: true),

        // From popular UX palettes on coolors.co (background / text / accent picked from each).
        new("Steel", "#212529", "#F8F9FA", "#ADB5BD"),
        new("Lemon Frost", "#0B132B", "#F4F4F8", "#FED766"),
        new("Flamingo", "#AA0E1B", "#FDEDEF", "#65CDB6"),
        new("Sunrise", "#2A536E", "#FFFFFF", "#F9F871"),
        new("Indigo", "#1F006C", "#FFFFFF", "#F9C22E"),
        new("Deep Rose", "#001514", "#FBFFFE", "#B47F7E"),
        new("Harbor", "#183642", "#FDF0D5", "#F0544F"),
        new("Olive", "#283618", "#FEFAE0", "#DDA15E"),
        new("Cocoa", "#402E2A", "#FFEECF", "#7FB285"),
        new("Amber", "#181611", "#F1F5F2", "#FFBC00"),
        new("Sky", "#0B132B", "#FFFFFF", "#7ECEFD"),
        new("Orchid", "#3C3C3C", "#FFFFFF", "#B83FCA"),
        new("Cream", "#FEFAE0", "#283618", "#BC6C25"),
    ];

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
            // The blur window behind the pill carries the tint; the pill itself only adds a rim.
            GlassTint = Color.FromArgb((byte)(bg.A * opacity), bg.R, bg.G, bg.B);
            res["Island.Background"] = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))); // alpha 1: still hit-testable
            res["Island.Rim"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.18 });
        }
        else
        {
            GlassTint = null;
            res["Island.Background"] = Frozen(new SolidColorBrush(bg) { Opacity = opacity });
            res["Island.Rim"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.12 });
        }
        res["Island.Foreground"] = Frozen(new SolidColorBrush(fg));
        res["Island.Subtle"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.6 });
        res["Island.Hover"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.12 });
        res["Island.Accent"] = Frozen(new SolidColorBrush(accent));
        res["Island.AccentText"] = Frozen(new SolidColorBrush(AccentTextColor(accent, bg, fg)));
        res["Island.Surface"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.07 });
        res["Island.SelectedBackground"] = Frozen(new SolidColorBrush(Color.FromRgb(fg.R, fg.G, fg.B)));
        res["Island.SelectedForeground"] = Frozen(new SolidColorBrush(Color.FromRgb(bg.R, bg.G, bg.B)));
    }

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
