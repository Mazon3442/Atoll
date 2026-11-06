using System.Diagnostics;
using Atoll.Activities;
using Atoll.Settings;
using Atoll.Updates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Atoll.Desktop;

public sealed class App : Application
{
    private Mutex? _instance;
    private FileStream? _unixLock;
    private SettingsService? _settings;
    private DesktopModules? _modules;
    private ActivityHost? _activities;
    private readonly List<IslandWindow> _islands = [];
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private bool _paused;
    private readonly CancellationTokenSource _shutdown = new();

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _lifetime = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!AcquireInstance()) { desktop.Shutdown(); return; }
        _settings = new SettingsService(new SettingsStore(SettingsStore.DefaultPath));
        _activities = new ActivityHost();
        Navigation.OpenSettings = OpenSettings;
#if WINDOWS
        Atoll.Modules.Claude.ClaudeSessionViewModel.FocusWindow = h => Atoll.Shell.WindowFocus.Bring(h);
#endif
        _modules = new DesktopModules(_settings, _activities);
        var first = new IslandWindow(_settings, _activities, _modules, OpenSettings);
        _islands.Add(first);
        desktop.MainWindow = first;
        first.Show();
        first.Screens.Changed += (_, _) => RebuildScreens();
        _settings.Changed += RebuildScreens;
        RebuildScreens();
        CreateTray();
        desktop.Exit += (_, _) => Dispose();
        if (_settings.Current.StartWithWindows)
        {
            try { Startup.SetEnabled(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Could not refresh login startup", ex); }
        }
        if (Atoll.Modules.Claude.ClaudeHookInstaller.GetStatus() == Atoll.Modules.Claude.ClaudeHookInstaller.Status.InstalledElsewhere)
            Atoll.Modules.Claude.ClaudeHookInstaller.Install();
        if (desktop.Args?.Any(a => a.StartsWith("--settings", StringComparison.Ordinal)) == true) OpenSettings(0);
        var open = desktop.Args?.FirstOrDefault(a => a.StartsWith("--open=", StringComparison.Ordinal));
        if (open is not null) first.OpenModule(open[7..]);
        Dispatcher.UIThread.UnhandledException += (_, e) => { Log.Error("Unhandled desktop error", e.Exception); e.Handled = true; };
        _ = CheckUpdatesAsync(_shutdown.Token);
        base.OnFrameworkInitializationCompleted();
    }

    private bool AcquireInstance()
    {
        if (OperatingSystem.IsWindows())
        {
            _instance = new Mutex(true, @"Local\Atoll.SingleInstance", out var first);
            return first;
        }
        var directory = Path.GetDirectoryName(SettingsStore.DefaultPath)!;
        Directory.CreateDirectory(directory);
        try { _unixLock = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    private void RebuildScreens()
    {
        if (_settings is null || _modules is null || _activities is null || _islands.Count == 0) return;
        var screens = _islands[0].Screens.All;
        var chosen = _settings.Current.MonitorMode switch
        {
            MonitorMode.All => screens.ToList(),
            MonitorMode.Selected => screens.Where(s => _settings.Current.SelectedMonitors.Contains(s.DisplayName ?? s.Bounds.ToString())).ToList(),
            _ => screens.Where(s => s.IsPrimary).Take(1).ToList(),
        };
        if (chosen.Count == 0 && screens.Count > 0) chosen.Add(screens[0]);
        while (_islands.Count > Math.Max(1, chosen.Count)) { _islands[^1].Close(); _islands.RemoveAt(_islands.Count - 1); }
        while (_islands.Count < chosen.Count)
        {
            var island = new IslandWindow(_settings, _activities, _modules, OpenSettings);
            _islands.Add(island); island.Show();
        }
        for (var i = 0; i < chosen.Count; i++) { _islands[i].Monitor = chosen[i]; _islands[i].ApplySettings(); }
    }

    private void CreateTray()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Atoll/Assets/Atoll.png"));
        _tray = new TrayIcon { Icon = new WindowIcon(new Bitmap(stream)), ToolTipText = "ATOLL" };
        var menu = new NativeMenu();
        var settings = new NativeMenuItem("Settings"); settings.Click += (_, _) => OpenSettings(0);
        var pause = new NativeMenuItem("Pause island") { ToggleType = NativeMenuItemToggleType.CheckBox };
        pause.Click += (_, _) => { _paused = !_paused; foreach (var island in _islands) { island.Paused = _paused; island.IsVisible = !_paused; } };
        var quit = new NativeMenuItem("Quit ATOLL"); quit.Click += (_, _) => _lifetime?.Shutdown();
        menu.Add(settings); menu.Add(pause); menu.Add(new NativeMenuItemSeparator()); menu.Add(quit);
        _tray.Menu = menu; _tray.Clicked += (_, _) => OpenSettings(0);
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    internal void OpenSettings(int page)
    {
        if (_settings is null || _modules is null) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, _modules, () => _lifetime?.Shutdown());
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.SelectPage(page);
        _settingsWindow.Show(); _settingsWindow.Activate();
    }

    private static async Task CheckUpdatesAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), ct);
            while (!ct.IsCancellationRequested)
            {
                await UpdateService.CheckAndDownloadAsync();
                await Task.Delay(TimeSpan.FromHours(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error("Update check stopped", ex); }
    }

    private void Dispose()
    {
        _shutdown.Cancel(); _tray?.Dispose();
        foreach (var island in _islands) island.Dispose();
        _modules?.Dispose(); _activities?.Dispose(); _settings?.Dispose();
        _instance?.Dispose(); _unixLock?.Dispose(); _shutdown.Dispose();
    }
}
