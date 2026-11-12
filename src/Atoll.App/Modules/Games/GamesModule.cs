using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules.Stats;
using Atoll.Settings;
using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Games;

public sealed partial class GameViewModel : ObservableObject
{
    public ObservableCollection<GameEntry> Library { get; } = [];
    [ObservableProperty] private bool _hasLibrary;

    // Session
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _gameName = "";
    [ObservableProperty] private string _gameDetail = "";
    [ObservableProperty] private string _playTime = "";
    [ObservableProperty] private string _stats = "";

    // Minecraft server
    [ObservableProperty] private bool _hasServer;
    [ObservableProperty] private string _serverAddress = "";
    [ObservableProperty] private bool _serverOnline;
    [ObservableProperty] private string _serverStatus = "";
    [ObservableProperty] private string _serverMotd = "";

    // Overlay extras: what else the player asked to see while playing. Empty when off or nothing is happening.
    [ObservableProperty] private string _music = "";
    [ObservableProperty] private string _claude = "";

    public bool HasStats => Stats.Length > 0;
    public bool HasMusic => Music.Length > 0;
    public bool HasClaude => Claude.Length > 0;
    partial void OnStatsChanged(string value) => OnPropertyChanged(nameof(HasStats));
    partial void OnMusicChanged(string value) => OnPropertyChanged(nameof(HasMusic));
    partial void OnClaudeChanged(string value) => OnPropertyChanged(nameof(HasClaude));

    [RelayCommand]
    private void Launch(GameEntry? game)
    {
        if (game is null) return;
        try { Process.Start(new ProcessStartInfo(game.Launch) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error($"Could not start {game.Name}", ex); }
    }
}

/// <summary>
/// Game mode: notices when a game runs, shows it with play time (and CPU, GPU and GPU temperature while it is in
/// front, as an overlay), keeps notifications quiet, lists installed games and watches one Minecraft server.
/// </summary>
public sealed class GamesModule : IIslandModule
{
    private readonly GameViewModel _vm = new();
    private readonly CpuSampler _cpu = new();
    private GpuSampler? _gpu;
    private ModuleContext? _context;
    private DispatcherTimer? _timer;
    private readonly CancellationTokenSource _stop = new();
    private uint _pid;
    private DateTimeOffset _started;
    private string? _postedAs;
    private DateTimeOffset _nextServerCheck;
    private IReadOnlyList<GameEntry> _library = [];
    private string _libraryKey = "";

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        context.Settings.Changed += OnSettingsChanged;
        OnSettingsChanged();
        Tick();
    }

    private void OnSettingsChanged()
    {
        if (_context is null) return;
        var games = _context.Settings.Current.Games;
        var address = games.MinecraftServer.Trim();
        if (address != _vm.ServerAddress)
        {
            _vm.ServerAddress = address;
            _vm.ServerMotd = "";
            _nextServerCheck = DateTimeOffset.MinValue;
        }
        _vm.HasServer = games.ShowServer && address.Length > 0;
        if (_vm.HasServer && _vm.ServerStatus.Length == 0) _vm.ServerStatus = "Checking…";
        if (!_vm.HasServer) _vm.ServerStatus = "";

        var libraryKey = $"{games.ScanSteam}{games.ScanEpic}{games.ScanMinecraft}";
        if (libraryKey != _libraryKey)
        {
            _libraryKey = libraryKey;
            _ = ScanLibraryAsync();
        }

        if (!games.DetectGames && _pid != 0) EndSession();
    }

    private async Task ScanLibraryAsync()
    {
        if (_context is null) return;
        var games = _context.Settings.Current.Games;
        var steam = games.ScanSteam;
        var epic = games.ScanEpic;
        var minecraft = games.ScanMinecraft;
        var found = await Task.Run(() => GameLibrary.Scan()
            .Where(game => game.Source switch
            {
                "Steam" => steam,
                "Epic" => epic,
                "Minecraft" => minecraft,
                _ => true,
            })
            .ToList());
        if (_context is null) return;
        _library = found;
        _vm.Library.Clear();
        foreach (var game in found) _vm.Library.Add(game);
        _vm.HasLibrary = _vm.Library.Count > 0;
    }

