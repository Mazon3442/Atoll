using System.Windows.Threading;
using System.Windows;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Settings;
using Microsoft.Win32;

namespace Atoll.Shell;

/// <summary>Owns one <see cref="IslandWindow"/> per chosen monitor and keeps them in sync. UI thread only.</summary>
public sealed class IslandManager : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ActivityHost _activities;
    private readonly ModuleManager _modules;
    private readonly Action<int> _openSettings;
    private readonly Dispatcher _dispatcher;
    private readonly FullscreenWatcher _fullscreen = new();
    private readonly List<IslandWindow> _windows = [];
    private string _monitorSignature = "";
    private bool _paused;

    public IslandManager(SettingsService settings, ActivityHost activities, ModuleManager modules, Action<int> openSettings, Dispatcher dispatcher)
    {
        _settings = settings;
        _activities = activities;
        _modules = modules;
        _openSettings = openSettings;
        _dispatcher = dispatcher;

        _activities.Changed += OnActivitiesChanged;
        _settings.Changed += OnSettingsChanged;
        _fullscreen.Check += UpdateVisibility;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Rebuild();
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            UpdateVisibility();
        }
    }

    private void Rebuild()
    {
        foreach (var w in _windows) w.Close();
        _windows.Clear();

        var s = _settings.Current;
        _monitorSignature = MonitorSignature(s);
        var all = Monitors.GetAll();
        var chosen = s.MonitorMode switch
        {
            MonitorMode.All => all.ToList(),
            MonitorMode.Selected => all.Where(m => s.SelectedMonitors.Contains(m.DeviceName)).ToList(),
            _ => all.Where(m => m.IsPrimary).ToList(),
        };
        if (chosen.Count == 0) chosen = all.Where(m => m.IsPrimary).Take(1).ToList();
        if (chosen.Count == 0) chosen = all.Take(1).ToList();

        var snapshot = _activities.Snapshot(4);
        foreach (var monitor in chosen)
        {
            var window = new IslandWindow(monitor, _settings, _modules, _openSettings);
            window.SetActivitySnapshot(snapshot.Primary, snapshot.Sides);
            _windows.Add(window);
        }
        UpdateVisibility();
    }

    /// <summary>Expands the island under the pointer, or the primary display, to a module's tab.</summary>
    public void OpenModule(string moduleId)
    {
        var target = Native.GetCursorPos(out var p)
            ? _windows.FirstOrDefault(w => p.X >= w.Monitor.Bounds.X && p.X < w.Monitor.Bounds.X + w.Monitor.Bounds.Width
                && p.Y >= w.Monitor.Bounds.Y && p.Y < w.Monitor.Bounds.Y + w.Monitor.Bounds.Height) : null;
        (target ?? _windows.FirstOrDefault(w => w.Monitor.IsPrimary) ?? _windows.FirstOrDefault())?.OpenModule(moduleId);
    }

    /// <summary>The open shortcut acts on the island of the display the pointer is on, else the main one.</summary>
    public void ToggleFromKeyboard()
    {
        var target = Native.GetCursorPos(out var p)
            ? _windows.FirstOrDefault(w => p.X >= w.Monitor.Bounds.X && p.X < w.Monitor.Bounds.X + w.Monitor.Bounds.Width && p.Y >= w.Monitor.Bounds.Y && p.Y < w.Monitor.Bounds.Y + w.Monitor.Bounds.Height) : null;
        // Close whichever island is open first, so the shortcut always undoes itself.
        (_windows.FirstOrDefault(w => w.IsExpanded) ?? target ?? _windows.FirstOrDefault(w => w.Monitor.IsPrimary) ?? _windows.FirstOrDefault())
            ?.ToggleFromKeyboard();
    }

    private void UpdateVisibility()
    {
        foreach (var w in _windows)
        {
            w.SetEnvironment(_paused, FullscreenWatcher.Classify(w.Monitor));
            if (w.IsVisible) w.EnsureTopmost(); // other topmost apps can push us down
        }
    }

    private void OnActivitiesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        var snapshot = _activities.Snapshot(4);
        foreach (var w in _windows) w.SetActivitySnapshot(snapshot.Primary, snapshot.Sides);
    });

    private void OnSettingsChanged()
    {
        if (MonitorSignature(_settings.Current) != _monitorSignature)
        {
            Rebuild();
            return;
        }
        foreach (var w in _windows) w.ApplySettings();
        UpdateVisibility();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Rebuild);

    private static string MonitorSignature(AppSettings s) => $"{s.MonitorMode}|{string.Join(",", s.SelectedMonitors)}";

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _activities.Changed -= OnActivitiesChanged;
        _settings.Changed -= OnSettingsChanged;
        _fullscreen.Dispose();
        foreach (var w in _windows) w.Close();
        _windows.Clear();
    }
}
