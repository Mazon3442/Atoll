using System.Collections.ObjectModel;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules.Actions;
using Atoll.Modules.Calendar;
using Atoll.Modules.Claude;
using Atoll.Modules.Clipboard;
using Atoll.Modules.Shelf;
using Atoll.Modules.Stats;
using Atoll.Modules.Weather;
using Atoll.Modules.Clock;
using Atoll.Modules.Indicators;
using Atoll.Modules.Media;
using Atoll.Modules.Notifications;
using Atoll.Modules.Privacy;
using Atoll.Modules.Providers;
using Atoll.Settings;

namespace Atoll.Modules;

public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleDescriptor> All { get; } =
    [
        new("claude", "Agents", "", () => new ClaudeModule()),
        // Off until the user adds an API key in Settings.
        new("providers", "API keys", "", () => new ProviderModule(), EnabledByDefault: false),
        new("media", "Media", "", () => new MediaModule()),
        new("notifications", "Notifications", "", () => new NotificationsModule()),
        new("clock", "Clock & timer", "", () => new ClockModule()),
        new("calendar", "Calendar", "\uE787", () => new CalendarModule()),
        new("notes", "Notes", "\uE70B", () => new Notes.NotesModule()),
        new("system", "System", "", () => new IndicatorsModule()),
        new("shelf", "File shelf", "", () => new ShelfModule()),
        new("clipboard", "Clipboard history", "", () => new ClipboardModule()),
        new("stats", "System stats", "", () => new StatsModule()),
        new("weather", "Weather", "", () => new WeatherModule()),
        new("actions", "Quick actions", "", () => new QuickActionsModule()),
        new("privacy", "Camera and mic in use", "", () => new PrivacyModule()),
        new("launcher", "Launcher", "\uE71D", () => new Launcher.LauncherModule()),
        new("games", "Games", "", () => new Games.GamesModule()),
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

    private static readonly ModuleDescriptor HomeTab = new("home", "Home", "\uE80F",
        static () => throw new NotSupportedException());

    public IReadOnlyList<ModuleDescriptor> Catalog => catalog;

    public ObservableCollection<ModuleTab> Tabs { get; } = [];

    /// <summary>Features switched off in Settings, including Home; these are the 3-dot menu's entries.</summary>
    public IReadOnlyList<ModuleDescriptor> DisabledFeatures()
    {
        var off = catalog
            .Where(d => settings.Current.Modules.FirstOrDefault(e => e.Id == d.Id)?.Enabled != true)
            .ToList();
        if (!settings.Current.Home.Enabled) off.Insert(0, HomeTab);
        return off;
    }

    /// <summary>Turns a feature back on from the overflow menu.</summary>
    public void EnableFeature(ModuleDescriptor descriptor)
    {
        if (descriptor.Id == "home") settings.Current.Home.Enabled = true;
        else
        {
            var entry = settings.Current.Modules.FirstOrDefault(e => e.Id == descriptor.Id);
            if (entry is null) settings.Current.Modules.Add(new ModuleEntry { Id = descriptor.Id, Enabled = true });
            else entry.Enabled = true;
        }
        Apply();
        settings.NotifyChanged();
    }

    public void Apply()
    {
        var entries = settings.Current.Modules;
        foreach (var d in catalog.Where(d => entries.All(e => e.Id != d.Id)))
            entries.Add(new ModuleEntry { Id = d.Id, Enabled = d.EnabledByDefault });

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
    }

    private void Stop(string id)
    {
        try { _running[id].Dispose(); }
        catch (Exception ex) { Log.Error($"Module '{id}' failed to stop", ex); }
        _running.Remove(id);
        activities.RemoveModule(id);
    }

    public void Dispose()
    {
        foreach (var id in _running.Keys.ToList()) Stop(id);
    }
}
