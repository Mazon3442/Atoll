namespace Atoll.Modules.Tide.Listen;

/// <summary>
/// Who is holding the microphone for a long recording. Listen and push-to-talk dictation both read the default
/// communications mic; the second one to ask is told who has it instead of failing with a device error.
/// </summary>
internal static class MicOwnership
{
    private static string? _owner;

    /// <summary>The name of whoever holds the mic, or null when it is free.</summary>
    public static string? BusyWith() => _owner;

    public static void Set(string owner, bool held)
    {
        if (held) _owner = owner;
        else if (_owner == owner) _owner = null;
    }
}
