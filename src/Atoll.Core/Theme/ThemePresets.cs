namespace Atoll.Theme;

public static class ThemePresets
{
    public static IReadOnlyList<ThemePreset> All { get; } =
    [
        new("Midnight", "#000000", "#FFFFFF", "#0A84FF"),
        new("Graphite", "#1C1C1E", "#F2F2F7", "#FF9F0A"),
        new("Ocean", "#0B1E33", "#E6F1FF", "#64D2FF"),
        new("Forest", "#0F1F17", "#E8F5EC", "#30D158"),
        new("Paper", "#F5F5F7", "#1C1C1E", "#007AFF"),
        new("Frosted Glass", "#59161618", "#FFFFFF", "#7ECEFD", Glass: true),
        new("Frosted Light", "#8CF4F4F8", "#0B132B", "#2E6BEF", Glass: true),
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
}
