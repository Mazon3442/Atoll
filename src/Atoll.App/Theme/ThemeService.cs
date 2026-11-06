using System.Windows;
using System.Windows.Media;
using Atoll.Settings;

namespace Atoll.Theme;

/// <param name="Glass">Frosted glass: the desktop behind the island is blurred and tinted with <paramref name="Background"/> (its alpha sets the tint strength).</param>
public sealed record ThemePreset(string Name, string Background, string Foreground, string Accent, bool Glass = false);

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
            res["Island.Rim"] = Frozen(new SolidColorBrush(Colors.Transparent));
        }
        res["Island.Foreground"] = Frozen(new SolidColorBrush(fg));
        res["Island.Subtle"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.6 });
        res["Island.Hover"] = Frozen(new SolidColorBrush(fg) { Opacity = 0.12 });
        res["Island.Accent"] = Frozen(new SolidColorBrush(accent));
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
