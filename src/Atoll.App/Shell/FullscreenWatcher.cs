using System.Text;
#if AVALONIA
using Avalonia.Threading;
using Int32Rect = Avalonia.PixelRect;
#else
using System.Windows;
using System.Windows.Threading;
#endif
using Microsoft.Win32;

namespace Atoll.Shell;

/// <summary>What the foreground window on a monitor is doing, as far as the island cares.</summary>
public enum ForegroundKind
{
    Normal,
    Maximized,
    Fullscreen,

    /// <summary>A game in fullscreen or maximized: the island hides completely.</summary>
    Game,
}

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

    /// <summary>
    /// What the foreground window on <paramref name="monitor"/> is doing, or null while one of ATOLL's own windows
    /// (the open island, Settings) is in front: the caller keeps its last answer, because opening the island must
    /// not make it forget the maximized or fullscreen app behind it.
    /// </summary>
    public static ForegroundKind? Classify(MonitorInfo monitor)
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == 0 || hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow()) return ForegroundKind.Normal;

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == OwnProcessId) return null;

        // Monitor handles can change after sleep or a display change; bounds identify the display reliably.
        if (Monitors.FromWindow(hwnd) is not { } shown || shown.Bounds != monitor.Bounds) return ForegroundKind.Normal;

        var cls = new StringBuilder(64);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "WorkerW" or "Progman") return ForegroundKind.Normal;

        if (!Native.GetWindowRect(hwnd, out var r)) return ForegroundKind.Normal;
        var maximized = Native.IsZoomed(hwnd);
        var fullscreen = IsFullscreen(r.Left, r.Top, r.Right, r.Bottom, monitor.Bounds, maximized);
        if (!maximized && !fullscreen) return ForegroundKind.Normal;

        // Exclusive-fullscreen Direct3D apps are games (or similar) by definition.
        var d3d = Native.SHQueryUserNotificationState(out var state) == 0 && state == Native.QUNS_RUNNING_D3D_FULL_SCREEN;
        if (d3d || GameDetector.IsGame(pid)) return ForegroundKind.Game;
        return maximized ? ForegroundKind.Maximized : ForegroundKind.Fullscreen;
    }

    /// <summary>
    /// A window is fullscreen when it covers the monitor. Browsers keep the maximized flag when a video goes
    /// fullscreen from a maximized window, so a maximized window that exactly matches the monitor counts too.
    /// An ordinary maximized window overhangs the monitor by its frame (or stops at the taskbar), so it does not.
    /// </summary>
    internal static bool IsFullscreen(int left, int top, int right, int bottom, Int32Rect monitor, bool maximized)
    {
        int mr = monitor.X + monitor.Width, mb = monitor.Y + monitor.Height;
        if (!maximized) return left <= monitor.X && top <= monitor.Y && right >= mr && bottom >= mb;
        return Math.Abs(left - monitor.X) <= 1 && Math.Abs(top - monitor.Y) <= 1 && Math.Abs(right - mr) <= 1 && Math.Abs(bottom - mb) <= 1;
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_hook != 0) Native.UnhookWinEvent(_hook);
    }
}

/// <summary>
/// Decides whether a process is a game: an exe Windows Game Bar has recorded as a game
/// (HKCU\System\GameConfigStore), or one installed in a common game-launcher library folder.
/// Results are cached per process id.
/// </summary>
internal static class GameDetector
{
    private static readonly string[] LibraryFolders =
        [@"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\GOG Games\", @"\GOG Galaxy\Games\", @"\XboxGames\", @"\EA Games\", @"\Ubisoft Game Launcher\games\"];

    private static readonly Dictionary<uint, bool> Cache = [];
    private static HashSet<string> _gameBarExes = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _gameBarLoaded = DateTime.MinValue;

    // Apps sold on Steam that are not games (Wallpaper Engine draws the desktop, so it was "playing" all day).
    private static readonly string[] NotGames = [@"\wallpaper_engine", @"\Steamworks Shared", @"\SteamVR", @"\Soundpad", @"\OVR Toolkit"];

    internal static bool IsNotAGame(string path) => NotGames.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));

    public static bool IsGame(uint pid)
    {
        if (Cache.TryGetValue(pid, out var known)) return known;
        var path = Native.GetProcessPath(pid);
        var isGame = path is not null && !IsNotAGame(path)
            && (LibraryFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)) || GameBarExes().Contains(path));
        if (Cache.Count > 256) Cache.Clear(); // pids get reused; keep the cache small
        Cache[pid] = isGame;
        return isGame;
    }

    private static HashSet<string> GameBarExes()
    {
        if (DateTime.UtcNow - _gameBarLoaded < TimeSpan.FromMinutes(5)) return _gameBarExes;
        _gameBarLoaded = DateTime.UtcNow;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var name in children?.GetSubKeyNames() ?? [])
            {
                using var child = children!.OpenSubKey(name);
                if (child?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0) set.Add(exe);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No Game Bar data; library folders still work.
        }
        _gameBarExes = set;
        return set;
    }
}
