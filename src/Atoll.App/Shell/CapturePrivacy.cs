namespace Atoll.Shell;

/// <summary>
/// Asks every island window to hide from screen capture while a private feature runs. <see cref="Set"/> is the
/// long-running switch (Tide); <see cref="Hold"/> is for a moment, such as taking a screenshot, and never undoes
/// what <see cref="Set"/> asked for.
/// </summary>
public static class CapturePrivacy
{
    private static bool _set;
    private static int _holds;

    public static event Action<bool>? Changed;

    public static void Set(bool hidden)
    {
        _set = hidden;
        Announce();
    }

    /// <summary>Hides the island from capture until the returned handle is disposed.</summary>
    public static IDisposable Hold()
    {
        _holds++;
        Announce();
        return new Hold();
    }

    internal static void Release()
    {
        _holds = Math.Max(0, _holds - 1);
        Announce();
    }

    private static void Announce() => Changed?.Invoke(_set || _holds > 0);
}

internal sealed class Hold : IDisposable
{
    private bool _released;

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        CapturePrivacy.Release();
    }
}
