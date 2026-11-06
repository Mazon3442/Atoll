using System.Windows.Threading;
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
    private readonly Action _openSettings;
    private readonly Dispatcher _dispatcher;
    private readonly FullscreenWatcher _fullscreen = new();
    private readonly List<IslandWindow> _windows = [];
    private string _monitorSignature = "";
    private bool _paused;

    public IslandManager(SettingsService settings, ActivityHost activities, ModuleManager modules, Action openSettings, Dispatcher dispatcher)
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

        var (primary, secondary) = _activities.Current;
        foreach (var monitor in chosen)
        {
            var window = new IslandWindow(monitor, _settings, _modules, _openSettings);
            window.SetActivities(primary, secondary);
            _windows.Add(window);
        }
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var hideFullscreen = _settings.Current.HideInFullscreen;
        foreach (var w in _windows)
        {
            var shown = !_paused && !(hideFullscreen && FullscreenWatcher.IsFullscreenOn(w.Monitor));
            w.SetShown(shown);
            if (shown) w.EnsureTopmost(); // other topmost apps can push us down
        }
    }

    private void OnActivitiesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        var (primary, secondary) = _activities.Current;
        foreach (var w in _windows) w.SetActivities(primary, secondary);
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
