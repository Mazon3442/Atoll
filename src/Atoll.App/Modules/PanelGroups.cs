using Atoll.Modules.Actions;
using Atoll.Modules.Calendar;
using Atoll.Modules.Clipboard;
using Atoll.Modules.Clock;
using Atoll.Modules.Games;
using Atoll.Modules.Indicators;
using Atoll.Modules.Launcher;
using Atoll.Modules.Media;
using Atoll.Modules.Shelf;
using Atoll.Modules.Stats;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules;

public sealed record MediaPanelViewModel(MediaViewModel? Player, SystemPanelViewModel? Sound);
public sealed record TimePanelViewModel(ClockPanelViewModel? Clock, CalendarViewModel? Calendar);
public sealed record FilesPanelViewModel(ShelfViewModel? Shelf, ClipboardViewModel? Clipboard);
public sealed record SystemGroupViewModel(SystemPanelViewModel? Devices, StatsViewModel? Stats,
    QuickActionsViewModel? Actions, SystemPanelViewModel? Sound);

/// <summary>The Launcher tab: Start Menu apps and the Games section share one tab.</summary>
public sealed partial class LauncherPanelViewModel(LauncherViewModel? apps, GameViewModel? games) : ObservableObject
{
    public LauncherViewModel? Apps { get; } = apps;
    public GameViewModel? Games { get; } = games;

    public bool HasApps => Apps is not null;
    public bool HasGames => Games is not null;
    public bool ShowSwitch => HasApps && HasGames;
    public IReadOnlyList<string> Sections { get; } = ["Apps", "Games"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApps), nameof(IsGames))]
    private int _sectionIndex = apps is null ? 1 : 0;

    public bool IsApps => SectionIndex == 0;
    public bool IsGames => SectionIndex == 1;

    [RelayCommand] private void ShowApps() => SectionIndex = 0;
    [RelayCommand] private void ShowGames() => SectionIndex = 1;

    // The selected sub-tab is presentation state; two tab lists with the same panels are the same tab.
    public override bool Equals(object? obj) =>
        obj is LauncherPanelViewModel other && ReferenceEquals(Apps, other.Apps) && ReferenceEquals(Games, other.Games);

    public override int GetHashCode() => HashCode.Combine(Apps, Games);
}

internal static class PanelGroups
{
    internal static string TabId(string id) => id switch
    {
        "agents" => "claude",
        "files" => "shelf",
        "stats" or "actions" or "privacy" => "system",
        "games" => "launcher",
        _ => id,
    };

    public static List<ModuleTab> Combine(IReadOnlyList<ModuleTab> modules, HomeSettings? home = null)
    {
        T? Panel<T>(string id) where T : class => modules.FirstOrDefault(t => t.Descriptor.Id == id)?.Panel as T;
        var result = new List<ModuleTab>();
        var added = new HashSet<string>();
        foreach (var module in modules)
        {
            var id = module.Descriptor.Id;
            var group = TabId(id);
            if (!added.Add(group)) continue;
            (string Name, object Panel) combined = group switch
            {
                "media" => ("Media", new MediaPanelViewModel(Panel<MediaViewModel>("media"), Panel<SystemPanelViewModel>("system"))),
                "clock" => ("Timer", Panel<ClockPanelViewModel>("clock")!),
                "calendar" => ("Calendar", Panel<CalendarViewModel>("calendar")!),
                "launcher" => ("Launcher", new LauncherPanelViewModel(Panel<LauncherViewModel>("launcher"), Panel<GameViewModel>("games"))),
                "shelf" => ("Files", Panel<ShelfViewModel>("shelf")!),
                "clipboard" => ("Clipboard", Panel<ClipboardViewModel>("clipboard")!),
                "system" => ("System", new SystemGroupViewModel(Panel<SystemPanelViewModel>("system"), Panel<StatsViewModel>("stats"),
                    Panel<QuickActionsViewModel>("actions"), Panel<MediaViewModel>("media") is null ? Panel<SystemPanelViewModel>("system") : null)),
                _ => (module.Descriptor.Name, module.Panel),
            };
            result.Add(new ModuleTab(module.Descriptor with { Id = group, Name = combined.Name }, combined.Panel));
        }
        if (home is { Enabled: true } && result.Count > 0)
            result.Insert(0, new ModuleTab(new ModuleDescriptor("home", "Home", "\uE80F", static () => throw new NotSupportedException()),
                new Home.HomeViewModel(Panel<MediaViewModel>("media"), Panel<Weather.WeatherViewModel>("weather"), Panel<ClockPanelViewModel>("clock"),
                    Panel<CalendarViewModel>("calendar"), Panel<Games.GameViewModel>("games"), home)));
        return result;
    }
}
