using System.Collections.ObjectModel;
using System.Windows.Threading;
using Island.Activities;
using Island.Modules.Claude;
using Island.Modules.Clock;
using Island.Modules.Indicators;
using Island.Modules.Media;
using Island.Settings;

namespace Island.Modules;

public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleDescriptor> All { get; } =
    [
        new("claude", "Claude", "", () => new ClaudeModule()),
        new("media", "Media", "", () => new MediaModule()),
        new("clock", "Clock & timer", "", () => new ClockModule()),
        new("system", "System", "", () => new IndicatorsModule()),
    ];
}

public sealed record ModuleTab(ModuleDescriptor Descriptor, object Panel);

/// <summary>Starts/stops modules to match settings and exposes the expanded-panel tabs. UI thread only.</summary>
public sealed class ModuleManager(
    IReadOnlyList<ModuleDescriptor> catalog,
    ActivityHost activities,
    Dispatcher dispatcher,
    SettingsService settings) : IDisposable
{
    private readonly Dictionary<string, IIslandModule> _running = [];

    public IReadOnlyList<ModuleDescriptor> Catalog => catalog;

    public ObservableCollection<ModuleTab> Tabs { get; } = [];

    public void Apply()
    {
        var entries = settings.Current.Modules;
        foreach (var d in catalog.Where(d => entries.All(e => e.Id != d.Id)))
            entries.Add(new ModuleEntry { Id = d.Id, Enabled = true });

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
        if (!tabs.SequenceEqual(Tabs))
        {
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
