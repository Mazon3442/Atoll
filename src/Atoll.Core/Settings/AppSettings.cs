using System.Globalization;
using System.Text.Json.Serialization;
using Atoll.Modules.Calendar;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Settings;

public enum MonitorMode
{
    Primary,
    All,
    Selected,
}

public enum FullscreenMode
{
    /// <summary>Hidden, but pops up for alerts and short indicators (volume, notifications), then goes away.</summary>
    PopUp,
    Stay,
    Dim,
    Hide,
}

public enum IslandShape
{
    /// <summary>Floats below the top edge with every corner rounded.</summary>
    Pill,
    /// <summary>Attached to the top edge; only the bottom corners are rounded.</summary>
    Notch,
}

public enum MotionStyle
{
    Bouncy,
    Smooth,
    Off,
}

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }

    /// <summary>False until the welcome setup has been finished or skipped.</summary>
    public bool OnboardingComplete { get; set; }

    /// <summary>Looks up time-synced lyrics for the playing song on lrclib.net.</summary>
    public bool ShowLyrics { get; set; } = true;

    /// <summary>The media tab's volume slider changes only the playing app (true) or all sound (false).</summary>
    public bool VolumeControlsApp { get; set; } = true;

    public HomeSettings Home { get; set; } = new();
    public GameSettings Games { get; set; } = new();

    /// <summary>What the island does over fullscreen apps (videos, games, F11 browsers).</summary>
    public FullscreenMode FullscreenMode { get; set; } = FullscreenMode.PopUp;

    /// <summary>What the island does while a maximized window is in front. Hide and PopUp reveal with an edge hold.</summary>
    public FullscreenMode MaximizedMode { get; set; } = FullscreenMode.Stay;

    /// <summary>Hide the island while nothing is happening; it returns for activity or an edge hold.</summary>
    public bool HideWhenIdle { get; set; }

    /// <summary>
    /// How long to hold the pointer at the top edge, near the island, to bring back a hidden island. Replaces
    /// RevealHoldMs (3 s), which v1.0.2 saved for everyone; the old value is ignored so the shorter default applies.
    /// </summary>
    public int EdgeHoldMs { get; set; } = 1000;

    /// <summary>
    /// The compact island lets clicks through to a maximized app behind it (e.g. browser tabs) and fades
    /// while the cursor passes over; resting on it for the hover delay makes it usable.
    /// </summary>
    public bool ClickThrough { get; set; } = true;

    public int HoverDelayMs { get; set; } = 200;

    /// <summary>Header battery chip and low-battery behavior on laptops. Desktops have no battery to show.</summary>
    public bool ShowBattery { get; set; } = true;

    /// <summary>System-wide shortcut that opens the island with keyboard focus, or closes it. Empty turns it off.</summary>
    public string OpenShortcut { get; set; } = "Ctrl+Alt+Space";

    /// <summary>System-wide shortcut that hides or shows the island, like the tray's "Hide island". Empty turns it off.</summary>
    public string HideShortcut { get; set; } = "Ctrl+Alt+H";

    /// <summary>System-wide shortcut that opens the command palette. Empty turns it off.</summary>
    public string PaletteShortcut { get; set; } = "Ctrl+Alt+K";

    public MonitorMode MonitorMode { get; set; } = MonitorMode.Primary;

    /// <summary>Monitor device names (e.g. \\.\DISPLAY2) used when <see cref="MonitorMode"/> is Selected.</summary>
    public List<string> SelectedMonitors { get; set; } = [];

    /// <summary>Gap between the top of the screen and the island, in DIPs.</summary>
    public double TopOffset { get; set; } = 6;

    public ThemeSettings Theme { get; set; } = new();

    /// <summary>Themes the user saved from the Appearance page.</summary>
    public List<Atoll.Theme.ThemePreset> CustomThemes { get; set; } = [];

    public ClaudeSettings Claude { get; set; } = new();
    public ProviderSettings Providers { get; set; } = new();

    public NotificationSettings Notifications { get; set; } = new();

    public WeatherSettings Weather { get; set; } = new();

    public CalendarSettings Calendar { get; set; } = new();

    public NotesSettings Notes { get; set; } = new();

    public PomodoroSettings Pomodoro { get; set; } = new();

    public FocusSettings Focus { get; set; } = new();

    /// <summary>List order = tab order in the expanded panel.</summary>
    public List<ModuleEntry> Modules { get; set; } = [];
}

public sealed class ProviderSettings
{
    public bool GatewayEnabled { get; set; }
    public string EncryptedGatewayToken { get; set; } = "";
    public List<ProviderAccount> Accounts { get; set; } = [];
}

