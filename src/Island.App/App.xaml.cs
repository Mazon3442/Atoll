using System.Windows;
using System.Windows.Threading;
using Island.Activities;
using Island.Modules;
using Island.Settings;
using Island.Shell;
using Island.Theme;

namespace Island;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private SettingsService? _settings;
    private ActivityHost? _activities;
    private ModuleManager? _modules;
    private IslandManager? _islands;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Island.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _settings = new SettingsService(new SettingsStore(SettingsStore.DefaultPath));
        ThemeService.Apply(_settings.Current.Theme);
        if (_settings.Current.StartWithWindows) StartupRegistration.Apply(true); // refresh exe path if the app moved

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
    }

    private void OpenSettings()
    {
        if (_settings is null || _modules is null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(new SettingsViewModel(_settings, _modules.Catalog));
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
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
    }
}
