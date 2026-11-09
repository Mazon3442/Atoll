using System.ComponentModel;
using System.Windows.Media;
using Atoll.Activities;
using Atoll.Modules.Calendar;
using Atoll.Modules.Claude;
using Atoll.Modules.Clock;
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

    private void Attach()
    {
        Detach();
        if (_content is not null) Watch(_content);
        switch (_activity.Content)
        {
            case MediaViewModel media: Watch(media.Visualizer); break;
            case ClaudeViewModel claude when claude.Current is INotifyPropertyChanged current: Watch(current); break;
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
        if (_activity.Content is ClaudeViewModel claude && ReferenceEquals(sender, claude)
            && e.PropertyName == nameof(ClaudeViewModel.Current))
            Attach();
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
        GameViewModel => property == nameof(GameViewModel.GameName),
        NotificationItemViewModel => property is nameof(NotificationItemViewModel.Title) or nameof(NotificationItemViewModel.AppName),
        ProviderViewModel => property == nameof(ProviderViewModel.Activity),
        PrivacyViewModel => property == nameof(PrivacyViewModel.Title),
        ClaudeSessionViewModel => property == nameof(ClaudeSessionViewModel.AgentName),
        _ => true,
    };

    private void Update()
    {
        var (glyph, label, tint) = Describe();
        Glyph = glyph;
        Label = label.Length == 0 ? ModuleName(_activity.ModuleId) : label;
        if (_lastTint != tint)
        {
            _lastTint = tint;
            TintBrush = Solid(tint);
            SurfaceBrush = Surface(tint);
        }
    }

    private (string Glyph, string Label, Color Tint) Describe() => _activity.Content switch
    {
        TimerViewModel timer => ("timer", timer.Display, TimerOrange),
        NextEventViewModel next => ("calendar", next.Title, Parse(next.Color, _accent)),
        ClaudeViewModel claude => ("bot", claude.Current?.AgentName ?? "Claude", ClaudeOrange),
        ClaudeAlertViewModel alert => ("bot", alert.Title, ClaudeOrange),
        MediaViewModel media => ("music", media.Title, Parse(media.Visualizer.Color, _accent)),
        GameViewModel game => ("gamepad", game.GameName, _accent),
        NotificationItemViewModel note => ("bell", note.Title.Length > 0 ? note.Title : note.AppName, _accent),
        ProviderViewModel provider => ("plug", provider.Activity, _accent),
        PrivacyViewModel privacy => (privacy.Camera ? "video" : "mic", privacy.Title,
            privacy.Camera ? CameraGreen : MicrophoneOrange),
        IndicatorViewModel indicator => (Icon.ForTab(_activity.ModuleId), indicator.Title, _accent),
        CalendarAlertViewModel alert => ("calendar", alert.Title, Parse(alert.Color, _accent)),
        _ => (Icon.ForTab(_activity.ModuleId), ModuleName(_activity.ModuleId), _accent),
    };

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
        "system" => "System",
        "weather" => "Weather",
        "launcher" => "Launcher",
        _ => "ATOLL",
    };

    public void Dispose() => Detach();
}
