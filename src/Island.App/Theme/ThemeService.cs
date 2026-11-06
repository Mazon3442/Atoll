using System.Windows;
using System.Windows.Media;
using Island.Settings;

namespace Island.Theme;

public sealed record ThemePreset(string Name, string Background, string Foreground, string Accent);

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
    ];

    public static void Apply(ThemeSettings theme)
    {
        var defaults = new ThemeSettings();
        var bg = ParseColor(theme.Background, defaults.Background);
        var fg = ParseColor(theme.Foreground, defaults.Foreground);
        var accent = ParseColor(theme.Accent, defaults.Accent);
        var opacity = Math.Clamp(theme.Opacity, 0.3, 1);

        var res = Application.Current.Resources;
        res["Island.Background"] = Frozen(new SolidColorBrush(bg) { Opacity = opacity });
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
