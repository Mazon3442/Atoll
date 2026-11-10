using System.Collections.ObjectModel;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules.Actions;
using Atoll.Modules.Calendar;
using Atoll.Modules.Claude;
using Atoll.Modules.Clipboard;
using Atoll.Modules.Downloads;
using Atoll.Modules.Shelf;
using Atoll.Modules.Stats;
using Atoll.Modules.Weather;
using Atoll.Modules.Clock;
using Atoll.Modules.Indicators;
using Atoll.Modules.Media;
using Atoll.Modules.Notifications;
using Atoll.Modules.Notes;
using Atoll.Modules.Privacy;
using Atoll.Modules.Providers;
using Atoll.Modules.Tide;
using Atoll.Settings;

namespace Atoll.Modules;

public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleDescriptor> All { get; } =
    [
        new("claude", "Agents", "", () => new ClaudeModule()),
        // Off until the user adds an API key in Settings.
        new("providers", "API keys", "", () => new ProviderModule(), EnabledByDefault: false, BookmarkedByDefault: false),
        new("media", "Media", "", () => new MediaModule()),
        new("notifications", "Notifications", "", () => new NotificationsModule()),
        new("clock", "Clock & timer", "", () => new ClockModule(), BookmarkedByDefault: false),
        new("calendar", "Calendar", "\uE787", () => new CalendarModule()),
        new("notes", "Notes", "\uE70B", () => new NotesModule(), BookmarkedByDefault: false),
        new("tide", "Tide", "\uE8A5", () => new TideModule(), BookmarkedByDefault: false),
        new("system", "System", "", () => new IndicatorsModule(), BookmarkedByDefault: false),
        new("shelf", "File shelf", "", () => new ShelfModule()),
        new("downloads", "Downloads", "\uE896", () => new DownloadsModule(), BookmarkedByDefault: false),
        new("clipboard", "Clipboard history", "", () => new ClipboardModule()),
        new("stats", "System stats", "", () => new StatsModule(), BookmarkedByDefault: false),
        new("weather", "Weather", "", () => new WeatherModule()),
        new("actions", "Quick actions", "", () => new QuickActionsModule(), BookmarkedByDefault: false),
        new("privacy", "Camera and mic in use", "", () => new PrivacyModule(), BookmarkedByDefault: false),
        new("launcher", "Launcher", "\uE71D", () => new Launcher.LauncherModule(), BookmarkedByDefault: false),
        new("games", "Games", "", () => new Games.GamesModule(), BookmarkedByDefault: false),
    ];
}

public sealed record ModuleTab(ModuleDescriptor Descriptor, object Panel)
{
    public bool Contains(string id) => Descriptor.Id == PanelGroups.TabId(id);
}

/// <summary>Starts/stops modules to match settings and exposes the expanded-panel tabs. UI thread only.</summary>
public sealed class ModuleManager(
    IReadOnlyList<ModuleDescriptor> catalog,
    ActivityHost activities,
    Dispatcher dispatcher,
    SettingsService settings) : IDisposable
{
    private readonly Dictionary<string, IIslandModule> _running = [];
    private string _homeKey = "";

    public IReadOnlyList<ModuleDescriptor> Catalog => catalog;

    public ObservableCollection<ModuleTab> Tabs { get; } = [];

    /// <summary>Raised after <see cref="Apply"/>, so the island can refit the header when placement changes.</summary>
    public event Action? LayoutChanged;

    /// <summary>True when the tab sits in the island's top row rather than the ⋯ menu.</summary>
    public bool IsBookmarked(ModuleTab tab)
    {
        if (tab.Descriptor.Id == "home") return settings.Current.Home.Bookmarked != false;
        var members = settings.Current.Modules
            .Where(entry => entry.Enabled && PanelGroups.TabId(entry.Id) == tab.Descriptor.Id)
            .ToList();
        // A tab supplied without settings entries (tests, embedded hosts) behaves like a new feature.
        return members.Count == 0 || members.Any(IsEntryBookmarked);
    }

    private bool IsEntryBookmarked(ModuleEntry entry) =>
        entry.Bookmarked ?? catalog.FirstOrDefault(d => d.Id == entry.Id)?.BookmarkedByDefault ?? true;

    /// <summary>Pins a tab to the top row, including every enabled feature that shares it.</summary>
    public void Bookmark(ModuleTab tab)
    {
        if (tab.Descriptor.Id == "home") settings.Current.Home.SetPlacement(FeaturePlacement.Bookmarked);
        else
        {
            foreach (var entry in settings.Current.Modules.Where(entry => PanelGroups.TabId(entry.Id) == tab.Descriptor.Id))
                entry.SetPlacement(FeaturePlacement.Bookmarked);
        }
        Apply();
        settings.NotifyChanged();
    }

    public void Apply()
    {
        var entries = settings.Current.Modules;
        foreach (var d in catalog.Where(d => entries.All(e => e.Id != d.Id)))
            entries.Add(new ModuleEntry { Id = d.Id, Enabled = d.EnabledByDefault, Bookmarked = d.BookmarkedByDefault });

        var wanted = entries
            .Where(e => e.Enabled)
            .Select(e => catalog.FirstOrDefault(d => d.Id == e.Id))
            .OfType<ModuleDescriptor>()
            .ToList();

        foreach (var id in _running.Keys.Where(id => wanted.All(d => d.Id != id)).ToList())
            Stop(id);

        foreach (var d in wanted.Where(d => !_running.ContainsKey(d.Id)))
        {
            var module = d.Create();
            try
            {
                module.Start(new ModuleContext(d.Id, activities, dispatcher, settings));
                _running[d.Id] = module;
            }
            catch (Exception ex)
            {
                Log.Error($"Module '{d.Id}' failed to start", ex);
                module.Dispose();
                activities.RemoveModule(d.Id);
            }
        }

        var tabs = wanted
            .Where(d => _running.TryGetValue(d.Id, out var m) && m.Panel is not null)
            .Select(d => new ModuleTab(d, _running[d.Id].Panel!))
            .ToList();
        var home = settings.Current.Home;
        tabs = PanelGroups.Combine(tabs, home);
        // Home is rebuilt each time, so compare the other tabs and Home's card choices instead.
        var homeKey = $"{home.Enabled}{home.Media}{home.Weather}{home.Timer}{home.Calendar}{home.UpNext}{home.Game}";
        if (homeKey != _homeKey || tabs.Count != Tabs.Count
            || !tabs.Where(t => t.Descriptor.Id != "home").SequenceEqual(Tabs.Where(t => t.Descriptor.Id != "home")))
        {
            _homeKey = homeKey;
            Tabs.Clear();
            tabs.ForEach(Tabs.Add);
        }
        LayoutChanged?.Invoke();
    }

    /// <summary>Tells the module whose tab is on screen that the user can see it now.</summary>
    public void NotifyPanelShown(string tabId)
    {
        if (_running.TryGetValue(tabId, out var module)) Invoke(() => module.OnPanelShown());
    }

    /// <summary>Tells the module whose tab was on screen that it no longer is.</summary>
    public void NotifyPanelHidden(string tabId)
    {
        if (_running.TryGetValue(tabId, out var module)) Invoke(() => module.OnPanelHidden());
    }

    private static void Invoke(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("A module failed while being shown or hidden", ex); }
    }

    private void Stop(string id)
    {        try { _running[id].Dispose(); }
        catch (Exception ex) { Log.Error($"Module '{id}' failed to stop", ex); }
        _running.Remove(id);
        activities.RemoveModule(id);
    }

    public void Dispose()
    {
        foreach (var id in _running.Keys.ToList()) Stop(id);
    }
}
