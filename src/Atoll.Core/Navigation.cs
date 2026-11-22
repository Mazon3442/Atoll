namespace Atoll;

public static class Navigation
{
    public static Action<int>? OpenSettings { get; set; }

    /// <summary>Opens the island expanded on a module id (for example "calendar").</summary>
    public static Action<string>? OpenModule { get; set; }

    /// <summary>Starts or finishes voice to text, the same as its hotkey.</summary>
    public static Action? Dictate { get; set; }

    /// <summary>Opens the first-run welcome again (Settings › About).</summary>
    public static Action? OpenOnboarding { get; set; }
}
