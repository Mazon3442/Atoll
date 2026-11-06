namespace Island.Activities;

/// <summary>
/// Generic short-lived status (volume, battery, timer done, ...). Shared templates live in Theme/Indicators.xaml.
/// </summary>
/// <param name="Glyph">Segoe Fluent Icons character.</param>
/// <param name="Title">Main text, e.g. "Caps Lock on".</param>
/// <param name="Detail">Optional second line shown in the peek view.</param>
/// <param name="Value">Optional 0–1 level drawn as a bar (volume, brightness, battery).</param>
public sealed record IndicatorViewModel(string Glyph, string Title, string? Detail = null, double? Value = null)
{
    public bool HasValue => Value is not null;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}
