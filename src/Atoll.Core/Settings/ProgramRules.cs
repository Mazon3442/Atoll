namespace Atoll.Settings;

/// <summary>What the island does while a named program is in front, whatever size its window is.</summary>
public sealed class ProgramRule
{
    /// <summary>The program's file name, with or without ".exe" (for example "chrome").</summary>
    public string Process { get; set; } = "";

    public FullscreenMode Mode { get; set; } = FullscreenMode.Hide;
}

/// <summary>Finds the rule for the program in front.</summary>
public static class ProgramRules
{
    /// <summary>"C:\Apps\Chrome.EXE", "chrome.exe" and "Chrome" are all the same program.</summary>
    public static string Normalize(string? program)
    {
        var name = System.IO.Path.GetFileName((program ?? "").Trim().Trim('"'));
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>The mode a rule gives to <paramref name="foreground"/>, or null when no rule names it.</summary>
    public static FullscreenMode? Match(IEnumerable<ProgramRule> rules, string? foreground)
    {
        var name = Normalize(foreground);
        if (name.Length == 0) return null;
        return rules.FirstOrDefault(rule => rule.Process.Length > 0 && Normalize(rule.Process).Equals(name, StringComparison.OrdinalIgnoreCase))?.Mode;
    }
}