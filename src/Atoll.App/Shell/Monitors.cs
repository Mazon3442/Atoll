using System.Runtime.InteropServices;
using System.Windows;

namespace Atoll.Shell;

/// <summary>A display in physical pixels. <see cref="Scale"/> = monitor DPI / 96.</summary>
public sealed record MonitorInfo(nint Handle, string DeviceName, Int32Rect Bounds, Int32Rect WorkArea, bool IsPrimary, double Scale)
{
    public string Label => $"{DeviceName.TrimStart('\\', '.')} ({Bounds.Width}×{Bounds.Height}){(IsPrimary ? " — primary" : "")}";
}

public static class Monitors
{
    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var result = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(0, 0, (nint handle, nint _, ref Native.RECT _, nint _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(handle, ref info)) return true;
            var scale = Native.GetDpiForMonitor(handle, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
            result.Add(new MonitorInfo(
                handle,
                info.szDevice,
                ToRect(info.rcMonitor),
                ToRect(info.rcWork),
                (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                scale));
            return true;
        }, 0);
        return result;
    }

    private static Int32Rect ToRect(Native.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