public sealed class ProviderAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "OpenAI";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string EncryptedKey { get; set; } = "";
    public string EncryptedAdminKey { get; set; } = "";
}

public sealed class ThemeSettings
{
    public string Background { get; set; } = "#000000";
    public string Foreground { get; set; } = "#FFFFFF";
    public string Accent { get; set; } = "#0A84FF";

    /// <summary>Frosted glass: blur what is behind the island, tinted with <see cref="Background"/>.</summary>
    public bool Glass { get; set; }

    /// <summary>Island background opacity, 0.3–1.</summary>
    public double Opacity { get; set; } = 1;

    public IslandShape Shape { get; set; } = IslandShape.Pill;

    /// <summary>Thin rim around the island.</summary>
    public bool Outline { get; set; } = true;

    /// <summary>Small bubbles beside the island for other running activities.</summary>
    public bool ShowSideActivities { get; set; } = true;

    public MotionStyle Motion { get; set; } = MotionStyle.Bouncy;

    public double CompactHeight { get; set; } = 34;

    /// <summary>Narrowest the resting island gets, in DIPs; content can make it wider.</summary>
    public double CompactWidth { get; set; } = 110;

    /// <summary>0 = square corners, 1 = fully rounded pill.</summary>
    public double Roundness { get; set; } = 1;

    public double ExpandedWidth { get; set; } = 640; // room for the Home dashboard's two columns
    public double ExpandedHeight { get; set; } = 260;
}

/// <summary>The Home tab: a dashboard of the other features. Each card can be turned off.</summary>
public sealed class HomeSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Null keeps Home in the top row, as it was before bookmarks existed.</summary>
    public bool? Bookmarked { get; set; }

    [JsonIgnore]
    public FeaturePlacement Placement => !Enabled ? FeaturePlacement.Off
        : Bookmarked == false ? FeaturePlacement.Menu
        : FeaturePlacement.Bookmarked;

    public void SetPlacement(FeaturePlacement placement)
    {
        switch (placement)
        {
            case FeaturePlacement.Off:
                Enabled = false;
                break;
            case FeaturePlacement.Menu:
                Enabled = true;
                Bookmarked = false;
                break;
            default:
                Enabled = true;
                Bookmarked = true;
                break;
        }
    }

    public bool Media { get; set; } = true;
    public bool Weather { get; set; } = true;
    public bool Timer { get; set; } = true;
    public bool Calendar { get; set; } = true;
    public bool UpNext { get; set; } = true;
    public bool Game { get; set; } = true;
}

public enum GameIslandMode { Overlay, Hidden }

public sealed class GameSettings
{
    /// <summary>Overlay: a small pill with the game, play time and CPU/GPU while you play. Hidden: out of the way until the open shortcut.</summary>
    public GameIslandMode Mode { get; set; } = GameIslandMode.Overlay;
    public bool MuteNotifications { get; set; } = true;
    /// <summary>Show CPU, GPU and GPU temperature in the game overlay and panel.</summary>
    public bool ShowPerformance { get; set; } = true;
    /// <summary>Show how long the current session has run.</summary>
    public bool ShowPlayTime { get; set; } = true;
    /// <summary>Pop a small island when a game starts.</summary>
    public bool AnnounceGameStart { get; set; } = true;
    /// <summary>Notice games automatically. Off leaves the library and server available.</summary>
    public bool DetectGames { get; set; } = true;
    public bool ScanSteam { get; set; } = true;
    public bool ScanEpic { get; set; } = true;
    public bool ScanMinecraft { get; set; } = true;
    /// <summary>A Minecraft (Java) server to watch, "host" or "host:port". Empty: none.</summary>
    public string MinecraftServer { get; set; } = "";
    public bool ShowServer { get; set; } = true;
    /// <summary>Seconds between Minecraft server checks.</summary>
    public int ServerRefreshSeconds { get; set; } = 60;
}

public sealed class ClaudeSettings
{
    public bool SoundOnDone { get; set; } = true;
    public bool SoundOnAttention { get; set; } = true;
}

public sealed class NotificationSettings
{
    /// <summary>False shows only the app name, for privacy (e.g. while screen sharing).</summary>
    public bool ShowContent { get; set; } = true;

    /// <summary>App display names whose notifications are ignored.</summary>
    public List<string> MutedApps { get; set; } = [];
}

public enum TemperatureUnits
{
    Auto,
    Celsius,
    Fahrenheit,
}

public sealed class WeatherSettings
{
    /// <summary>Place name looked up with Open-Meteo's geocoding (e.g. "Chicago" or "Paris, France").</summary>
    public string City { get; set; } = "";

