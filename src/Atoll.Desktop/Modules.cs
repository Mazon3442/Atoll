using System.Collections.ObjectModel;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Modules.Calendar;
using Atoll.Modules.Claude;
using Atoll.Modules.Clock;
using Atoll.Modules.Shelf;
using Atoll.Modules.Weather;
using Atoll.Modules.Media;
using Atoll.Modules.Indicators;
using Atoll.Modules.Actions;
using Atoll.Settings;
using Avalonia.Threading;

namespace Atoll.Desktop;

public sealed record ModuleTab(string Id, string Name, object Panel)
{
    public bool Contains(string id) => Id == id || Panel is PanelGroup group && group.Modules.Any(m => m.Id == id);
}
public sealed record PanelGroup(IReadOnlyList<ModuleTab> Modules);

public sealed class DesktopModules : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ActivityHost _activities;
    private readonly Dictionary<string, IIslandModule> _running = [];
    private readonly Dictionary<string, ModuleTab> _panels = [];
    public ObservableCollection<ModuleTab> Tabs { get; } = [];

    public static IReadOnlyList<(string Id, string Name, Func<IIslandModule> Create)> Catalog { get; } =
    [
        ("claude", "Agents", () => new ClaudeModule()),
        ("media", "Media", () => new MediaModule()),
        ("clock", "Clock & timer", () => new ClockModule()),
        ("calendar", "Calendar", () => new CalendarModule()),
        ("weather", "Weather", () => new WeatherModule()),
        ("shelf", "File shelf", () => new ShelfModule()),
        ("clipboard", "Clipboard", () => new ClipboardModule()),
        ("system", "Devices", () => new IndicatorsModule()),
        ("stats", "System stats", () => new StatisticsModule()),
        ("actions", "Quick actions", () => new QuickActionsModule()),
#if WINDOWS
        ("notifications", "Notifications", () => new Atoll.Modules.Notifications.NotificationsModule()),
#endif
    ];

    public DesktopModules(SettingsService settings, ActivityHost activities)
    {
        _settings = settings;
        _activities = activities;
        settings.Changed += Apply;
        Apply();
    }

    private void Apply()
    {
        foreach (var (id, _, _) in Catalog.Where(d => _settings.Current.Modules.All(e => e.Id != d.Id)))
            _settings.Current.Modules.Add(new ModuleEntry { Id = id });
        var wanted = _settings.Current.Modules.Where(e => e.Enabled).Select(e => e.Id).ToHashSet();
        foreach (var id in _running.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            _running[id].Dispose();
            _running.Remove(id);
            _activities.RemoveModule(id);
            _panels.Remove(id);
        }
        foreach (var (id, name, create) in Catalog.Where(d => wanted.Contains(d.Id) && !_running.ContainsKey(d.Id)))
        {
            var module = create();
            try
            {
                module.Start(new ModuleContext(id, _activities, Dispatcher.UIThread, _settings));
                _running.Add(id, module);
                if (module.Panel is { } panel) _panels[id] = new ModuleTab(id, name, panel);
            }
            catch (Exception ex)
            {
                module.Dispose();
                _activities.RemoveModule(id);
                Log.Error($"Could not start {name}", ex);
                _panels[id] = new ModuleTab(id, name, new ModuleError($"{name} could not start. Restart ATOLL or check its log."));
            }
        }
        foreach (var id in _panels.Keys.Where(id => !wanted.Contains(id)).ToList()) _panels.Remove(id);
        var groups = _settings.Current.Modules.Where(e => e.Enabled && _panels.ContainsKey(e.Id)).Select(e => _panels[e.Id])
            .GroupBy(p => p.Id switch { "calendar" => "clock", "clipboard" => "shelf", "stats" or "actions" => "system", _ => p.Id })
            .Select(g => new ModuleTab(g.Key, g.Key switch { "claude" => "Agents", "clock" => "Clock", "shelf" => "Files", "system" => "System", "notifications" => "Alerts", _ => g.First().Name },
                g.Count() == 1 ? g.First().Panel : new PanelGroup(g.ToArray()))).ToList();
        var changed = groups.Count != Tabs.Count || groups.Where((g, i) => i >= Tabs.Count || g.Id != Tabs[i].Id || !SamePanel(g.Panel, Tabs[i].Panel)).Any();
        if (changed)
        {
            Tabs.Clear(); foreach (var group in groups) Tabs.Add(group);
        }
    }

    private static bool SamePanel(object left, object right) => ReferenceEquals(left, right)
        || left is PanelGroup a && right is PanelGroup b && a.Modules.SequenceEqual(b.Modules);

    public void Dispose()
    {
        _settings.Changed -= Apply;
        foreach (var module in _running.Values) module.Dispose();
        _running.Clear();
    }
}

public sealed record ModuleError(string Message);
