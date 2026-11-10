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
    private HotkeyService? _hotkeys;
    private FocusService? _focus;
    private DispatcherTimer? _focusTimer;
    private SettingsWindow? _settingsWindow;
    private Onboarding.OnboardingWindow? _onboardingWindow;
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

        // "--onboarding" opens only the welcome window, without islands, tray, hooks or update checks.
        if (e.Args.Any(arg => arg.Equals("--onboarding", StringComparison.OrdinalIgnoreCase)))
        {
            var setup = new Onboarding.OnboardingViewModel(_settings.Current, _settings.NotifyChanged, StartupRegistration.Apply);
            var window = new Onboarding.OnboardingWindow(setup);
            window.Closed += (_, _) => Shutdown();
            window.Show();
            return;
        }

        // If ATOLL moved (a Velopack update installs to a new folder), point the hooks back at it so
        // Claude Code keeps reporting to this copy without the user reinstalling them.
        if (ClaudeHookInstaller.GetStatus() == ClaudeHookInstaller.Status.InstalledElsewhere)
            ClaudeHookInstaller.Install();

        _activities = new ActivityHost();
        _focus = new FocusService(TimeProvider.System);
        FocusService.Current = _focus;
        _focus.Configure(_settings.Current.Focus.Enabled, _settings.Current.Focus.FollowWindowsFocus);
        _focus.SetWindowsFocus(WindowsFocus());
        _modules = new ModuleManager(ModuleCatalog.All, _activities, Dispatcher, _settings);
        _modules.Apply();

        _settings.Changed += () =>
        {
            ThemeService.Apply(_settings.Current.Theme);
            _focus?.Configure(_settings.Current.Focus.Enabled, _settings.Current.Focus.FollowWindowsFocus);
            _modules.Apply();
        };

        _islands = new IslandManager(_settings, _activities, _modules, OpenSettings, Dispatcher);
        Navigation.OpenModule = _islands.OpenModule;
        Navigation.OpenOnboarding = ShowOnboarding;
        _tray = new TrayIcon(OpenSettings, SetPaused, SetFocus, Shutdown) { Focus = _focus.IsActive };
        _focus.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FocusService.IsActive) && _tray is not null) _tray.Focus = _focus.IsActive;
        };
        _focusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _focusTimer.Tick += (_, _) =>
        {
            _focus.Tick();
            _focus.SetWindowsFocus(WindowsFocus());
        };
        _focusTimer.Start();
        _hotkeys = new HotkeyService(_settings, _islands.ToggleFromKeyboard, () => SetPaused(!_islands.Paused), _islands.OpenPalette);

        // First run: explain the island and let the user choose the top row. Skipping still marks it done.
        if (!_settings.Current.OnboardingComplete)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowOnboarding));

        _ = CheckUpdatesLoopAsync();

        // "--open=<module id>" (e.g. shelf, weather) opens ATOLL expanded on that tab once it is on screen.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--open=", StringComparison.OrdinalIgnoreCase)) is { } open)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _islands.OpenModule(open["--open=".Length..]));

        // "--settings" or "--settings=<page number>" opens settings on launch.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--settings", StringComparison.OrdinalIgnoreCase)) is { } arg)
            OpenSettings(int.TryParse(arg.Split('=', 2).ElementAtOrDefault(1), out var page) ? page : 0);
    }

    private void OpenSettings() => OpenSettings(0);

    /// <summary>Hides or shows every island; the tray item and the hide shortcut stay in step.</summary>
    private void SetPaused(bool paused)
    {
        if (_islands is null || _tray is null) return;
        _islands.Paused = paused;
        _tray.Paused = paused;
    }

    /// <summary>Tray toggle: indefinite focus, or back to normal.</summary>
    private void SetFocus(bool on)
    {
        if (_focus is null) return;
        if (on) _focus.FocusIndefinitely();
        else _focus.Clear();
    }

    private static bool WindowsFocus() =>
        Native.SHQueryUserNotificationState(out var state) == 0 && state == Native.QUNS_QUIET_TIME;

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
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        var weather = _modules.Tabs.Select(t => t.Panel).OfType<Atoll.Modules.Weather.WeatherViewModel>().FirstOrDefault();
        var vm = new SettingsViewModel(_settings, _modules.Catalog, weather) { PageIndex = Math.Clamp(page, 0, 8) };
        _settingsWindow = new SettingsWindow(vm);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Shows the welcome picker; safe to call again from Settings › About.</summary>
    internal void ShowOnboarding()
    {
        if (_settings is null) return;
        if (_onboardingWindow is { IsLoaded: true })
        {
            if (_onboardingWindow.WindowState == WindowState.Minimized) _onboardingWindow.WindowState = WindowState.Normal;
            _onboardingWindow.Activate();
            return;
        }
        var vm = new Onboarding.OnboardingViewModel(_settings.Current, _settings.NotifyChanged, StartupRegistration.Apply);
        _onboardingWindow = new Onboarding.OnboardingWindow(vm);
        _onboardingWindow.Closed += (_, _) => _onboardingWindow = null;
        _onboardingWindow.Show();
        _onboardingWindow.Activate();
    }

    private string? _askedAboutVersion;

    private async Task CheckUpdatesLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10)); // let the island appear before any network call
            while (true)
            {
                var result = await UpdateService.CheckAndDownloadAsync();
                if (result is { Downloaded: true, Version: { } version }) await Dispatcher.InvokeAsync(() => AskToRestart(version));
                await Task.Delay(TimeSpan.FromHours(1));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Background update loop stopped", ex);
        }
    }

    /// <summary>Posts the restart question once per downloaded version; Later keeps the quiet apply-on-next-start.</summary>
    private void AskToRestart(string version)
    {
        if (_activities is null || _askedAboutVersion == version) return;
        _askedAboutVersion = version;
        const string id = "update.ready";
        var prompt = new UpdatePromptViewModel(version,
            restart: () =>
            {
                if (UpdateService.ApplyOnExitAndRestart()) Shutdown();
            },
            dismiss: () => _activities.Remove(id));
        _activities.Post(new Activity(id, "update", prompt, ActivityPriority.High, TimeSpan.FromMinutes(5), alert: true));
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // An overlay that vanishes is worse than one with a broken widget: log and keep running.
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _focusTimer?.Stop();
        _focusTimer = null;
        FocusService.Current = null;
        _hotkeys?.Dispose();
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
