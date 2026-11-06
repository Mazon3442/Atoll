using System.Text;
using System.Windows.Threading;

namespace Island.Shell;

/// <summary>
/// Raises <see cref="Check"/> when the foreground window changes and once a second (apps can go
/// fullscreen without changing focus, e.g. F11 or YouTube fullscreen). UI thread only.
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly DispatcherTimer _timer;
    private readonly Native.WinEventProc _proc; // held so the GC does not collect the callback
    private readonly nint _hook;

    public FullscreenWatcher()
    {
        _proc = (_, _, _, _, _, _, _) => Check?.Invoke();
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Check?.Invoke();
        _timer.Start();
    }

    public event Action? Check;

    /// <summary>True when the foreground window (not ours, not the desktop, not maximized) covers the whole monitor.</summary>
    public static bool IsFullscreenOn(MonitorInfo monitor)
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == 0 || hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow()) return false;
        if (Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONULL) != monitor.Handle) return false;

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == OwnProcessId) return false;

        var cls = new StringBuilder(64);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "WorkerW" or "Progman") return false;

        // Maximized windows can match the monitor when the taskbar auto-hides; they are not fullscreen.
        if (Native.IsZoomed(hwnd)) return false;

        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var b = monitor.Bounds;
        return r.Left <= b.X && r.Top <= b.Y && r.Right >= b.X + b.Width && r.Bottom >= b.Y + b.Height;
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_hook != 0) Native.UnhookWinEvent(_hook);
    }
}