    /// <summary>Coordinates chosen from search or Windows location. Null keeps legacy city lookup working.</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public TemperatureUnits Units { get; set; } = TemperatureUnits.Auto;
}

public sealed class CalendarSettings
{
    /// <summary>How long before an event the island pops up. 0 disables alerts.</summary>
    public int AlertMinutesBefore { get; set; } = 10;

    public bool SoundOnAlert { get; set; } = true;

    /// <summary>ICS calendars to merge. The URL is DPAPI-encrypted (see SecretStore).</summary>
    public List<CalendarSource> Sources { get; set; } = [];

    public Dictionary<string, DateTimeOffset> DismissedOccurrences { get; set; } = [];
    public string? PinnedOccurrence { get; set; }
}

public sealed class CalendarSource
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#0A84FF";

    /// <summary>DPAPI-protected ICS URL, Base64. Empty until the user saves a URL.</summary>
    public string EncryptedUrl { get; set; } = "";
}

/// <summary>Where a feature appears: pinned to the island's top row, in the ⋯ menu, or not at all.</summary>
public enum FeaturePlacement
{
    Bookmarked,
    Menu,
    Off,
}

public sealed class ModuleEntry
{
    public string Id { get; set; } = "";

    /// <summary>False keeps the feature out of the island entirely. Kept for older settings files.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Null means the default, which is bookmarked: settings saved before bookmarks, and new features,
    /// keep their tab in the top row. False moves an enabled feature into the ⋯ menu.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Bookmarked { get; set; }

    [JsonIgnore]
    public FeaturePlacement Placement => !Enabled ? FeaturePlacement.Off
        : Bookmarked == false ? FeaturePlacement.Menu
        : FeaturePlacement.Bookmarked;

    /// <summary>Moves the feature without forgetting where it was, so turning it back on restores it.</summary>
    public void SetPlacement(FeaturePlacement placement)
    {
        switch (placement)
        {
            case FeaturePlacement.Off:
                Enabled = false;
                break;
            case FeaturePlacement.Menu:
                Enabled = true;
                Bookmarked = false;
                break;
            default:
                Enabled = true;
                Bookmarked = true;
                break;
        }
    }
}

/// <summary>Quiet-mode switches for ATOLL's Do Not Disturb.</summary>
public sealed class FocusSettings
{
    /// <summary>Shows the focus controls. Off hides the moon chip and the follow option.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Stay quiet whenever Windows Focus/Do Not Disturb is on.</summary>
    public bool FollowWindowsFocus { get; set; } = true;

    /// <summary>Show the moon chip in the island header.</summary>
    public bool ShowChip { get; set; } = true;
}

/// <summary>Timer lengths for the Pomodoro section of the timer tab.</summary>
public sealed class PomodoroSettings
{
    /// <summary>Minutes in one focus session (5-120).</summary>
    public int FocusMinutes { get; set; } = 25;

    /// <summary>Minutes in a short break (1-60).</summary>
    public int ShortBreakMinutes { get; set; } = 5;

    /// <summary>Minutes in a long break (5-120).</summary>
    public int LongBreakMinutes { get; set; } = 15;

    /// <summary>Focus sessions before a long break instead of a short one (1-12).</summary>
    public int SessionsBeforeLongBreak { get; set; } = 4;
}

/// <summary>Notes, one-off reminders and a small to-do list, kept with the rest of the settings.</summary>
public sealed class NotesSettings
{
    public List<NoteItem> Notes { get; set; } = [];
    public List<ReminderItem> Reminders { get; set; } = [];
    public List<TodoItem> Todos { get; set; } = [];
}

public sealed partial class NoteItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatedText))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatedText))]
    private DateTimeOffset _updated = DateTimeOffset.Now;

    [JsonIgnore]
    public string UpdatedText => Updated.LocalDateTime.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
}

public sealed partial class ReminderItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverdue))]
    private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DueText), nameof(IsOverdue))]
    private DateTimeOffset _due = DateTimeOffset.Now;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DueText), nameof(IsOverdue))]
    private bool _done;

    [JsonIgnore] public string DueText => Done ? "Done" : IcsCalendar.Relative(Due, DateTimeOffset.Now);
    [JsonIgnore] public bool IsOverdue => !Done && Due <= DateTimeOffset.Now;

    internal void RefreshTime()
    {
        OnPropertyChanged(nameof(DueText));
        OnPropertyChanged(nameof(IsOverdue));
    }
}

public sealed partial class TodoItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _done;
}