    private void Tick()
    {
        if (_context is null) return;
        var foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out var fgPid);

        // A game stays the session until its process ends, even while you look at something else.
        if (_pid != 0 && !IsRunning(_pid)) EndSession();
        var games = _context.Settings.Current.Games;
        if (games.DetectGames && _pid == 0 && fgPid != 0 && IsGame(fgPid, foreground)) StartSession(fgPid);

        if (_pid != 0)
        {
            var inFront = fgPid == _pid;
            _vm.PlayTime = games.ShowPlayTime ? Format(DateTimeOffset.Now - _started) : "";
            if (_vm.GameName.StartsWith("Minecraft", StringComparison.Ordinal)) _vm.GameDetail = MinecraftDetail(foreground, inFront) ?? _vm.GameDetail;
            var overlay = inFront && games.Mode == GameIslandMode.Overlay;
            _vm.Stats = overlay && games.ShowPerformance ? SampleStats() : "";
            if (!overlay || !games.ShowPerformance) { _gpu?.Dispose(); _gpu = null; }
            var live = _context.Activities.Snapshot(8);
            var running = live.Primary is null ? live.Sides : [live.Primary, .. live.Sides];
            _vm.Music = overlay && games.OverlayShowMusic ? OverlayExtras.Music(running) : "";
            _vm.Claude = overlay && games.OverlayShowClaude ? OverlayExtras.Claude(running) : "";
            Post(overlay ? "overlay" : "session");
        }

