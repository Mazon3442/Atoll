using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Atoll.Modules;
using Atoll.Modules.Claude;
using Atoll.Modules.Notifications;
using Atoll.Shell;
using Atoll.Theme;
using Atoll.Updates;

namespace Atoll.Settings;

public sealed record Choice<T>(T Value, string Label);

public sealed record SettingsPage(string Title, string Glyph);

/// <summary>Edits <see cref="SettingsService.Current"/> in place; every change applies live and saves.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _service;
    private bool _ownsWeather;

    public SettingsViewModel(SettingsService service, IReadOnlyList<ModuleDescriptor> catalog, Atoll.Modules.Weather.WeatherViewModel? weather = null)
    {
        _service = service;
        _ownsWeather = weather is null;
        Weather = weather ?? new Atoll.Modules.Weather.WeatherViewModel(service);
        if (!_ownsWeather) Weather.Disposed += OnSharedWeatherDisposed;
        WeatherLocationExpanded = S.Weather.City.Length == 0;
        Modules = new(S.Modules
            .Select(e => (Entry: e, Descriptor: catalog.FirstOrDefault(d => d.Id == e.Id)))
            .Where(x => x.Descriptor is not null)
            .Select(x => new ModuleRow(x.Entry, x.Descriptor!, Save, MoveModule)));
        RefreshModulePositions();
        Displays = new(Shell.Monitors.GetAll().Select(m => new DisplayRow(m, S, Save)));
        CalendarSources = new(S.Calendar.Sources.Select(s => new CalendarSourceRow(s) { Url = SecretStore.Unprotect(s.EncryptedUrl) ?? "" }));
        RebuildThemes();
    }

    private AppSettings S => _service.Current;
    private ThemeSettings T => S.Theme;

    // ---------- navigation ----------

    public IReadOnlyList<SettingsPage> Pages { get; } =
    [
        new("General", ""),
        new("Appearance", ""),
        new("Modules", ""),
        new("Notifications", ""),
        new("Agents", ""),
        new("Calendar", "\uE787"),
        new("Weather", "\uE753"),
        new("About", ""),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(PageDescription))]
    private int _pageIndex;

    public string PageTitle => Pages[Math.Clamp(PageIndex, 0, Pages.Count - 1)].Title;
    public string PageDescription => PageIndex switch
    {
        1 => "Make the island yours. Changes apply automatically.",
        2 => "Choose what appears in your island.",
        3 => "Decide which notifications reach you and what they show.",
        4 => "Connect your coding agents and choose their alert sounds.",
        5 => "Connect calendars and set event reminders.",
        6 => "Choose a place and explore its forecast.",
        7 => "Version, updates, and local app data.",
        _ => "Set how ATOLL opens, behaves, and sits on your displays.",
    };

    // ---------- General ----------

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set => Set(S.StartWithWindows, value, v =>
        {
            S.StartWithWindows = v;
            StartupRegistration.Apply(v);
        });
    }

    public bool ClickThrough { get => S.ClickThrough; set => Set(S.ClickThrough, value, v => S.ClickThrough = v); }

    public IReadOnlyList<Choice<FullscreenMode>> FullscreenModes { get; } =
    [
        new(FullscreenMode.PopUp, "Pop up for alerts, then hide"),
        new(FullscreenMode.Stay, "Stay visible"),
        new(FullscreenMode.Dim, "Stay visible, 50% opacity"),
        new(FullscreenMode.Hide, "Hide completely"),
    ];

    public FullscreenMode FullscreenMode { get => S.FullscreenMode; set => Set(S.FullscreenMode, value, v => S.FullscreenMode = v); }
    public int HoverDelayMs { get => S.HoverDelayMs; set => Set(S.HoverDelayMs, value, v => S.HoverDelayMs = v); }
    public double TopOffset { get => S.TopOffset; set => Set(S.TopOffset, value, v => S.TopOffset = v); }

    public ObservableCollection<DisplayRow> Displays { get; }

    public IReadOnlyList<Choice<MonitorMode>> MonitorModes { get; } =
    [
        new(MonitorMode.Primary, "Main display"),
        new(MonitorMode.All, "Every display"),
        new(MonitorMode.Selected, "Displays I choose"),
    ];

    public MonitorMode MonitorMode
    {
        get => S.MonitorMode;
        set
        {
            Set(S.MonitorMode, value, v => S.MonitorMode = v);
            OnPropertyChanged(nameof(IsSelectedMode));
        }
    }

    public bool IsSelectedMode => S.MonitorMode == MonitorMode.Selected;

    // ---------- Appearance ----------

    public ObservableCollection<ThemeCard> Themes { get; } = [];

    [ObservableProperty] private string _newThemeName = "";
    [ObservableProperty] private string _themeMessage = "";

    public bool Glass { get => T.Glass; set => Set(T.Glass, value, v => T.Glass = v); }
    public string Background { get => T.Background; set => SetColor(T.Background, value, v => T.Background = v); }
    public string Foreground { get => T.Foreground; set => SetColor(T.Foreground, value, v => T.Foreground = v); }
    public string Accent { get => T.Accent; set => SetColor(T.Accent, value, v => T.Accent = v); }
    public double Opacity { get => T.Opacity; set => Set(T.Opacity, value, v => T.Opacity = v); }
    public double CompactHeight { get => T.CompactHeight; set => Set(T.CompactHeight, value, v => T.CompactHeight = v); }
    public double Roundness { get => T.Roundness; set => Set(T.Roundness, value, v => T.Roundness = v); }
    public double ExpandedWidth { get => T.ExpandedWidth; set => Set(T.ExpandedWidth, value, v => T.ExpandedWidth = v); }
    public double ExpandedHeight { get => T.ExpandedHeight; set => Set(T.ExpandedHeight, value, v => T.ExpandedHeight = v); }

    // Live preview of the island with the current theme.
    public Brush PreviewFill
    {
        get
        {
            var bg = ThemeService.TryParseColor(T.Background, out var c) ? c : Colors.Black;
            // The brush color already carries alpha; apply only the separate background opacity here.
            return Freeze(new SolidColorBrush(bg) { Opacity = Math.Clamp(T.Opacity, 0.3, 1) });
        }
    }

    public Brush PreviewText => ColorBrush(T.Foreground, Colors.White);
    public Brush PreviewSubtle => Freeze(new SolidColorBrush(((SolidColorBrush)PreviewText).Color) { Opacity = 0.6 });
    public Brush PreviewAccent => ColorBrush(T.Accent, Colors.DodgerBlue);
    public double PreviewHeight => Math.Clamp(T.CompactHeight, 24, 60);
    public CornerRadius PreviewCorner => new(Math.Clamp(T.Roundness, 0, 1) * PreviewHeight / 2);
    public CornerRadius PreviewPeekCorner => new(Math.Clamp(T.Roundness, 0, 1) * Math.Min(PreviewHeight * 0.85, 28));
    public double PreviewBlur => T.Glass ? 18 : 0;

    [RelayCommand]
    private void ApplyTheme(ThemeCard card)
    {
        var p = card.Preset;
        T.Background = p.Background;
        T.Foreground = p.Foreground;
        T.Accent = p.Accent;
        T.Glass = p.Glass;
        foreach (var name in new[] { nameof(Background), nameof(Foreground), nameof(Accent), nameof(Glass) }) OnPropertyChanged(name);
        ThemeMessage = "";
        Save();
    }

    [RelayCommand]
    private void SaveTheme()
    {
        var name = NewThemeName.Trim();
        if (name.Length == 0)
        {
            ThemeMessage = "Give the theme a name first.";
            return;
        }
        if (ThemeService.Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ThemeMessage = $"\"{name}\" is a built-in theme. Pick another name.";
            return;
        }
        var existing = S.CustomThemes.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var theme = new ThemePreset(name, T.Background, T.Foreground, T.Accent, T.Glass);
        if (existing >= 0) S.CustomThemes[existing] = theme;
        else S.CustomThemes.Add(theme);
        ThemeMessage = existing >= 0 ? $"Updated \"{name}\"." : $"Saved \"{name}\".";
        NewThemeName = "";
        Save();
    }

    [RelayCommand]
    private void DeleteTheme(ThemeCard card)
    {
        S.CustomThemes.RemoveAll(p => p.Name == card.Preset.Name);
        ThemeMessage = $"Deleted \"{card.Preset.Name}\".";
        Save();
    }

    private void RebuildThemes()
    {
        Themes.Clear();
        foreach (var p in ThemeService.Presets) Themes.Add(new ThemeCard(p, isCustom: false));
        foreach (var p in S.CustomThemes) Themes.Add(new ThemeCard(p, isCustom: true));
        RefreshThemeSelection();
    }

    private void RefreshThemeSelection()
    {
        // The first match wins so a saved copy of a built-in theme does not light up twice.
        var match = Themes.FirstOrDefault(c => Same(c.Preset.Background, T.Background) && Same(c.Preset.Foreground, T.Foreground)
                                               && Same(c.Preset.Accent, T.Accent) && c.Preset.Glass == T.Glass);
        foreach (var c in Themes) c.IsSelected = ReferenceEquals(c, match);
    }

    private void RefreshAppearance()
    {
        foreach (var name in new[] { nameof(PreviewFill), nameof(PreviewText), nameof(PreviewSubtle), nameof(PreviewAccent),
                                     nameof(PreviewHeight), nameof(PreviewCorner), nameof(PreviewPeekCorner), nameof(PreviewBlur) })
            OnPropertyChanged(name);
        if (!Themes.Where(c => c.IsCustom).Select(c => c.Preset).SequenceEqual(S.CustomThemes))
            RebuildThemes();
        else
            RefreshThemeSelection();
    }

    // ---------- Modules ----------

    public ObservableCollection<ModuleRow> Modules { get; }
    public IReadOnlyList<int> ModulePositions => Enumerable.Range(1, Modules.Count).ToArray();

    [RelayCommand]
    private void MoveUp(ModuleRow row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(ModuleRow row) => Move(row, +1);

    private void Move(ModuleRow row, int delta) => MoveModule(row, Modules.IndexOf(row) + delta);

    public void MoveModule(ModuleRow row, int to)
    {
        var from = Modules.IndexOf(row);
        if (from < 0 || to < 0 || to >= Modules.Count || from == to) return;
        Modules.Move(from, to);
        RefreshModulePositions();

        // Rebuild the settings list in the new order, keeping entries for modules this build does not know.
        var unknown = S.Modules.Where(e => Modules.All(m => m.Entry != e)).ToList();
        S.Modules.Clear();
        S.Modules.AddRange(Modules.Select(m => m.Entry));
        S.Modules.AddRange(unknown);
        Save();
    }

    private void RefreshModulePositions()
    {
        for (var i = 0; i < Modules.Count; i++) Modules[i].UpdatePosition(i + 1);
    }

    // ---------- Weather (on the Modules page) ----------

    public string WeatherCity
    {
        get => S.Weather.City;
        set => Set(S.Weather.City, value.Trim(), v => { S.Weather.City = v; S.Weather.Latitude = null; S.Weather.Longitude = null; });
    }

    public IReadOnlyList<Choice<TemperatureUnits>> TemperatureUnitChoices { get; } =
    [
        new(TemperatureUnits.Auto, "Match my region"),
        new(TemperatureUnits.Celsius, "Celsius · km/h · mm"),
        new(TemperatureUnits.Fahrenheit, "Fahrenheit · mph · inches"),
    ];

    public TemperatureUnits TemperatureUnits { get => S.Weather.Units; set => Set(S.Weather.Units, value, v => S.Weather.Units = v); }

    // ---------- Notifications ----------

    public string NotificationStatus => NotificationAccessInfo.Describe(_notificationAccess);
    public bool CanRequestNotifications => _notificationAccess == NotificationAccess.NeedsPermission;
    public bool NotificationsAllowed => _notificationAccess == NotificationAccess.Allowed;

    public bool ShowNotificationContent
    {
        get => S.Notifications.ShowContent;
        set => Set(S.Notifications.ShowContent, value, v => S.Notifications.ShowContent = v);
    }

    public ObservableCollection<string> MutedApps => new(S.Notifications.MutedApps);
    public bool HasMutedApps => S.Notifications.MutedApps.Count > 0;

    [RelayCommand]
    private async Task AllowNotifications()
    {
        await NotificationAccessInfo.RequestAsync();
        RefreshNotificationAccess();
    }

    [RelayCommand]
    private void OpenNotificationSettings() => NotificationAccessInfo.OpenWindowsSettings();

    [RelayCommand]
    private void Unmute(string app)
    {
        S.Notifications.MutedApps.RemoveAll(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(MutedApps));
        OnPropertyChanged(nameof(HasMutedApps));
        Save();
    }

    // ---------- Agents ----------

    public IReadOnlyList<AgentIntegrationRow> AgentIntegrations { get; } =
        Atoll.Claude.HookProtocol.Agents.Select(agent => new AgentIntegrationRow(agent, new AgentIntegrationInstaller())).ToList();

    public bool SoundOnDone { get => S.Claude.SoundOnDone; set => Set(S.Claude.SoundOnDone, value, v => S.Claude.SoundOnDone = v); }
    public bool SoundOnAttention { get => S.Claude.SoundOnAttention; set => Set(S.Claude.SoundOnAttention, value, v => S.Claude.SoundOnAttention = v); }

    // ---------- Calendar ----------

    public ObservableCollection<CalendarSourceRow> CalendarSources { get; }

    private int _colorIndex;
    private static readonly string[] CalendarPalette =
        ["#0A84FF", "#30D158", "#FF9F0A", "#FF375F", "#BF5AF2", "#64D2FF", "#FFD60A", "#5E5CE6"];

    [ObservableProperty] private string _calendarMessage = "";

    public int CalendarAlertMinutes
    {
        get => S.Calendar.AlertMinutesBefore;
        set => Set(S.Calendar.AlertMinutesBefore, value, v => S.Calendar.AlertMinutesBefore = v);
    }

    public bool CalendarSoundOnAlert
    {
        get => S.Calendar.SoundOnAlert;
        set => Set(S.Calendar.SoundOnAlert, value, v => S.Calendar.SoundOnAlert = v);
    }

    [RelayCommand]
    private void AddCalendar()
    {
        var source = new CalendarSource { Name = "New calendar", Color = CalendarPalette[_colorIndex++ % CalendarPalette.Length] };
        S.Calendar.Sources.Add(source);
        CalendarSources.Add(new CalendarSourceRow(source));
        CalendarMessage = "";
        Save();
    }

    [RelayCommand]
    private void SaveCalendar(CalendarSourceRow row)
    {
        var url = row.Url.Trim();
        if (url.Length == 0)
        {
            CalendarMessage = "Paste the calendar's ICS link first.";
            return;
        }
        row.Source.Name = row.Name.Trim().Length == 0 ? "Calendar" : row.Name.Trim();
        row.Source.Color = row.Color;
        row.Source.EncryptedUrl = SecretStore.Protect(url);
        row.Url = url;
        CalendarMessage = $"Saved \"{row.Source.Name}\". It appears after the next refresh.";
        Save();
    }

    [RelayCommand]
    private void RemoveCalendar(CalendarSourceRow row)
    {
        S.Calendar.Sources.Remove(row.Source);
        CalendarSources.Remove(row);
        CalendarMessage = "";
        Save();
    }

    // ---------- About ----------

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
    public string DataFolder => Path.GetDirectoryName(SettingsStore.DefaultPath)!;

    public bool IsInstalledApp => UpdateService.IsInstalled;
    public bool HasPendingUpdate => UpdateService.PendingVersion is not null;
    public string InstalledVersionText => UpdateService.InstalledVersion ?? "";
    public string PendingUpdateText => UpdateService.PendingVersion is { } v
        ? $"Version {v} is downloaded; it installs the next time ATOLL starts."
        : "";

    public string UpdateDescription => !IsInstalledApp
        ? "ATOLL checks github.com/Mazon3442/Atoll for new releases once it is installed with Setup.exe."
        : HasPendingUpdate
            ? PendingUpdateText
            : $"Version {InstalledVersionText} · checks github.com/Mazon3442/Atoll for new releases";

    [ObservableProperty] private string _updateMessage = "";

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        UpdateMessage = "Checking for updates…";
        var result = await UpdateService.CheckAndDownloadAsync();
        UpdateMessage = result switch
        {
            { Installed: false } => "Updates work once ATOLL is installed. Run Setup.exe from a release.",
            { Error: not null } => $"Update check failed: {result.Error}",
            { Downloaded: true } => $"Version {result.Version} downloaded; it installs the next time ATOLL starts.",
            _ => "You're on the latest version.",
        };
        OnPropertyChanged(nameof(IsInstalledApp));
        OnPropertyChanged(nameof(InstalledVersionText));
        OnPropertyChanged(nameof(HasPendingUpdate));
        OnPropertyChanged(nameof(PendingUpdateText));
        OnPropertyChanged(nameof(UpdateDescription));
    }

    [RelayCommand]
    private void OpenDataFolder() => Process.Start(new ProcessStartInfo(DataFolder) { UseShellExecute = true });

    // ---------- helpers ----------

    private void SetColor(string current, string value, Action<string> assign, [CallerMemberName] string? name = null)
    {
        // Only commit parseable colors so a half-typed hex does not flash the island.
        if (!ThemeService.TryParseColor(value, out _)) return;
        Set(current, value, assign, name);
    }

    private void Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(name);
        Save();
    }

    private void Save()
    {
        RefreshAppearance();
        _service.NotifyChanged();
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Brush ColorBrush(string text, Color fallback) =>
        Freeze(new SolidColorBrush(ThemeService.TryParseColor(text, out var c) ? c : fallback));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

public sealed partial class ThemeCard(ThemePreset preset, bool isCustom) : ObservableObject
{
    public ThemePreset Preset { get; } = preset;
    public bool IsCustom { get; } = isCustom;

    [ObservableProperty] private bool _isSelected;
}

public sealed class ModuleRow(ModuleEntry entry, ModuleDescriptor descriptor, Action save, Action<ModuleRow, int> move) : ObservableObject
{
    private int _position;
    public ModuleEntry Entry { get; } = entry;
    public string Name => descriptor.Name;
    public string Glyph => descriptor.Glyph;
    public string Group => PanelGroups.TabId(Entry.Id) switch
    {
        "clock" => "Clock & calendar tab", "media" => "Media tab", "shelf" => "Files tab", "system" => "System tab",
        "claude" => "Agents tab", "notifications" => "Notifications tab", "weather" => "Weather tab", _ => "",
    };
    public int Position { get => _position; set { if (value != _position) move(this, value - 1); } }
    internal void UpdatePosition(int position) { _position = position; OnPropertyChanged(nameof(Position)); }

    public bool Enabled
    {
        get => Entry.Enabled;
        set
        {
            if (Entry.Enabled == value) return;
            Entry.Enabled = value;
            OnPropertyChanged();
            save();
        }
    }
}

public sealed class DisplayRow(MonitorInfo monitor, AppSettings settings, Action save) : ObservableObject
{
    public string Label => monitor.Label;

    public bool IsSelected
    {
        get => settings.SelectedMonitors.Contains(monitor.DeviceName);
        set
        {
            if (IsSelected == value) return;
            if (value) settings.SelectedMonitors.Add(monitor.DeviceName);
            else settings.SelectedMonitors.Remove(monitor.DeviceName);
            OnPropertyChanged();
            save();
        }
    }
}

/// <summary>Editable row for one ICS calendar. <see cref="Url"/> is plaintext while editing; it is encrypted on save.</summary>
public sealed class CalendarSourceRow(CalendarSource source) : ObservableObject
{
    public CalendarSource Source { get; } = source;

    public string Name
    {
        get => Source.Name;
        set { if (Source.Name != value) { Source.Name = value; OnPropertyChanged(); } }
    }

    public string Color
    {
        get => Source.Color;
        set { if (Source.Color != value) { Source.Color = value; OnPropertyChanged(); } }
    }

    public string Url { get; set; } = "";
}
