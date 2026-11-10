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
        BuildModuleTabs(catalog);
        Displays = new(Shell.Monitors.GetAll().Select(m => new DisplayRow(m, S, Save)));
        CalendarSources = new(S.Calendar.Sources.Select(s => new CalendarSourceRow(s) { Url = SecretStore.Unprotect(s.EncryptedUrl) ?? "" }));
        LoadProviderAccounts();
        RebuildThemes();
        HookShortcuts();
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
        new("Games", "\uE7FC"),
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
        4 => "Connect coding agents or your own API keys, ask a quick question, and choose alert sounds.",
        5 => "Connect calendars and set event reminders.",
        6 => "Choose a place and explore its forecast.",
        7 => "Set how games appear, what ATOLL watches, and your Minecraft server.",
        8 => "Version, updates, and local app data.",
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
    public bool ShowBattery { get => S.ShowBattery; set => Set(S.ShowBattery, value, v => S.ShowBattery = v); }

    /// <summary>Desktop PCs have no battery, so the battery card is not shown at all.</summary>
    public bool HasBattery => Atoll.Modules.Indicators.BatteryPresence.Present;

    public IReadOnlyList<Choice<FullscreenMode>> FullscreenModes { get; } =
    [
        new(FullscreenMode.Stay, "Stay visible"),
        new(FullscreenMode.Dim, "Stay visible, dimmed"),
        new(FullscreenMode.PopUp, "Hide, but show notifications"),
        new(FullscreenMode.Hide, "Hide completely"),
    ];

    public FullscreenMode FullscreenMode { get => S.FullscreenMode; set => Set(S.FullscreenMode, value, v => S.FullscreenMode = v); }
    public FullscreenMode MaximizedMode { get => S.MaximizedMode; set => Set(S.MaximizedMode, value, v => S.MaximizedMode = v); }
    public bool HideWhenIdle { get => S.HideWhenIdle; set => Set(S.HideWhenIdle, value, v => S.HideWhenIdle = v); }
    public double RevealHoldSeconds
    {
        get => S.EdgeHoldMs / 1000.0;
        set => Set(S.EdgeHoldMs, (int)Math.Round(Math.Clamp(value, 0.5, 5) * 1000), v => S.EdgeHoldMs = v);
    }
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
    public double CompactWidth { get => T.CompactWidth; set => Set(T.CompactWidth, value, v => T.CompactWidth = v); }
    public bool Outline { get => T.Outline; set => Set(T.Outline, value, v => T.Outline = v); }
    public bool ShowSideActivities { get => T.ShowSideActivities; set => Set(T.ShowSideActivities, value, v => T.ShowSideActivities = v); }

    // Two radio buttons bind here; only the checked one writes.
    public bool IsPill { get => T.Shape == IslandShape.Pill; set { if (value) SetShape(IslandShape.Pill); } }
    public bool IsNotch { get => T.Shape == IslandShape.Notch; set { if (value) SetShape(IslandShape.Notch); } }

    private void SetShape(IslandShape shape)
    {
        Set(T.Shape, shape, v => T.Shape = v, nameof(IsPill));
        OnPropertyChanged(nameof(IsNotch));
    }

    public IReadOnlyList<Choice<MotionStyle>> MotionStyles { get; } =
    [
        new(MotionStyle.Bouncy, "Bouncy"),
        new(MotionStyle.Smooth, "Smooth, no bounce"),
        new(MotionStyle.Snappy, "Snappy, quick settle"),
        new(MotionStyle.Off, "Off"),
    ];

    public MotionStyle Motion { get => T.Motion; set => Set(T.Motion, value, v => T.Motion = v); }

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
    public CornerRadius PreviewCorner => Corners(Math.Clamp(T.Roundness, 0, 1) * PreviewHeight / 2);
    public CornerRadius PreviewPeekCorner => Corners(Math.Clamp(T.Roundness, 0, 1) * Math.Min(PreviewHeight * 0.85, 28));
    public double PreviewWidth => Math.Clamp(T.CompactWidth, 60, 400);
    public Thickness PreviewTopGap => new(0, IsNotch ? 0 : 6 + Math.Clamp(S.TopOffset, 0, 80) / 2, 0, 0);
    public Thickness PreviewRimThickness => IsNotch ? new(1, 0, 1, 1) : new(1);
    public Brush PreviewRim => T.Outline ? Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))) : Brushes.Transparent;

    private CornerRadius Corners(double r) => IsNotch ? new(0, 0, r, r) : new(r);
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
                                     nameof(PreviewHeight), nameof(PreviewCorner), nameof(PreviewPeekCorner), nameof(PreviewBlur),
                                     nameof(PreviewWidth), nameof(PreviewTopGap), nameof(PreviewRimThickness), nameof(PreviewRim) })
            OnPropertyChanged(name);
        if (!Themes.Where(c => c.IsCustom).Select(c => c.Preset).SequenceEqual(S.CustomThemes))
            RebuildThemes();
        else
            RefreshThemeSelection();
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

    public bool FocusEnabled { get => S.Focus.Enabled; set => Set(S.Focus.Enabled, value, v => S.Focus.Enabled = v); }
    public bool FollowWindowsFocus { get => S.Focus.FollowWindowsFocus; set => Set(S.Focus.FollowWindowsFocus, value, v => S.Focus.FollowWindowsFocus = v); }
    public bool ShowFocusChip { get => S.Focus.ShowChip; set => Set(S.Focus.ShowChip, value, v => S.Focus.ShowChip = v); }

    // Home tab cards
    public bool HomeEnabled { get => S.Home.Enabled; set => Set(S.Home.Enabled, value, v => S.Home.Enabled = v); }

    /// <summary>On pins Home to the top row; off leaves it in the ⋯ menu while it stays enabled.</summary>
    public bool HomeBookmarked
    {
        get => S.Home.Bookmarked ?? true;
        set
        {
            if (HomeBookmarked == value) return;
            S.Home.SetPlacement(value ? FeaturePlacement.Bookmarked : S.Home.Enabled ? FeaturePlacement.Menu : FeaturePlacement.Off);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HomeEnabled));
            Save();
        }
    }

    public bool HomeMedia { get => S.Home.Media; set => Set(S.Home.Media, value, v => S.Home.Media = v); }
    public bool HomeWeather { get => S.Home.Weather; set => Set(S.Home.Weather, value, v => S.Home.Weather = v); }
    public bool HomeTimer { get => S.Home.Timer; set => Set(S.Home.Timer, value, v => S.Home.Timer = v); }
    public bool HomeCalendar { get => S.Home.Calendar; set => Set(S.Home.Calendar, value, v => S.Home.Calendar = v); }
    public bool HomeUpNext { get => S.Home.UpNext; set => Set(S.Home.UpNext, value, v => S.Home.UpNext = v); }
    public bool HomeGame { get => S.Home.Game; set => Set(S.Home.Game, value, v => S.Home.Game = v); }

    // Games
    public IReadOnlyList<Choice<GameIslandMode>> GameModes { get; } =
    [
        new(GameIslandMode.Overlay, "Show a small game overlay"),
        new(GameIslandMode.Hidden, "Hide the island"),
    ];
    public GameIslandMode GameMode { get => S.Games.Mode; set => Set(S.Games.Mode, value, v => S.Games.Mode = v); }
    public bool GameMuteNotifications { get => S.Games.MuteNotifications; set => Set(S.Games.MuteNotifications, value, v => S.Games.MuteNotifications = v); }
    public bool GameShowPerformance { get => S.Games.ShowPerformance; set => Set(S.Games.ShowPerformance, value, v => S.Games.ShowPerformance = v); }
    public bool GameShowPlayTime { get => S.Games.ShowPlayTime; set => Set(S.Games.ShowPlayTime, value, v => S.Games.ShowPlayTime = v); }
    public bool GameAnnounceStart { get => S.Games.AnnounceGameStart; set => Set(S.Games.AnnounceGameStart, value, v => S.Games.AnnounceGameStart = v); }
    public bool GameDetect { get => S.Games.DetectGames; set => Set(S.Games.DetectGames, value, v => S.Games.DetectGames = v); }
    public bool GameScanSteam { get => S.Games.ScanSteam; set => Set(S.Games.ScanSteam, value, v => S.Games.ScanSteam = v); }
    public bool GameScanEpic { get => S.Games.ScanEpic; set => Set(S.Games.ScanEpic, value, v => S.Games.ScanEpic = v); }
    public bool GameScanMinecraft { get => S.Games.ScanMinecraft; set => Set(S.Games.ScanMinecraft, value, v => S.Games.ScanMinecraft = v); }
    public string MinecraftServer { get => S.Games.MinecraftServer; set => Set(S.Games.MinecraftServer, value.Trim(), v => S.Games.MinecraftServer = v); }
    public bool GameShowServer { get => S.Games.ShowServer; set => Set(S.Games.ShowServer, value, v => S.Games.ShowServer = v); }
    public IReadOnlyList<Choice<int>> GameServerIntervals { get; } =
    [
        new(30, "Every 30 seconds"),
        new(60, "Every minute"),
        new(120, "Every 2 minutes"),
        new(300, "Every 5 minutes"),
    ];
    public int GameServerInterval { get => S.Games.ServerRefreshSeconds; set => Set(S.Games.ServerRefreshSeconds, value, v => S.Games.ServerRefreshSeconds = v); }

    public bool ShowLyrics { get => S.ShowLyrics; set => Set(S.ShowLyrics, value, v => S.ShowLyrics = v); }

    /// <summary>Opt-in: the clipboard module reads and writes nothing while this is false.</summary>
    public bool ClipboardHistoryEnabled
    {
        get => S.Clipboard.HistoryEnabled;
        set => Set(S.Clipboard.HistoryEnabled, value, v => S.Clipboard.HistoryEnabled = v);
    }

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
        Atoll.Claude.HookProtocol.Agents.Append("shell").Select(agent => new AgentIntegrationRow(agent, new AgentIntegrationInstaller())).ToList();

    public bool SoundOnDone { get => S.Claude.SoundOnDone; set => Set(S.Claude.SoundOnDone, value, v => S.Claude.SoundOnDone = v); }
    public bool SoundOnAttention { get => S.Claude.SoundOnAttention; set => Set(S.Claude.SoundOnAttention, value, v => S.Claude.SoundOnAttention = v); }

    // The Ask section: chat-only by default, with an explicit switch for tools.
    public bool AskAllowTools { get => S.AgentChat.AllowTools; set => Set(S.AgentChat.AllowTools, value, v => S.AgentChat.AllowTools = v); }
    public string AskClaudeModel { get => S.AgentChat.ClaudeModel; set => Set(S.AgentChat.ClaudeModel, value, v => S.AgentChat.ClaudeModel = v); }
    public string AskCodexModel { get => S.AgentChat.CodexModel; set => Set(S.AgentChat.CodexModel, value, v => S.AgentChat.CodexModel = v); }
    public string AskOpenCodeModel { get => S.AgentChat.OpenCodeModel; set => Set(S.AgentChat.OpenCodeModel, value, v => S.AgentChat.OpenCodeModel = v); }
    public string AskApiModel { get => S.AgentChat.ApiModel; set => Set(S.AgentChat.ApiModel, value, v => S.AgentChat.ApiModel = v); }
    public int AskTimeoutSeconds
    {
        get => S.AgentChat.TimeoutSeconds;
        set => Set(S.AgentChat.TimeoutSeconds, Math.Clamp(value, 15, 600), v => S.AgentChat.TimeoutSeconds = v);
    }

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

/// <summary>One feature's show/bookmark switches inside a tab card on the Modules page.</summary>
public sealed class ModuleRow(ModuleEntry entry, string name, string description, string glyph, Action toggled,
    bool bookmarkedByDefault = true) : ObservableObject
{
    public ModuleEntry Entry { get; } = entry;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string Glyph { get; } = glyph;

    /// <summary>On means it appears somewhere; off keeps it out of the island entirely.</summary>
    public bool Enabled
    {
        get => Entry.Enabled;
        set
        {
            if (Entry.Enabled == value) return;
            Entry.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Bookmarked));
            toggled();
        }
    }

    /// <summary>On pins the tab to the top row; off leaves an enabled feature in the ⋯ menu.</summary>
    public bool Bookmarked
    {
        get => Entry.Bookmarked ?? bookmarkedByDefault;
        set
        {
            if (Bookmarked == value) return;
            Entry.SetPlacement(value ? FeaturePlacement.Bookmarked : Entry.Enabled ? FeaturePlacement.Menu : FeaturePlacement.Off);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Enabled));
            toggled();
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Bookmarked));
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
