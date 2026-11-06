using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Modules.Stats;

/// <summary>One metric tile: current value text, 0–1 level and a 60-sample sparkline.</summary>
public sealed partial class StatViewModel(string name, string glyph) : ObservableObject
{
    public const int Samples = 60;
    public const double ChartWidth = 120;
    public const double ChartHeight = 28;

    private readonly Queue<double> _history = new();

    public string Name { get; } = name;
    public string Glyph { get; } = glyph;

    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _level;
    [ObservableProperty] private PointCollection _sparkline = [];

    /// <param name="level">0–1 for the bar and sparkline (network passes a self-scaled value).</param>
    public void Push(double level, string value, string detail = "")
    {
        level = double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
        Level = level;
        Value = value;
        Detail = detail;
        _history.Enqueue(level);
        while (_history.Count > Samples) _history.Dequeue();
        Sparkline = BuildSparkline(_history);
    }

    internal static PointCollection BuildSparkline(IReadOnlyCollection<double> values)
    {
        var points = new PointCollection(values.Count + 2);
        var step = ChartWidth / (Samples - 1);
        var x = ChartWidth - (values.Count - 1) * step; // newest sample at the right edge
        points.Add(new Point(x, ChartHeight));
        foreach (var v in values)
        {
            points.Add(new Point(x, ChartHeight - v * ChartHeight));
            x += step;
        }
        points.Add(new Point(ChartWidth, ChartHeight));
        points.Freeze();
        return points;
    }
}

public sealed class StatsViewModel
{
    public StatViewModel Cpu { get; } = new("CPU", "");
    public StatViewModel Gpu { get; } = new("GPU", "");
    public StatViewModel Memory { get; } = new("Memory", "");
    public StatViewModel Network { get; } = new("Network", "");
}

/// <summary>
/// CPU, GPU, memory and network once a second, read without admin rights: GetSystemTimes,
/// the "GPU Engine" performance counters, GlobalMemoryStatusEx and network interface byte counts.
/// Sampling runs on a worker thread; tiles update on the UI thread.
/// </summary>
public sealed class StatsModule : IIslandModule
{
    private readonly StatsViewModel _vm = new();
    private readonly CpuSampler _cpu = new();
    private readonly GpuSampler _gpu = new();
    private readonly NetworkSampler _net = new();
    private ModuleContext? _context;
    private Timer? _timer;
    private int _busy;

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void Sample()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var cpu = _cpu.Sample();
            var gpu = _gpu.Sample();
            var (usedGb, totalGb, memLoad) = MemorySampler.Sample();
            var (down, up) = _net.Sample();
            var netScale = _net.Scale(down + up);

            _context?.Dispatcher.BeginInvoke(() =>
            {
                if (cpu is { } c) _vm.Cpu.Push(c, $"{c:P0}");
                if (gpu is { } g) _vm.Gpu.Push(g, $"{g:P0}");
                else _vm.Gpu.Push(0, "—", "not available");
                _vm.Memory.Push(memLoad, $"{memLoad:P0}", $"{usedGb:0.0} of {totalGb:0} GB");
                _vm.Network.Push(netScale, $"↓ {Rate(down)}", $"↑ {Rate(up)}");
            });
        }
        catch (Exception ex)
        {
            Log.Error("Stats sample failed", ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    internal static string Rate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1e9 => $"{bytesPerSecond / 1e9:0.0} GB/s",
        >= 1e6 => $"{bytesPerSecond / 1e6:0.0} MB/s",
        >= 1e3 => $"{bytesPerSecond / 1e3:0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };

    public void Dispose()
    {
        _timer?.Dispose();
        _gpu.Dispose();
    }
}

internal sealed class CpuSampler
{
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private (long Idle, long Kernel, long User)? _last;

    public double? Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var now = (idle, kernel, user);
        var last = _last;
        _last = now;
        return last is { } l ? Usage(l, now) : null;
    }

    /// <summary>Busy share between two GetSystemTimes readings (kernel time includes idle time).</summary>
    internal static double Usage((long Idle, long Kernel, long User) a, (long Idle, long Kernel, long User) b)
    {
        var idle = b.Idle - a.Idle;
        var total = (b.Kernel - a.Kernel) + (b.User - a.User);
        return total <= 0 ? 0 : Math.Clamp(1 - (double)idle / total, 0, 1);
    }
}

internal static class MemorySampler
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    public static (double UsedGb, double TotalGb, double Load) Sample()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref s) || s.ullTotalPhys == 0) return (0, 0, 0);
        const double gb = 1024d * 1024 * 1024;
        var used = s.ullTotalPhys - s.ullAvailPhys;
        return (used / gb, s.ullTotalPhys / gb, (double)used / s.ullTotalPhys);
    }
}

/// <summary>3D engine utilization summed over all processes (what Task Manager shows as GPU "3D").</summary>
internal sealed class GpuSampler : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(nint query);

    // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer (8) + CStatus (4) + padding (4) + double (8).
    private const int ItemSize = 24;
    private const int ValueOffset = 16;
    private const int StatusOffset = 8;

    private nint _query;
    private nint _counter;
    private bool _ready;

    public GpuSampler()
    {
        if (PdhOpenQuery(null, 0, out _query) != 0) return;
        if (PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", 0, out _counter) != 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
            return;
        }
        PdhCollectQueryData(_query); // rates need two collections
    }

    public double? Sample()
    {
        if (_query == 0 || PdhCollectQueryData(_query) != 0) return null;
        uint size = 0;
        var status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out _, 0);
        if (status != PDH_MORE_DATA || size == 0) return _ready ? 0 : null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out var count, buffer) != 0) return null;
            double sum = 0;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * ItemSize;
                if (Marshal.ReadInt32(item + StatusOffset) != 0) continue; // invalid instance (process just exited)
                sum += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + ValueOffset));
            }
            _ready = true;
            return Math.Clamp(sum / 100, 0, 1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
    }
}

internal sealed class NetworkSampler
{
    private (long Rx, long Tx, DateTime At)? _last;
    private double _peak = 1_000_000; // bytes/s that fills the sparkline; grows with real traffic

    public (double Down, double Up) Sample()
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var s = nic.GetIPStatistics();
            rx += s.BytesReceived;
            tx += s.BytesSent;
        }
        var now = DateTime.UtcNow;
        var last = _last;
        _last = (rx, tx, now);
        if (last is not { } l) return (0, 0);
        var seconds = Math.Max(0.1, (now - l.At).TotalSeconds);
        return (Math.Max(0, rx - l.Rx) / seconds, Math.Max(0, tx - l.Tx) / seconds);
    }

    /// <summary>0–1 against the busiest rate seen recently (slowly decays so the chart stays readable).</summary>
    public double Scale(double total)
    {
        _peak = Math.Max(1_000_000, Math.Max(total, _peak * 0.995));
        return total / _peak;
    }
}
