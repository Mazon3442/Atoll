namespace Atoll;

public static class Navigation
{
    public static Action<int>? OpenSettings { get; set; }

    /// <summary>Opens the island expanded on a module id (for example "calendar").</summary>
    public static Action<string>? OpenModule { get; set; }
}
