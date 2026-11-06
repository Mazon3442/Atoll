namespace Island.Shell;

public static class WindowFocus
{
    private const int SW_RESTORE = 9;

    /// <summary>Restores and focuses another app's window. Works right after the user clicked the island.</summary>
    public static bool Bring(nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd)) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, SW_RESTORE);
        return Native.SetForegroundWindow(hwnd);
    }
}
