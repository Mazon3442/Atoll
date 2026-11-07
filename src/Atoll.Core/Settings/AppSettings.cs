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

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }

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
    /// The compact island lets clicks through to the app behind it (e.g. browser tabs) and fades
    /// while the cursor passes over; resting on it for the hover delay makes it usable.
    /// </summary>
    public bool ClickThrough { get; set; } = true;

    public int HoverDelayMs { get; set; } = 200;

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

    public double CompactHeight { get; set; } = 34;

    /// <summary>0 = square corners, 1 = fully rounded pill.</summary>
    public double Roundness { get; set; } = 1;

    public double ExpandedWidth { get; set; } = 520;
    public double ExpandedHeight { get; set; } = 240;
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

public sealed class ModuleEntry
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
