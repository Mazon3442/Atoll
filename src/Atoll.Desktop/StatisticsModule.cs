using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Atoll.Modules;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Desktop;

public sealed partial class StatisticsModule : ObservableObject, IIslandModule
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _busy;
    private bool _disposed;
    private (ulong Idle, ulong Total)? _lastCpu;
    private long _lastBytes;
    private DateTime _lastSample = DateTime.UtcNow;
    [ObservableProperty] private string _cpu = "Sampling…";
    [ObservableProperty] private string _memory = "Sampling…";
    [ObservableProperty] private string _network = "Sampling…";
    [ObservableProperty] private string _status = "";
    public object Panel => this;
    public void Start(ModuleContext context) { _timer.Tick += Tick; _timer.Start(); Tick(null, EventArgs.Empty); }
    private async void Tick(object? sender, EventArgs e)
    {
        if (_busy || _disposed) return; _busy = true;
        try
        {
            (ulong Idle, ulong Total) cpu = default;
            if (OperatingSystem.IsWindows())
            {
                if (GetSystemTimes(out var idle, out var kernel, out var user)) cpu = (idle, kernel + user);
                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref memory)) Memory = $"{(memory.TotalPhysical - memory.AvailablePhysical) / 1073741824.0:0.0} / {memory.TotalPhysical / 1073741824.0:0.0} GB";
            }
            else if (OperatingSystem.IsLinux())
            {
                var fields = (await File.ReadAllLinesAsync("/proc/stat"))[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(v => ulong.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                cpu = (fields[3] + fields[4], fields.Take(8).Aggregate(0UL, (sum, n) => sum + n));
                var mem = (await File.ReadAllLinesAsync("/proc/meminfo")).Select(l => l.Split(':')).ToDictionary(p => p[0], p => ulong.Parse(p[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture));
                Memory = $"{(mem["MemTotal"] - mem["MemAvailable"]) / 1048576.0:0.0} / {mem["MemTotal"] / 1048576.0:0.0} GB";
            }
            else
            {
                var total = double.Parse(await Commands.RunAsync("sysctl", "-n", "hw.memsize"), CultureInfo.InvariantCulture);
                var vm = await Commands.RunAsync("vm_stat");
                var page = System.Text.RegularExpressions.Regex.Match(vm, @"page size of (\d+) bytes");
                var free = System.Text.RegularExpressions.Regex.Matches(vm, @"Pages (?:free|inactive):\s+(\d+)").Sum(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                Memory = $"{(total - free * double.Parse(page.Groups[1].Value, CultureInfo.InvariantCulture)) / 1073741824:0.0} / {total / 1073741824:0.0} GB";
                var load = await Commands.RunAsync("ps", "-A", "-o", "%cpu=");
                Cpu = $"{Math.Clamp(load.Split('\n').Sum(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0) / Environment.ProcessorCount, 0, 100):0}%";
            }
            if (cpu.Total > 0)
            {
                if (_lastCpu is { } last && cpu.Total > last.Total) Cpu = $"{Math.Clamp(100.0 * (1 - (double)(cpu.Idle - last.Idle) / (cpu.Total - last.Total)), 0, 100):0}%";
                _lastCpu = cpu;
            }
            var bytes = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).Sum(n => n.GetIPStatistics().BytesReceived + n.GetIPStatistics().BytesSent);
            var now = DateTime.UtcNow;
            if (_lastBytes > 0) Network = $"{Math.Max(0, bytes - _lastBytes) / Math.Max(.1, (now - _lastSample).TotalSeconds) / 1024:0} KB/s";
            _lastBytes = bytes; _lastSample = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.ComponentModel.Win32Exception or NetworkInformationException) { Status = "Some system readings are unavailable on this device."; Log.Error("Statistics sample failed", ex); }
        finally { _busy = false; }
    }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    public void Dispose() { _disposed = true; _timer.Stop(); _timer.Tick -= Tick; }
}
