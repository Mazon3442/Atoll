using System.Windows;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Modules.Claude;
using Atoll.Settings;
using Atoll.Shell;
using Atoll.Theme;
using Atoll.Updates;

namespace Atoll;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private SettingsService? _settings;
    private ActivityHost? _activities;
    private ModuleManager? _modules;
    private IslandManager? _islands;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private int? _restartSettingsPage;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Atoll.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ClaudeSessionViewModel.FocusWindow = handle => WindowFocus.Bring(handle);
        Navigation.OpenSettings = OpenSettings;
        DispatcherUnhandledException += OnUnhandledException;

        _settings = new SettingsService(new SettingsStore(SettingsStore.DefaultPath));
        ThemeService.Apply(_settings.Current.Theme);
        if (_settings.Current.StartWithWindows) StartupRegistration.Apply(true); // refresh exe path if the app moved

        // If ATOLL moved (a Velopack update installs to a new folder), point the hooks back at it so
        // Claude Code keeps reporting to this copy without the user reinstalling them.
        if (ClaudeHookInstaller.GetStatus() == ClaudeHookInstaller.Status.InstalledElsewhere)
            ClaudeHookInstaller.Install();

        _activities = new ActivityHost();
        _modules = new ModuleManager(ModuleCatalog.All, _activities, Dispatcher, _settings);
        _modules.Apply();

        _settings.Changed += () =>
        {
            ThemeService.Apply(_settings.Current.Theme);
            _modules.Apply();
        };

        _islands = new IslandManager(_settings, _activities, _modules, OpenSettings, Dispatcher);
        _tray = new TrayIcon(OpenSettings, paused => _islands.Paused = paused, Shutdown);

        _ = CheckUpdatesLoopAsync();

        // "--open=<module id>" (e.g. shelf, weather) opens ATOLL expanded on that tab once it is on screen.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--open=", StringComparison.OrdinalIgnoreCase)) is { } open)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _islands.OpenModule(open["--open=".Length..]));

        // "--settings" or "--settings=<page number>" opens settings on launch.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--settings", StringComparison.OrdinalIgnoreCase)) is { } arg)
            OpenSettings(int.TryParse(arg.Split('=', 2).ElementAtOrDefault(1), out var page) ? page : 0);
    }

    private void OpenSettings() => OpenSettings(0);

    internal void RestartToSettings(int page)
    {
        if (!File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "Atoll.exe")))
            throw new FileNotFoundException("ATOLL executable was not found.");
        _restartSettingsPage = page;
        Shutdown();
    }

    internal void OpenSettings(int page)
    {
        if (_settings is null || _modules is null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.DataContext is SettingsViewModel existing)
            {
                existing.PageIndex = Math.Clamp(page, 0, existing.Pages.Count - 1);
                if (page == 6) existing.WeatherLocationExpanded = true;
            }
            _settingsWindow.Activate();
            return;
        }
        var weather = _modules.Tabs.Select(t => t.Panel).OfType<Atoll.Modules.Weather.WeatherViewModel>().FirstOrDefault();
        var vm = new SettingsViewModel(_settings, _modules.Catalog, weather) { PageIndex = Math.Clamp(page, 0, 7) };
        _settingsWindow = new SettingsWindow(vm);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private static async Task CheckUpdatesLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45)); // let startup settle before any network call
            while (true)
            {
                await UpdateService.CheckAndDownloadAsync();
                await Task.Delay(TimeSpan.FromHours(1));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Background update loop stopped", ex);
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // An overlay that vanishes is worse than one with a broken widget: log and keep running.
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _islands?.Dispose();
        _modules?.Dispose();
        _activities?.Dispose();
        _settings?.Flush();
        _singleInstance?.Dispose();
        base.OnExit(e);
        // Start only after settings, module handles, and the single-instance mutex are released.
        if (_restartSettingsPage is { } page)
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(AppContext.BaseDirectory, "Atoll.exe")) { UseShellExecute = true };
                start.ArgumentList.Add($"--settings={page}");
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { Log.Error("Could not restart ATOLL; reopen it manually", ex); }
        }
    }
}
