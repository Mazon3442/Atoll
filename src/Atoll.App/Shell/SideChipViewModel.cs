using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Atoll.Activities;
using Atoll.Modules.Calendar;
using Atoll.Modules.Claude;
using Atoll.Modules.Clock;
using Atoll.Modules.Downloads;
using Atoll.Modules.Games;
using Atoll.Modules.Media;
using Atoll.Modules.Notifications;
using Atoll.Modules.Privacy;
using Atoll.Modules.Providers;
using Atoll.Theme;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Shell;

/// <summary>
/// One of the small capsules beside the main island. It keeps a short live label and a module tint,
/// so four modules can sit side by side without shrinking to bare glyphs.
/// </summary>
public sealed partial class SideChipViewModel : ObservableObject, IDisposable
{
    private static readonly Color ClaudeOrange = Color.FromRgb(0xD9, 0x77, 0x57);
    private static readonly Color TimerOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color CameraGreen = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color MicrophoneOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private readonly Activity _activity;
    private readonly Color _accent;
    private readonly INotifyPropertyChanged? _content;
    private readonly List<INotifyPropertyChanged> _watched = [];
    private Color? _lastTint;

    public SideChipViewModel(Activity activity, Color accent)
    {
        _activity = activity;
        _accent = accent;
        _content = activity.Content as INotifyPropertyChanged;
        Attach();
        Update();
    }

    public Activity Activity => _activity;
    public string ModuleId => _activity.ModuleId;

    [ObservableProperty] private string _glyph = "more";
    [ObservableProperty] private string _label = "ATOLL";
    [ObservableProperty] private Brush _tintBrush = Brushes.White;
    [ObservableProperty] private Brush _surfaceBrush = Brushes.Transparent;
    [ObservableProperty] private bool _isRing;
    [ObservableProperty] private string _ringText = "";
    [ObservableProperty] private double _ringProgress;
    [ObservableProperty] private Geometry _ringArc = Geometry.Empty;
    [ObservableProperty] private bool _showLabel = true;
    private bool _wide;

    /// <summary>Wide capsules only when 3-4 activities need labels; one or two become round dots.</summary>
    public void SetWide(bool wide)
    {
        _wide = wide;
        ShowLabel = wide && !IsRing;
    }

    private void Attach()
    {
        Detach();
        if (_content is not null) Watch(_content);
        switch (_activity.Content)
        {
            case MediaViewModel media: Watch(media.Visualizer); break;
            case ClaudeViewModel claude:
                Watch(claude.Usage);
                foreach (var limit in claude.Usage.Limits) Watch(limit);
                if (claude.Current is INotifyPropertyChanged current) Watch(current);
                break;
        }
    }

    private void Watch(INotifyPropertyChanged value)
    {
        if (_watched.Contains(value)) return;
        value.PropertyChanged += ContentChanged;
        _watched.Add(value);
    }

    private void Detach()
    {
        foreach (var value in _watched) value.PropertyChanged -= ContentChanged;
        _watched.Clear();
    }

