using System.Diagnostics;
using System.Runtime.InteropServices;
using Atoll.Activities;
using Atoll.Modules.Controls;
using System.Windows.Threading;
using Windows.Devices.Radios;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;

namespace Atoll.Modules.Actions;

public sealed partial class QuickActionsViewModel(QuickActionsModule module) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicGlyph), nameof(MicLabel))]
    private bool _micMuted;

    public string MicGlyph => MicMuted ? "" : "";
    public string MicLabel => MicMuted ? "Unmute mic" : "Mute mic";

    /// <summary>Wi-Fi and Bluetooth: null while unknown or when the PC has no such radio.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WifiState), nameof(WifiAvailable))] private bool? _wifiOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BluetoothState), nameof(BluetoothAvailable))] private bool? _bluetoothOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ThemeState))] private bool _darkTheme;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SleepLabel), nameof(RestartLabel), nameof(ShutDownLabel))] private string? _armed;

    public bool WifiAvailable => WifiOn is not null;
    public bool BluetoothAvailable => BluetoothOn is not null;
    public string WifiState => WifiOn is null ? "Not available" : WifiOn.Value ? "On" : "Off";
    public string BluetoothState => BluetoothOn is null ? "Not available" : BluetoothOn.Value ? "On" : "Off";
    public string ThemeState => DarkTheme ? "On" : "Off";
    public string SleepLabel => Armed == "sleep" ? "Press again" : "Sleep";
    public string RestartLabel => Armed == "restart" ? "Press again" : "Restart";
    public string ShutDownLabel => Armed == "shutdown" ? "Press again" : "Shut down";

    [RelayCommand] private void RefreshControls() => module.RefreshControls();
    [RelayCommand] private Task ToggleWifi() => module.ToggleRadio(RadioKind.WiFi);
    [RelayCommand] private Task ToggleBluetooth() => module.ToggleRadio(RadioKind.Bluetooth);
    [RelayCommand] private void ToggleTheme() => module.ToggleTheme();
    [RelayCommand] private void Sleep() => module.Arm("sleep", PowerActions.Sleep);
    [RelayCommand] private void Restart() => module.Arm("restart", PowerActions.Restart);
    [RelayCommand] private void ShutDown() => module.Arm("shutdown", PowerActions.ShutDown);

    [RelayCommand] private void Screenshot() => module.Screenshot();
    [RelayCommand] private void ToggleMic() => module.ToggleMic();
    [RelayCommand] private void Lock() => module.Lock();
    [RelayCommand] private void ScreenOff() => module.ScreenOff();
    [RelayCommand] private void Downloads() => module.OpenDownloads();
}

/// <summary>One-click actions: screenshot, microphone mute, lock, display off, Downloads folder.</summary>
public sealed class QuickActionsModule : IIslandModule
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private static readonly nint HWND_BROADCAST = 0xFFFF;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out string path);

    private static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    private readonly QuickActionsViewModel _vm;
    private readonly ConfirmPress _confirm = new(TimeProvider.System);
    private readonly DispatcherTimer _disarm = new() { Interval = TimeSpan.FromSeconds(3) };
    private ModuleContext? _context;

    public QuickActionsModule()
    {
        _vm = new QuickActionsViewModel(this);
        _disarm.Tick += (_, _) =>
        {
            _disarm.Stop();
            _confirm.Reset();
            _vm.Armed = null;
        };
    }

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _vm.MicMuted = WithMic(v => v.Mute) ?? false;
    }

    internal void Screenshot()
    {
        if (Atoll.Navigation.ScreenTool is { } tool) tool("area");
        else Run("ms-screenclip:");
    }

    /// <summary>Reads the radios and the theme; the status card calls this each time it opens.</summary>
    internal async void RefreshControls()
    {
        _vm.DarkTheme = WindowsTheme.IsDark();
        _vm.WifiOn = await RadioControl.IsOnAsync(RadioKind.WiFi);
        _vm.BluetoothOn = await RadioControl.IsOnAsync(RadioKind.Bluetooth);
    }

    internal async Task ToggleRadio(RadioKind kind)
    {
        var now = await RadioControl.ToggleAsync(kind);
        if (now is null) { Indicate("", $"Could not switch {(kind == RadioKind.WiFi ? "Wi-Fi" : "Bluetooth")}"); return; }
        if (kind == RadioKind.WiFi) _vm.WifiOn = now; else _vm.BluetoothOn = now;
    }

    internal void ToggleTheme()
    {
        try { WindowsTheme.SetDark(!WindowsTheme.IsDark()); }
        catch (Exception ex) { Log.Error("Could not switch the Windows theme", ex); }
        _vm.DarkTheme = WindowsTheme.IsDark();
    }

    /// <summary>First press arms the action and the tile says so; a second press within 3 seconds runs it.</summary>
    internal void Arm(string key, Action run)
    {
        if (_confirm.Press(key))
        {
            _disarm.Stop();
            _vm.Armed = null;
            run();
            return;
        }
        _vm.Armed = key;
        _disarm.Stop();
        _disarm.Start();
    }

    /// <summary>Flips the default microphone's mute and returns the new state; null when there is no microphone.</summary>
    internal static bool? SwitchMic() => WithMic(v =>
    {
        v.Mute = !v.Mute;
        return v.Mute;
    });

    internal void ToggleMic()
    {
        var muted = SwitchMic();
        if (muted is not { } m)
        {
            Indicate("", "No microphone found");
            return;
        }
        _vm.MicMuted = m;
        Indicate(m ? "" : "", m ? "Microphone muted" : "Microphone on");
    }

    internal void Lock() => LockWorkStation();

    // Posted (not sent) so a hung window cannot block the island.
    internal void ScreenOff() => PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2);

    internal void OpenDownloads()
    {
        var path = SHGetKnownFolderPath(DownloadsFolder, 0, 0, out var p) == 0 ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Run(path);
    }

    /// <summary>Runs <paramref name="use"/> on the default communications microphone; null if there is none.</summary>
    private static bool? WithMic(Func<AudioEndpointVolume, bool> use)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var mic = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return use(mic.AudioEndpointVolume);
        }
        catch (Exception ex)
        {
            Log.Info($"Microphone unavailable: {ex.Message}");
            return null;
        }
    }

    private void Indicate(string glyph, string title) =>
        _context?.Post("action", new IndicatorViewModel(glyph, title), ActivityPriority.High, TimeSpan.FromSeconds(2));

    private static void Run(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open {target}", ex); }
    }

    public void Dispose() { }
}
