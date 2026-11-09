using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Atoll.Shell;

/// <summary>
/// A Lucide line icon (lucide.dev, ISC license), drawn from bundled 24×24 path data so it looks the same on every PC.
/// Takes its color from the inherited text color, like a glyph would.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public Icon() => SnapsToDevicePixels = true;

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (Geometry(Kind) is not { } geometry) return;
        var scale = Size / 24;
        var pen = new Pen(Foreground, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
    }

    private static readonly Dictionary<string, Geometry> Cache = [];

    public static Geometry? Geometry(string? kind)
    {
        if (kind is null || !Paths.TryGetValue(kind, out var data)) return null;
        if (Cache.TryGetValue(kind, out var cached)) return cached;
        var geometry = System.Windows.Media.Geometry.Parse(data);
        geometry.Freeze();
        return Cache[kind] = geometry;
    }

    private static string C(double cx, double cy, double r) =>
        FormattableString.Invariant($"M{cx - r} {cy}a{r} {r} 0 1 0 {2 * r} 0a{r} {r} 0 1 0 {-2 * r} 0");

    private static string R(double x, double y, double w, double h, double rx) => FormattableString.Invariant(
        $"M{x + rx} {y}h{w - 2 * rx}a{rx} {rx} 0 0 1 {rx} {rx}v{h - 2 * rx}a{rx} {rx} 0 0 1 {-rx} {rx}h{-(w - 2 * rx)}a{rx} {rx} 0 0 1 {-rx} {-rx}v{-(h - 2 * rx)}a{rx} {rx} 0 0 1 {rx} {-rx}z");

    internal static readonly Dictionary<string, string> Paths = new()
    {
        ["house"] = "M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8M3 10a2 2 0 0 1 .709-1.528l7-5.999a2 2 0 0 1 2.582 0l7 5.999A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
        ["music"] = "M9 18V5l12-2v13" + C(6, 18, 3) + C(18, 16, 3),
        ["bot"] = "M12 8V4H8" + R(4, 8, 16, 12, 2) + "M2 14h2M20 14h2M15 13v2M9 13v2",
        ["plug"] = "M12 22v-5M9 8V2M15 8V2M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8Z",
        ["bell"] = "M10.268 21a2 2 0 0 0 3.464 0M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326",
        ["timer"] = "M10 2h4M12 14l3-3" + C(12, 14, 8),
        ["clock"] = C(12, 12, 10) + "M12 6v6l4 2",
        ["calendar"] = R(3, 4, 18, 18, 2) + "M16 2v4M8 2v4M3 10h18",
        ["cpu"] = R(4, 4, 16, 16, 2) + R(9, 9, 6, 6, 1) + "M15 2v2M15 20v2M2 15h2M2 9h2M20 15h2M20 9h2M9 2v2M9 20v2",
        ["folder"] = "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z",
        ["clipboard"] = R(8, 2, 8, 4, 1) + "M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2",
        ["cloud"] = "M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z",
        ["sun"] = C(12, 12, 4) + "M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41",
        ["gamepad"] = "M6 11h4M8 9v4M15 12h.01M18 10h.01M17.32 5H6.68a4 4 0 0 0-3.978 3.59c-.006.052-.01.101-.017.152C2.604 9.416 2 14.456 2 16a3 3 0 0 0 3 3c1 0 1.5-.5 2-1l1.414-1.414A2 2 0 0 1 9.828 16h4.344a2 2 0 0 1 1.414.586L17 18c.5.5 1 1 2 1a3 3 0 0 0 3-3c0-1.545-.604-6.584-.685-7.258-.007-.05-.011-.1-.017-.151A4 4 0 0 0 17.32 5z",
        ["play"] = "M6 3l14 9-14 9z",
        ["pause"] = R(14, 4, 4, 16, 1) + R(6, 4, 4, 16, 1),
        ["skip-forward"] = "M5 4l10 8-10 8zM19 5v14",
        ["skip-back"] = "M19 20L9 12l10-8zM5 19V5",
        ["volume"] = "M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298zM16 9a5 5 0 0 1 0 6M19.364 18.364a9 9 0 0 0 0-12.728",
        ["volume-off"] = "M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298zM22 9l-6 6M16 9l6 6",
        ["mic-vocal"] = "m11 7.6-6 8.19a1 1 0 0 0 .1 1.3l.82.82a1 1 0 0 0 1.31.09L15.09 12M16.5 21.17C15.5 20.5 14.37 20 13 20c-2.06 0-3.93 2.36-6 2-2.07-.36-2.78-3.37-1.5-4.5" + C(16, 7, 5),
        ["mic"] = "M12 19v3M19 10v2a7 7 0 0 1-14 0v-2" + R(9, 2, 6, 13, 3),
        ["mic-off"] = "M12 19v3M15 9.34V5a3 3 0 0 0-5.68-1.33M16.95 16.95A7 7 0 0 1 5 12v-2M18.89 13.23A7 7 0 0 0 19 12v-2M2 2l20 20M9 9v3a3 3 0 0 0 5.12 2.12",
        ["video"] = "M16 13l5.223 3.482a.5.5 0 0 0 .777-.416V7.87a.5.5 0 0 0-.752-.432L16 10.5" + R(2, 6, 14, 12, 2),
        ["camera"] = "M14.5 4h-5L7 7H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3l-2.5-3z" + C(12, 13, 3),
        ["lock"] = R(3, 11, 18, 11, 2) + "M7 11V7a5 5 0 0 1 10 0v4",
        ["battery"] = R(2, 7, 16, 10, 2) + "M22 11v2",
        ["monitor-off"] = "M17 17H4a2 2 0 0 1-2-2V5c0-1.5 1-2 1-2M22 15V5a2 2 0 0 0-2-2H9M8 21h8M12 17v4M2 2l20 20",
        ["download"] = "M12 15V3M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5",
        ["settings"] = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z" + C(12, 12, 3),
        ["moon"] = "M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z",
        ["bluetooth"] = "M7 7l10 10-5 5V2l5 5L7 17",
        ["server"] = R(2, 2, 20, 8, 2) + R(2, 14, 20, 8, 2) + "M6 6h.01M6 18h.01",
        ["activity"] = "M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2",
        ["chevron-left"] = "M15 18l-6-6 6-6",
        ["chevron-right"] = "M9 18l6-6-6-6",
        ["more"] = C(12, 12, 1) + C(19, 12, 1) + C(5, 12, 1),
        ["grid"] = R(3, 3, 7, 7, 1) + R(14, 3, 7, 7, 1) + R(14, 14, 7, 7, 1) + R(3, 14, 7, 7, 1),
        ["refresh"] = "M21 12a9 9 0 1 1-2.64-6.36M21 3v6h-6",
        ["pencil"] = "M17 3a2.85 2.85 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z",
        ["notebook"] = R(5, 2, 14, 20, 2) + "M9 2v20M2 6h3M2 12h3M2 18h3",
        ["rocket"] = "M4.5 16.5c-1.5 1.26-2 5-2 5s3.74-.5 5-2c.71-.84.7-2.13-.09-2.91a2.18 2.18 0 0 0-2.91-.09zM12 15l-3-3a22 22 0 0 1 2-3.95A12.88 12.88 0 0 1 22 2c0 2.72-.78 7.5-6 11a22.35 22.35 0 0 1-4 2zM9 12H4s.55-3.03 2-4c1.62-1.08 5 0 5 0M12 15v5s3.03-.55 4-2c1.08-1.62 0-5 0-5",
    };

    /// <summary>The icon for a tab in the island header.</summary>
    public static string ForTab(string? id) => id switch
    {
        "home" => "house",
        "claude" => "bot",
        "providers" => "plug",
        "media" => "music",
        "notifications" => "bell",
        "clock" => "timer",
        "calendar" => "calendar",
        "system" => "cpu",
        "shelf" => "folder",
        "clipboard" => "clipboard",
        "notes" => "notebook",
        "weather" => "cloud",
        "launcher" => "grid",
        "games" => "gamepad",
        _ => "more",
    };
}

/// <summary>"Pause" while a timer runs, "Resume" while it is paused.</summary>
public sealed class PauseLabel : IValueConverter
{
    public static PauseLabel Instance { get; } = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "Pause" : "Resume";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Collapsed for null, so a chip disappears when its feature is off.</summary>
public sealed class NotNullToVisibility : IValueConverter
{
    public static NotNullToVisibility Instance { get; } = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TabIconConverter : IValueConverter
{
    public static TabIconConverter Instance { get; } = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Icon.ForTab(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