    private void ContentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_activity.Content is ClaudeViewModel claude)
        {
            if (ReferenceEquals(sender, claude) && e.PropertyName == nameof(ClaudeViewModel.Current)) Attach();
            else if (ReferenceEquals(sender, claude.Usage) && e.PropertyName == nameof(UsageViewModel.HasLimits)) Attach();
        }
        if (!IsRelevant(sender, e.PropertyName)) return;
        Update();
    }

    /// <summary>A busy view model (media progress, timer ticks) should not repaint the capsule on every change.</summary>
    private static bool IsRelevant(object? sender, string? property) => sender switch
    {
        VisualizerViewModel => property == nameof(VisualizerViewModel.Color),
        MediaViewModel => property == nameof(MediaViewModel.Title),
        TimerViewModel => property == nameof(TimerViewModel.Display),
        NextEventViewModel => property is nameof(NextEventViewModel.Title) or nameof(NextEventViewModel.Color),
        ClaudeViewModel => property == nameof(ClaudeViewModel.Current),
        UsageViewModel => property is nameof(UsageViewModel.HasLimits) or nameof(UsageViewModel.HasData),
        PlanLimitRow => property is nameof(PlanLimitRow.Progress) or nameof(PlanLimitRow.PercentText) or nameof(PlanLimitRow.ResetText),
        GameViewModel => property == nameof(GameViewModel.GameName),
        NotificationItemViewModel => property is nameof(NotificationItemViewModel.Title) or nameof(NotificationItemViewModel.AppName),
        ProviderViewModel => property == nameof(ProviderViewModel.Activity),
        DownloadsViewModel => property == nameof(DownloadsViewModel.Summary),
        PrivacyViewModel => property == nameof(PrivacyViewModel.Title),
        ClaudeSessionViewModel => property == nameof(ClaudeSessionViewModel.AgentName),
        _ => true,
    };

    private void Update()
    {
        var (glyph, label, tint, ringText, ringProgress) = Describe();
        Glyph = glyph;
        Label = label.Length == 0 ? ModuleName(_activity.ModuleId) : label;
        IsRing = ringText is not null;
        RingText = ringText ?? "";
        RingProgress = ringProgress;
        RingArc = IsRing ? Arc(ringProgress) : Geometry.Empty;
        ShowLabel = _wide && !IsRing;
        if (_lastTint != tint)
        {
            _lastTint = tint;
            TintBrush = Solid(tint);
            SurfaceBrush = Surface(tint);
        }
    }

    private (string Glyph, string Label, Color Tint, string? RingText, double RingProgress) Describe() => _activity.Content switch
    {
        TimerViewModel timer => ("timer", timer.Display, TimerOrange, CompactTime(timer),
            timer.Kind == TimerKind.Stopwatch ? 1 : 1 - timer.Progress),
        ClaudeViewModel claude when BestLimit(claude.Usage) is { } limit => ("bot",
            $"Claude {limit.Name} limit · {Math.Round(limit.Progress * 100):0}% used", ClaudeOrange,
            $"{Math.Round(limit.Progress * 100):0}%", limit.Progress),
        NextEventViewModel next => ("calendar", next.Title, Parse(next.Color, _accent), null, 0),
        ClaudeViewModel claude => ("bot", claude.Current?.AgentName ?? "Claude", ClaudeOrange, null, 0),
        ClaudeAlertViewModel alert => ("bot", alert.Title, ClaudeOrange, null, 0),
        MediaViewModel media => ("music", media.Title, Parse(media.Visualizer.Color, _accent), null, 0),
        GameViewModel game => ("gamepad", game.GameName, _accent, null, 0),
        NotificationItemViewModel note => ("bell", note.Title.Length > 0 ? note.Title : note.AppName, _accent, null, 0),
        ProviderViewModel provider => ("plug", provider.Activity, _accent, null, 0),
        PrivacyViewModel privacy => (privacy.Camera ? "video" : "mic", privacy.Title,
            privacy.Camera ? CameraGreen : MicrophoneOrange, null, 0),
        DownloadsViewModel downloads => ("download", downloads.Summary, _accent, null, 0),
        IndicatorViewModel indicator => (Icon.ForTab(_activity.ModuleId), indicator.Title, _accent, null, 0),
        CalendarAlertViewModel alert => ("calendar", alert.Title, Parse(alert.Color, _accent), null, 0),
        _ => (Icon.ForTab(_activity.ModuleId), ModuleName(_activity.ModuleId), _accent, null, 0),
    };

    /// <summary>The Claude limit closest to its cap. Codex limits share the list but belong to another agent.</summary>
    private static PlanLimitRow? BestLimit(UsageViewModel usage) =>
        usage.Limits.Where(limit => !limit.Name.StartsWith("Codex", StringComparison.Ordinal)).MaxBy(limit => limit.Progress);

    /// <summary>Short enough to sit inside a 30 px ring.</summary>
    private static string CompactTime(TimerViewModel timer)
    {
        if (timer.Kind == TimerKind.Stopwatch) return timer.Display;
        var left = TimeSpan.FromSeconds(Math.Ceiling(timer.Remaining.TotalSeconds));
        return left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}" : $"{left.Minutes}:{left.Seconds:00}";
    }

    private static Geometry Arc(double remaining)
    {
        remaining = Math.Clamp(remaining, 0, 1);
        const double size = 30, center = size / 2, radius = center - 2;
        if (remaining <= 0.004) return Geometry.Empty;
        if (remaining >= 0.996) return Freeze(new EllipseGeometry(new Point(center, center), radius, radius));

        var end = (-90 + 360 * remaining) * Math.PI / 180;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center, center - radius), false, false);
            context.ArcTo(new Point(center + radius * Math.Cos(end), center + radius * Math.Sin(end)),
                new Size(radius, radius), 0, remaining > 0.5, SweepDirection.Clockwise, true, false);
        }
        return Freeze(geometry);
    }

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    private static Color Parse(string? value, Color fallback) =>
        ThemeService.TryParseColor(value, out var color) ? color : fallback;

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush Surface(Color color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static string ModuleName(string moduleId) => moduleId switch
    {
        "clock" => "Timer",
        "calendar" => "Calendar",
        "claude" => "Agents",
        "media" => "Media",
        "games" => "Game",
        "notifications" => "Alerts",
        "providers" => "API",
        "privacy" => "Privacy",
        "shelf" => "Files",
        "downloads" => "Downloads",
        "system" => "System",
        "weather" => "Weather",
        "launcher" => "Launcher",
        _ => "ATOLL",
    };

    public void Dispose() => Detach();
}
