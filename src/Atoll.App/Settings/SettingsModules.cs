using System.Collections.ObjectModel;
using Atoll.Modules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Settings;

/// <summary>
/// Settings › Modules, organised the way the island is: one card per tab, in tab order, with the
/// tab's features as switches inside it. Moving a card moves the tab.
/// </summary>
public sealed partial class SettingsViewModel
{
    // Tab id → (name, description). Order is the default tab order.
    private static readonly (string Id, string Name, string Description)[] TabInfo =
    [
        ("claude", "Agents", "Claude Code, Codex, OpenCode and Hermes sessions, with alerts when they finish or need you."),
        ("providers", "API keys", "Live requests and token totals for your own OpenAI, Anthropic, DeepSeek or compatible API keys."),
        ("media", "Media", "What's playing, with controls, seeking and the volume slider."),
        ("notifications", "Notifications", "Windows notifications, mirrored into the island."),
        ("clock", "Timer", "The clock, countdowns, stopwatch and Pomodoro."),
        ("calendar", "Calendar", "A month view and the selected day's events."),
        ("notes", "Notes", "Notes, quick reminders and a to-do list."),
        ("system", "System", "Device status, live graphs and quick actions."),
        ("shelf", "Files", "Files you drop on the island to keep their paths handy."),
        ("clipboard", "Clipboard", "Your recent copies, kept in memory only."),
        ("weather", "Weather", "Current conditions and a 7-day forecast."),
        ("launcher", "Launcher", "Start Menu apps and your games, one tab away."),
    ];

    // Feature id → (name, description) where the catalog name alone doesn't explain it.
    private static readonly Dictionary<string, (string Name, string Description)> FeatureInfo = new()
    {
        ["clock"] = ("Clock & timers", "Countdowns, stopwatch and Pomodoro."),
        ["calendar"] = ("Calendar", "Events from the calendars you add."),
        ["notes"] = ("Notes", "Notes, quick reminders and a to-do list."),
        ["launcher"] = ("App launcher", "Search and open Start Menu apps."),
        ["games"] = ("Games", "Game mode, play time, your game library and a Minecraft server."),
        ["system"] = ("Status popups", "Volume, battery, network, Bluetooth, brightness and lock keys."),
        ["stats"] = ("Live graphs", "CPU, GPU, memory and network."),
        ["actions"] = ("Quick actions", "Screenshot, mic mute, lock, screen off and Downloads."),
        ["privacy"] = ("Camera and mic in use", "A green dot while an app uses your camera, orange for the microphone."),
        ["shelf"] = ("File shelf", "Drop files on the island to keep them at hand."),
        ["clipboard"] = ("Clipboard history", "Your last 25 copies, kept in memory only."),
    };

    public ObservableCollection<ModuleTabRow> ModuleTabs { get; } = [];

    private void BuildModuleTabs(IReadOnlyList<ModuleDescriptor> catalog)
    {
        // Every catalog feature gets an entry; ModuleManager adds missing ones the same way at startup.
        foreach (var d in catalog.Where(d => S.Modules.All(e => e.Id != d.Id)))
            S.Modules.Add(new ModuleEntry { Id = d.Id, Enabled = d.EnabledByDefault });

        // A tab sits where its first switched-on feature sits; the island orders tabs the same way.
        int Rank(string tab)
        {
            var members = S.Modules.Select((e, i) => (e, i)).Where(x => PanelGroups.TabId(x.e.Id) == tab).ToList();
            var on = members.Where(x => x.e.Enabled).Select(x => x.i).DefaultIfEmpty(-1).First();
            return on >= 0 ? on : S.Modules.Count + members.Select(x => x.i).DefaultIfEmpty(0).First();
        }

        foreach (var (id, name, description) in TabInfo.OrderBy(t => Rank(t.Id)))
        {
            var features = catalog.Where(d => PanelGroups.TabId(d.Id) == id)
                .Select(d =>
                {
                    var info = FeatureInfo.GetValueOrDefault(d.Id, (d.Name, description));
                    return new ModuleRow(S.Modules.First(e => e.Id == d.Id), info.Item1, info.Item2, d.Glyph, OnFeatureToggled);
                })
                .ToList();
            if (features.Count > 0)
                ModuleTabs.Add(new ModuleTabRow(id, name, description, features[0].Glyph, features));
        }
        RefreshTabPositions();
        // Keep each tab's features together in settings so switching one on can't move a tab.
        WriteModuleOrder(save: false);
    }

    [RelayCommand]
    private void MoveTabUp(ModuleTabRow tab) => MoveTab(tab, ModuleTabs.IndexOf(tab) - 1);

    [RelayCommand]
    private void MoveTabDown(ModuleTabRow tab) => MoveTab(tab, ModuleTabs.IndexOf(tab) + 1);

    public void MoveTab(ModuleTabRow tab, int to)
    {
        var from = ModuleTabs.IndexOf(tab);
        if (from < 0 || to < 0 || to >= ModuleTabs.Count || from == to) return;
        ModuleTabs.Move(from, to);
        RefreshTabPositions();
        WriteModuleOrder(save: true);
    }

    private void OnFeatureToggled()
    {
        foreach (var tab in ModuleTabs) tab.Refresh();
        Save();
    }

    private void RefreshTabPositions()
    {
        for (var i = 0; i < ModuleTabs.Count; i++) ModuleTabs[i].SetPosition(i, ModuleTabs.Count);
    }

    /// <summary>Writes the settings list tab by tab, keeping entries for modules this build doesn't know at the end.</summary>
    private void WriteModuleOrder(bool save)
    {
        var known = ModuleTabs.SelectMany(t => t.Features).Select(f => f.Entry).ToList();
        var unknown = S.Modules.Where(e => !known.Contains(e)).ToList();
        if (S.Modules.SequenceEqual(known.Concat(unknown))) return;
        S.Modules.Clear();
        S.Modules.AddRange(known);
        S.Modules.AddRange(unknown);
        if (save) Save();
    }
}

/// <summary>One tab of the expanded island and the features that make it up.</summary>
public sealed partial class ModuleTabRow(string id, string name, string description, string glyph, IReadOnlyList<ModuleRow> features) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string Glyph { get; } = glyph;
    public IReadOnlyList<ModuleRow> Features { get; } = features;
    public bool HasSeveralFeatures => Features.Count > 1;
    /// <summary>The switch shown on the card itself when the tab has a single feature.</summary>
    public ModuleRow OnlyFeature => Features[0];

    [ObservableProperty] private int _number;
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isLast;

    public bool IsShown => Features.Any(f => f.Enabled);

    /// <summary>"2 of 3 on", or why the tab isn't in the island.</summary>
    public string Status => !IsShown ? (HasSeveralFeatures ? "Not in the island: every feature is off" : "Not in the island")
        : Features.All(f => f.Enabled) ? "" : $"{Features.Count(f => f.Enabled)} of {Features.Count} on";

    internal void SetPosition(int index, int count)
    {
        Number = index + 1;
        IsFirst = index == 0;
        IsLast = index == count - 1;
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(Status));
    }
}

/// <summary>One tab in the Modules page's preview of the island's tab strip.</summary>
