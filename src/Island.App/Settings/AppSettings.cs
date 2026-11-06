namespace Island.Settings;

public enum MonitorMode
{
    Primary,
    All,
    Selected,
}

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public int HoverDelayMs { get; set; } = 200;

    public MonitorMode MonitorMode { get; set; } = MonitorMode.Primary;

    /// <summary>Monitor device names (e.g. \\.\DISPLAY2) used when <see cref="MonitorMode"/> is Selected.</summary>
    public List<string> SelectedMonitors { get; set; } = [];

    /// <summary>Gap between the top of the screen and the island, in DIPs.</summary>
    public double TopOffset { get; set; } = 6;

    public ThemeSettings Theme { get; set; } = new();

    public ClaudeSettings Claude { get; set; } = new();

    /// <summary>List order = tab order in the expanded panel.</summary>
    public List<ModuleEntry> Modules { get; set; } = [];
}

public sealed class ThemeSettings
{
    public string Background { get; set; } = "#000000";
    public string Foreground { get; set; } = "#FFFFFF";
    public string Accent { get; set; } = "#0A84FF";

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

public sealed class ModuleEntry
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