        if (_vm.HasServer && DateTimeOffset.Now >= _nextServerCheck) _ = CheckServerAsync();
    }

    private static bool IsGame(uint pid, nint hwnd) => GameDetector.IsGame(pid) || GameDetector.IsMinecraft(pid, hwnd);

    private void StartSession(uint pid)
    {
        _pid = pid;
        _started = DateTimeOffset.Now;
        _vm.GameName = NameFor(pid);
        _vm.GameDetail = "";
        _vm.IsPlaying = true;
        GameState.Playing = true;
        Log.Info($"Game started: {_vm.GameName}");
        if (_context?.Settings.Current.Games.AnnounceGameStart == true && !FocusService.IsQuietNow)
            _context.Post("game.start", new IndicatorViewModel("\uE7FC", _vm.GameName, "Game started"),
                ActivityPriority.Ambient, TimeSpan.FromSeconds(5));
    }

    private void EndSession()
    {
        Log.Info($"Game ended: {_vm.GameName} after {_vm.PlayTime}");
        _pid = 0;
        _vm.IsPlaying = false;
        _vm.Stats = "";
        GameState.Playing = false;
        _gpu?.Dispose(); _gpu = null;
        _context?.Remove("session");
        _postedAs = null;
    }

    private void Post(string kind)
    {
        if (_context is null || kind == _postedAs) return;
        // In front with the overlay on: the game is what the island shows. Otherwise it waits beside other things.
        _context.Post("session", _vm, kind == "overlay" ? ActivityPriority.High : ActivityPriority.Ambient);
        _postedAs = kind;
    }

    private string NameFor(uint pid)
    {
        var path = ProcessPath(pid);
        if (path is not null && _library.FirstOrDefault(g => g.InstallDir is { } dir && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) is { } game)
            return game.Name;
        if (ProcessName(pid) is "javaw" or "java" or "Minecraft.Windows") return "Minecraft";
        try
        {
            if (path is not null && FileVersionInfo.GetVersionInfo(path) is { } info)
                return info.ProductName is { Length: > 0 } product ? product.Trim() : info.FileDescription is { Length: > 0 } d ? d.Trim() : Path.GetFileNameWithoutExtension(path);
        }
        catch (FileNotFoundException) { }
        return ProcessName(pid) ?? "Game";
    }

    /// <summary>"1.21.4 · Multiplayer · play.example.net" from the window title and the launcher log.</summary>
    internal static string? MinecraftDetail(nint hwnd, bool inFront)
    {
        if (!inFront) return null;
        var title = new StringBuilder(256);
        Native.GetWindowText(hwnd, title, title.Capacity);
        var parts = title.ToString().Split(" - ", 2);
        if (parts.Length < 2 || !parts[0].StartsWith("Minecraft", StringComparison.Ordinal)) return null;
        var version = parts[0].Replace("Minecraft*", "").Replace("Minecraft", "").Trim();
        var mode = parts[1].StartsWith("Multiplayer", StringComparison.Ordinal) ? "Multiplayer" : parts[1];
        var server = mode == "Multiplayer" ? LastServer() : null;
        return string.Join(" · ", new[] { version, mode, server }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>The server from the newest "Connecting to host, port" line in the game's log.</summary>
    internal static string? LastServer(string? log = null)
    {
        log ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "logs", "latest.log");
        try
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? server = null;
            while (reader.ReadLine() is { } line)
            {
                var at = line.IndexOf("Connecting to ", StringComparison.Ordinal);
                if (at >= 0) server = line[(at + 14)..].Split(',')[0].Trim();
            }
            return server;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private string SampleStats()
    {
        var cpu = _cpu.Sample();
        var gpu = (_gpu ??= new GpuSampler()).Sample();
        var temp = GpuTemperature.Read();
        var parts = new List<string>();
        if (cpu is { } c) parts.Add($"CPU {c:P0}");
        if (gpu is { } g) parts.Add($"GPU {g:P0}");
        if (temp is { } t) parts.Add($"{t}°C");
        return string.Join("  ", parts);
    }

    private async Task CheckServerAsync()
    {
        var seconds = Math.Clamp(_context?.Settings.Current.Games.ServerRefreshSeconds ?? 60, 15, 3600);
        _nextServerCheck = DateTimeOffset.Now + TimeSpan.FromSeconds(seconds);
        if (_context?.Settings.Current.Games.ShowServer != true) return;
        var address = _vm.ServerAddress;
        if (address.Length == 0) return;
        try
        {
            var status = await MinecraftServer.QueryAsync(address, _stop.Token);
            if (address != _vm.ServerAddress) return;
            _vm.ServerOnline = true;
            _vm.ServerStatus = $"{status.Players}/{status.MaxPlayers} online · {status.PingMs} ms" + (status.Version.Length > 0 ? $" · {status.Version}" : "");
            _vm.ServerMotd = status.Motd;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        {
            if (_stop.IsCancellationRequested || address != _vm.ServerAddress) return;
            _vm.ServerOnline = false;
            _vm.ServerStatus = "Offline or unreachable";
            _vm.ServerMotd = "";
        }
    }

    internal static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{(int)t.TotalMinutes}m";

    private static bool IsRunning(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return !p.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static string? ProcessName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    private static string? ProcessPath(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.MainModule?.FileName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _timer?.Stop();
        if (_context is not null) _context.Settings.Changed -= OnSettingsChanged;
        if (_pid != 0) GameState.Playing = false;
        _gpu?.Dispose();
    }
}

/// <summary>NVIDIA GPU temperature through NVML, which ships with the driver. Null on other GPUs.</summary>
internal static class GpuTemperature
{
    private static nint _device;
    private static bool _unavailable;

    public static int? Read()
    {
        if (_unavailable) return null;
        try
        {
            if (_device == 0 && (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0)) { _unavailable = true; return null; }
            return nvmlDeviceGetTemperature(_device, 0, out var celsius) == 0 ? (int)celsius : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _unavailable = true; return null; }
    }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(nint device, int sensor, out uint temperature);
}
