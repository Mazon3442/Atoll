using System.Reflection;
using Atoll.Modules.Claude;
using Atoll.Modules.Weather;
using Atoll.Settings;
using Atoll.Theme;
using Atoll.Updates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Atoll.Desktop;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly DesktopModules _modules;
    private readonly ListBox _navigation;
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Action _quit;
    private static readonly string[] Pages = ["General", "Appearance", "Modules", "Notifications", "Agents", "Calendar", "Weather", "Updates & about"];
    private CancellationTokenSource? _search;

    public SettingsWindow(SettingsService settings, DesktopModules modules, Action quit)
    {
        _settings = settings; _modules = modules; _quit = quit;
        Title = "ATOLL Settings"; Width = 940; Height = 740; MinWidth = 660; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _navigation = new ListBox { ItemsSource = Pages, Margin = new Thickness(12), MinWidth = 166 };
        _navigation.SelectionChanged += (_, _) => ShowPage();
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*") };
        layout.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#808080"), .06), Child = Views.Stack(Views.Label("ATOLL", 25), _navigation), Padding = new Thickness(12, 22) });
        var scroll = new ScrollViewer { Content = _page, Margin = new Thickness(28), HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); layout.Children.Add(scroll); Content = layout;
        Closed += (_, _) => { _settings.Flush(); _search?.Cancel(); _search?.Dispose(); };
        SelectPage(0);
    }
    public void SelectPage(int page) => _navigation.SelectedIndex = Math.Clamp(page, 0, Pages.Length - 1);
    private void ShowPage()
    {
        _page.Content = _navigation.SelectedIndex switch
        {
            0 => General(), 1 => Appearance(), 2 => Modules(), 3 => Notifications(),
            4 => Agents(), 5 => Calendar(), 6 => Weather(), _ => Updates(),
        };
    }
    private CheckBox Check(string title, bool value, Action<bool> change)
    {
        var box = new CheckBox { Content = title, IsChecked = value };
        box.IsCheckedChanged += (_, _) => { change(box.IsChecked == true); _settings.NotifyChanged(); }; return box;
    }
    private Control Field(string title, string value, Action<string> save, string? hint = null)
    {
        var text = new TextBox { Text = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(text, title);
        text.LostFocus += (_, _) => { save(text.Text ?? ""); _settings.NotifyChanged(); };
        var panel = Views.Stack(Views.Label(title), text); if (hint is not null) panel.Children.Add(Views.Label(hint, 12)); return panel;
    }
    private Control Number(string title, double value, double min, double max, Action<double> save)
    {
        var input = new NumericUpDown { Value = (decimal)Math.Clamp(value, min, max), Minimum = (decimal)min, Maximum = (decimal)max, Increment = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(input, title);
        input.ValueChanged += (_, _) => { if (input.Value is { } v) { save((double)v); _settings.NotifyChanged(); } };
        return Views.Stack(Views.Label(title), input);
    }
    private Control General()
    {
        var startupStatus = Views.Label("", 12);
        var startup = Check("Start ATOLL when I log in", _settings.Current.StartWithWindows, enabled =>
        {
            try { Startup.SetEnabled(enabled); _settings.Current.StartWithWindows = enabled; startupStatus.Text = ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { startupStatus.Text = "Could not change login startup. Check permissions and try again."; }
        });
        var mode = new ComboBox { ItemsSource = Enum.GetValues<MonitorMode>(), SelectedItem = _settings.Current.MonitorMode, HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.SelectionChanged += (_, _) => { if (mode.SelectedItem is MonitorMode m) { _settings.Current.MonitorMode = m; _settings.NotifyChanged(); } };
        var monitors = Views.Stack(Views.Label("Displays"), mode);
        foreach (var screen in Screens.All)
        {
            var id = screen.DisplayName ?? screen.Bounds.ToString();
            monitors.Children.Add(Check(id, _settings.Current.SelectedMonitors.Contains(id), enabled => { _settings.Current.SelectedMonitors.Remove(id); if (enabled) _settings.Current.SelectedMonitors.Add(id); }));
        }
        var panel = Views.Stack(Views.Label("General", 28), startup, startupStatus, monitors,
            Number("Distance from the top edge", _settings.Current.TopOffset, 0, 200, v => _settings.Current.TopOffset = v),
            Number("Hover delay (milliseconds)", _settings.Current.HoverDelayMs, 100, 2000, v => _settings.Current.HoverDelayMs = (int)v));
#if WINDOWS
        panel.Children.Add(Check("Let clicks pass through the compact island", _settings.Current.ClickThrough, v => _settings.Current.ClickThrough = v));
        var fullscreen = new ComboBox { ItemsSource = Enum.GetValues<FullscreenMode>(), SelectedItem = _settings.Current.FullscreenMode, HorizontalAlignment = HorizontalAlignment.Stretch };
        fullscreen.SelectionChanged += (_, _) => { if (fullscreen.SelectedItem is FullscreenMode value) { _settings.Current.FullscreenMode = value; _settings.NotifyChanged(); } };
        panel.Children.Add(Views.Stack(Views.Label("Over fullscreen apps"), fullscreen, Views.Label("Games stay hidden. Hold at the top center of the display for three seconds to reveal the island.", 12)));
#else
        panel.Children.Add(Views.Label("Window positioning and always-on-top behavior depend on your desktop. Linux currently uses X11 or XWayland; a Wayland compositor may restrict overlays.", 12));
#endif
        panel.Children.Add(Views.Button("Quit ATOLL", _quit)); return panel;
    }
    private Control Appearance()
    {
        var theme = _settings.Current.Theme;
        var presets = ThemePresets.All.ToList();
        presets.AddRange(_settings.Current.CustomThemes);
        var choices = new ComboBox { ItemsSource = presets, ItemTemplate = new FuncDataTemplate<ThemePreset>((p, _) => Views.Label(p?.Name ?? "")), HorizontalAlignment = HorizontalAlignment.Stretch };
        choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is ThemePreset p) { theme.Background = p.Background; theme.Foreground = p.Foreground; theme.Accent = p.Accent; theme.Glass = p.Glass; _settings.NotifyChanged(); ShowPage(); } };
        var message = Views.Label("", 12);
        Control ColorField(string title, string value, Action<string> save) => Field(title, value, input => { if (Color.TryParse(input, out _)) { save(input); message.Text = ""; } else message.Text = "Use a color such as #0A84FF."; });
        var name = new TextBox { Watermark = "Theme name" };
        return Views.Stack(Views.Label("Appearance", 28), choices,
            ColorField("Background", theme.Background, v => theme.Background = v),
            ColorField("Text", theme.Foreground, v => theme.Foreground = v),
            ColorField("Accent", theme.Accent, v => theme.Accent = v), message,
            Check("Frosted backdrop (when the desktop supports it)", theme.Glass, v => theme.Glass = v),
            Number("Expanded width", theme.ExpandedWidth, 420, 900, v => theme.ExpandedWidth = v),
            Number("Compact height", theme.CompactHeight, 28, 80, v => theme.CompactHeight = v),
            Number("Expanded panel height", theme.ExpandedHeight, 200, 720, v => theme.ExpandedHeight = v),
            Number("Opacity (percent)", theme.Opacity * 100, 30, 100, v => theme.Opacity = v / 100),
            Number("Roundness (percent)", theme.Roundness * 100, 0, 100, v => theme.Roundness = v / 100),
            name, Views.Button("Save current theme", () => { if (!string.IsNullOrWhiteSpace(name.Text)) { _settings.Current.CustomThemes.Add(new ThemePreset(name.Text.Trim(), theme.Background, theme.Foreground, theme.Accent, theme.Glass)); _settings.NotifyChanged(); ShowPage(); } }));
    }
    private Control Modules()
    {
        var panel = Views.Stack(Views.Label("Modules", 28), Views.Label("Choose which features appear in the island."));
        foreach (var (id, name, _) in DesktopModules.Catalog)
        {
            var entry = _settings.Current.Modules.First(e => e.Id == id);
            panel.Children.Add(Check(name, entry.Enabled, enabled => entry.Enabled = enabled));
        }
        return panel;
    }
    private Control Agents()
    {
        var installer = new AgentIntegrationInstaller();
        var panel = Views.Stack(Views.Label("Agents", 28), Views.Label("Adapters report session activity to ATOLL on this computer. Existing hooks are backed up and preserved."),
            Check("Play a sound when work finishes", _settings.Current.Claude.SoundOnDone, v => _settings.Current.Claude.SoundOnDone = v),
            Check("Play a sound when an agent needs attention", _settings.Current.Claude.SoundOnAttention, v => _settings.Current.Claude.SoundOnAttention = v));
        foreach (var agent in Atoll.Claude.HookProtocol.Agents)
        {
            var model = new AgentIntegrationRow(agent, installer);
            var row = Views.Stack(Views.Text("Name", 18), Views.Text("Status", 12), Views.Row(Views.Command("Install / update", "InstallCommand"), Views.Command("Remove adapter", "RemoveCommand")), Views.Text("Message", 12));
            row.DataContext = model; panel.Children.Add(row);
        }
        panel.Children.Add(ProviderSettingsPanel());
        return panel;
    }
    private Control Calendar()
    {
        var panel = Views.Stack(Views.Label("Calendar", 28), Views.Label("Add a private ICS calendar link. Links are encrypted before they are saved."));
        foreach (var source in _settings.Current.Calendar.Sources.ToList())
        {
            panel.Children.Add(Views.Row(Views.Label(source.Name, 18), Views.Button("Remove calendar", () => { _settings.Current.Calendar.Sources.Remove(source); _settings.NotifyChanged(); ShowPage(); })));
        }
        var name = new TextBox { Watermark = "Calendar name" };
        var url = new TextBox { Watermark = "https://… private ICS link", PasswordChar = '●' };
        var status = Views.Label("", 12);
        panel.Children.Add(Views.Stack(Views.Label("Calendar name"), name, Views.Label("Private ICS link"), url, Views.Button("Add calendar", () =>
        {
            if (!Uri.TryCreate(url.Text, UriKind.Absolute, out var uri) || uri.Scheme != "https" || string.IsNullOrWhiteSpace(name.Text)) { status.Text = "Enter a calendar name and a valid HTTPS ICS link."; return; }
            try { _settings.Current.Calendar.Sources.Add(new CalendarSource { Name = name.Text.Trim(), EncryptedUrl = SecretStore.Protect(uri.AbsoluteUri) }); _settings.NotifyChanged(); ShowPage(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { status.Text = "The calendar link could not be stored securely. Check your account's file permissions."; }
        }), status));
        panel.Children.Add(Number("Alert before an event (minutes; 0 disables alerts)", _settings.Current.Calendar.AlertMinutesBefore, 0, 120, v => _settings.Current.Calendar.AlertMinutesBefore = (int)v));
        panel.Children.Add(Check("Play calendar alert sounds", _settings.Current.Calendar.SoundOnAlert, v => _settings.Current.Calendar.SoundOnAlert = v)); return panel;
    }
    private Control Weather()
    {
        var city = new TextBox { Text = _settings.Current.Weather.City, Watermark = "City or region" };
        var status = Views.Label("", 12);
        var results = new ListBox { MaxHeight = 240, ItemTemplate = new FuncDataTemplate<Place>((place, _) => Views.Label(place?.Name ?? "")) };
        var search = Views.Button("Search cities", () => { });
        search.Click += async (_, _) =>
        {
            _search?.Cancel(); _search?.Dispose(); var cts = _search = new CancellationTokenSource();
            if (string.IsNullOrWhiteSpace(city.Text)) { status.Text = "Enter a city to search."; return; }
            status.Text = "Searching…";
            try { var places = await OpenMeteo.SearchAsync(city.Text, cts.Token); if (cts.IsCancellationRequested) return; results.ItemsSource = places; status.Text = places.Count == 0 ? "No places found. Try a nearby city." : "Choose the matching city and region."; }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException) { status.Text = "City search failed. Check your connection and try again."; }
        };
        results.SelectionChanged += (_, _) =>
        {
            if (results.SelectedItem is not Place place) return;
            _settings.Current.Weather.City = place.Name; _settings.Current.Weather.Latitude = place.Latitude; _settings.Current.Weather.Longitude = place.Longitude;
            _settings.NotifyChanged(); status.Text = "Location saved.";
        };
        var units = new ComboBox { ItemsSource = Enum.GetValues<TemperatureUnits>(), SelectedItem = _settings.Current.Weather.Units, HorizontalAlignment = HorizontalAlignment.Stretch };
        units.SelectionChanged += (_, _) => { if (units.SelectedItem is TemperatureUnits unit) { _settings.Current.Weather.Units = unit; _settings.NotifyChanged(); } };
        return Views.Stack(Views.Label("Weather", 28), Views.Label("Location"), city, search, status, results, Views.Label("Temperature units"), units, Views.Label("Forecasts and city search use Open-Meteo. Only your search text or chosen coordinates are sent.", 12));
    }
    private Control Notifications()
    {
        var panel = Views.Stack(Views.Label("Notifications", 28), Check("Show notification message text", _settings.Current.Notifications.ShowContent, v => _settings.Current.Notifications.ShowContent = v));
#if WINDOWS
        var access = Atoll.Modules.Notifications.NotificationAccessInfo.Get();
        var directory = Atoll.Modules.Notifications.NotificationSetup.FindDirectory();
        var guide = Atoll.Modules.Notifications.NotificationSetupGuide.For(access, directory is not null);
        var message = Views.Label(guide.Description);
        var action = Views.Button(guide.Action, () => { });
        action.Click += async (_, _) =>
        {
            action.IsEnabled = false;
            try
            {
                if (access == Atoll.Modules.Notifications.NotificationAccess.NeedsSetup && directory is not null)
                { var ok = await Atoll.Modules.Notifications.NotificationSetup.InstallAsync(directory); message.Text = ok ? "Setup complete. Quit and reopen ATOLL, then allow notification access." : "Setup did not complete. Try again."; }
                else if (access == Atoll.Modules.Notifications.NotificationAccess.NeedsPermission) { await Atoll.Modules.Notifications.NotificationAccessInfo.RequestAsync(); ShowPage(); }
                else if (access == Atoll.Modules.Notifications.NotificationAccess.Allowed) Atoll.Modules.Notifications.NotificationAccessInfo.Preview();
                else Commands.Open("ms-settings:privacy-notifications");
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { message.Text = "Notification setup did not complete. Check Windows permissions and try again."; }
            finally { action.IsEnabled = true; }
        };
        panel.Children.Add(message); panel.Children.Add(action);
#else
        panel.Children.Add(Views.Label("Mirroring other apps' notifications is currently available on Windows. macOS does not expose a supported notification listener; Linux notification services vary by desktop. Agent, timer and calendar alerts still appear in ATOLL."));
#endif
        foreach (var name in _settings.Current.Notifications.MutedApps.ToList()) panel.Children.Add(Views.Row(Views.Label(name), Views.Button("Unmute", () => { _settings.Current.Notifications.MutedApps.Remove(name); _settings.NotifyChanged(); ShowPage(); })));
        return panel;
    }
    private Control Updates()
    {
        var status = Views.Label("", 12);
        var button = Views.Button("Check for updates", () => { });
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false; status.Text = "Checking…";
            try { var result = await UpdateService.CheckAndDownloadAsync(); status.Text = result.Error is not null ? "Could not check for updates. Try again later." : result.Downloaded ? $"Version {result.Version} is ready. Quit and reopen ATOLL to update." : result.Installed ? "ATOLL is up to date." : "This copy is managed by your package manager or was built from source. Download updates from GitHub Releases."; }
            finally { button.IsEnabled = true; }
        };
        return Views.Stack(Views.Label("ATOLL", 32), Views.Label("Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "development")),
            Views.Label("A desktop island for your agents, media and daily tools."), button, status,
            Views.Button("Open GitHub releases", () => Commands.Open(UpdateService.GitHubUrl + "/releases")),
            Views.Button("Open log folder", () => Commands.Open(Path.GetDirectoryName(Log.FilePath)!)),
            Views.Label("Your settings, shelf and encrypted calendar links stay on this device. Clipboard history is kept in memory only. The app checks GitHub for updates and Open-Meteo when weather is enabled.", 12));
    }
}
