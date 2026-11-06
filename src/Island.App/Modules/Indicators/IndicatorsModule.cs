using System.Management;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Island.Activities;
using Island.Shell;
using NAudio.CoreAudioApi;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.System.Power;

namespace Island.Modules.Indicators;

/// <summary>Expanded tab: volume control and current status.</summary>
public sealed partial class SystemPanelViewModel(Action<double> setVolume, Action toggleMute) : ObservableObject
{
    private bool _applyingFromDevice;

    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private string _volumeGlyph = "";
    [ObservableProperty] private string _battery = "";
    [ObservableProperty] private string _network = "";
    [ObservableProperty] private string _bluetooth = "None connected";

    public bool HasBattery => Battery.Length > 0;

    partial void OnBatteryChanged(string value) => OnPropertyChanged(nameof(HasBattery));

    partial void OnVolumeChanged(double value)
    {
        if (!_applyingFromDevice) setVolume(value / 100);
    }

    /// <summary>Reflects a change made elsewhere without writing it back to the device.</summary>
    public void ShowDeviceVolume(double level, bool muted)
    {
        _applyingFromDevice = true;
        Volume = Math.Round(level * 100);
        _applyingFromDevice = false;
        IsMuted = muted;
        VolumeGlyph = IndicatorsModule.VolumeGlyph(level, muted);
    }

    [RelayCommand]
    private void ToggleMute() => toggleMute();
}

/// <summary>
/// Short popups for volume, brightness, battery, network, Bluetooth and lock keys. All sources are
/// marshalled to the UI thread before touching state.
/// </summary>
public sealed class IndicatorsModule : IIslandModule
{
    private static readonly TimeSpan ShortShow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongShow = TimeSpan.FromSeconds(4);

    private readonly SystemPanelViewModel _panel;
    private readonly Dictionary<string, string> _bluetoothNames = [];
    private ModuleContext? _context;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _deviceNotifications;
    private MMDevice? _device;
    private ManagementEventWatcher? _brightness;
    private DeviceWatcher? _bluetooth;
    private bool _bluetoothReady;
    private DispatcherTimer? _keyPoll;
    private bool _caps;
    private bool _num;
    private int _lastBatteryAlert = 100;

    public IndicatorsModule() => _panel = new SystemPanelViewModel(SetVolume, ToggleMute);

    public object? Panel => _panel;

    public void Start(ModuleContext context)
    {
        _context = context;
        StartVolume();
        StartBattery();
        StartNetwork();
        StartBrightness();
        StartBluetooth();
        StartLockKeys();
    }

    // ---------- Volume ----------

    private void StartVolume()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _deviceNotifications = _enumerator.CreateNotificationClient(); // events arrive on the UI thread
            _deviceNotifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) BindDefaultDevice(announce: true);
            };
            BindDefaultDevice(announce: false);
        }
        catch (Exception ex)
        {
            Log.Error("Volume monitoring unavailable", ex);
        }
    }

    private void BindDefaultDevice(bool announce)
    {
        if (_device is not null)
        {
            _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            _device.Dispose();
            _device = null;
        }
        try
        {
            _device = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            Log.Info($"No default audio output: {ex.Message}");
            return;
        }
        var vol = _device.AudioEndpointVolume;
        vol.OnVolumeNotification += OnVolumeNotification;
        _panel.ShowDeviceVolume(vol.MasterVolumeLevelScalar, vol.Mute);
        if (announce) Show(new IndicatorViewModel("", "Audio output", _device.FriendlyName), LongShow);
    }

    // Raised on a Core Audio thread.
    private void OnVolumeNotification(AudioVolumeNotificationData data) => OnUi(() =>
    {
        _panel.ShowDeviceVolume(data.MasterVolume, data.Muted);
        var title = data.Muted ? "Muted" : $"{Math.Round(data.MasterVolume * 100)}%";
        Show(new IndicatorViewModel(VolumeGlyph(data.MasterVolume, data.Muted), title, "Volume", data.Muted ? 0 : data.MasterVolume), ShortShow);
    });

    private void SetVolume(double level)
    {
        try
        {
            if (_device is not null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(level, 0, 1);
        }
        catch (Exception ex) { Log.Error("Setting volume failed", ex); }
    }

    private void ToggleMute()
    {
        try
        {
            if (_device is not null) _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
        }
        catch (Exception ex) { Log.Error("Toggling mute failed", ex); }
    }

    internal static string VolumeGlyph(double level, bool muted) => muted || level <= 0
        ? ""
        : level < 0.34 ? "" : level < 0.67 ? "" : "";

    // ---------- Battery ----------

    private void StartBattery()
    {
        if (PowerManager.BatteryStatus == BatteryStatus.NotPresent) return; // desktop PC
        PowerManager.PowerSupplyStatusChanged += OnPowerSupplyChanged;
        PowerManager.RemainingChargePercentChanged += OnChargeChanged;
        ShowBattery(announce: false);
    }

    private void OnPowerSupplyChanged(object? sender, object e) => OnUi(() => ShowBattery(announce: true));

    private void OnChargeChanged(object? sender, object e) => OnUi(() => ShowBattery(announce: false));

    private void ShowBattery(bool announce)
    {
        var pct = PowerManager.RemainingChargePercent;
        var plugged = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        var glyph = char.ConvertFromUtf32((plugged ? 0xEBAB : 0xEBA0) + Math.Clamp(pct / 10, 0, 10));
        _panel.Battery = $"{pct}% {(plugged ? "· plugged in" : "· on battery")}";

        if (announce)
        {
            Show(new IndicatorViewModel(glyph, plugged ? "Charging" : "On battery", $"{pct}%", pct / 100.0), LongShow);
            if (plugged) _lastBatteryAlert = 100;
            return;
        }
        // Warn once when dropping through 20% and again through 10%.
        var threshold = pct <= 10 ? 10 : pct <= 20 ? 20 : 100;
        if (!plugged && threshold < _lastBatteryAlert)
        {
            _lastBatteryAlert = threshold;
            _context?.Post("battery-low", new IndicatorViewModel(glyph, "Battery low", $"{pct}% remaining", pct / 100.0),
                ActivityPriority.Critical, TimeSpan.FromSeconds(6), alert: true);
        }
    }

    // ---------- Network ----------

    private void StartNetwork()
    {
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _panel.Network = NetworkInterface.GetIsNetworkAvailable() ? "Connected" : "Offline";
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => OnUi(() =>
    {
        _panel.Network = e.IsAvailable ? "Connected" : "Offline";
        Show(e.IsAvailable
            ? new IndicatorViewModel("", "Connected", "Network available")
            : new IndicatorViewModel("", "Offline", "No network connection"), LongShow);
    });

    // ---------- Brightness (laptop panels only; desktops have no WMI brightness events) ----------

    private void StartBrightness()
    {
        try
        {
            _brightness = new ManagementEventWatcher(
                new ManagementScope(@"root\WMI"),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _brightness.EventArrived += (_, e) =>
            {
                var level = Convert.ToDouble(e.NewEvent.Properties["Brightness"].Value) / 100;
                OnUi(() => Show(new IndicatorViewModel("", $"{Math.Round(level * 100)}%", "Brightness", level), ShortShow));
            };
            _brightness.Start();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _brightness?.Dispose();
            _brightness = null; // not supported on this PC
        }
    }

    // ---------- Bluetooth ----------

    private void StartBluetooth()
    {
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            _bluetooth = DeviceInformation.CreateWatcher(selector);
            _bluetooth.Added += (_, info) => OnUi(() =>
            {
                _bluetoothNames[info.Id] = info.Name;
                UpdateBluetoothPanel();
                if (_bluetoothReady) Show(new IndicatorViewModel("", "Connected", info.Name), LongShow);
            });
            _bluetooth.Removed += (_, update) => OnUi(() =>
            {
                if (!_bluetoothNames.Remove(update.Id, out var name)) return;
                UpdateBluetoothPanel();
                if (_bluetoothReady) Show(new IndicatorViewModel("", "Disconnected", name), LongShow);
            });
            // Devices already connected at startup arrive first; only announce changes after that.
            _bluetooth.EnumerationCompleted += (_, _) => OnUi(() => _bluetoothReady = true);
            _bluetooth.Start();
        }
        catch (Exception ex)
        {
            Log.Info($"Bluetooth monitoring unavailable: {ex.Message}");
        }
    }

    private void UpdateBluetoothPanel() =>
        _panel.Bluetooth = _bluetoothNames.Count == 0 ? "None connected" : string.Join(", ", _bluetoothNames.Values);

    // ---------- Caps Lock / Num Lock ----------

    private void StartLockKeys()
    {
        _caps = IsToggled(Native.VK_CAPITAL);
        _num = IsToggled(Native.VK_NUMLOCK);
        _keyPoll = new DispatcherTimer(DispatcherPriority.Background, _context!.Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _keyPoll.Tick += (_, _) =>
        {
            var caps = IsToggled(Native.VK_CAPITAL);
            if (caps != _caps)
            {
                _caps = caps;
                Show(new IndicatorViewModel("", caps ? "Caps Lock on" : "Caps Lock off"), TimeSpan.FromSeconds(1.5));
            }
            var num = IsToggled(Native.VK_NUMLOCK);
            if (num != _num)
            {
                _num = num;
                Show(new IndicatorViewModel("", num ? "Num Lock on" : "Num Lock off"), TimeSpan.FromSeconds(1.5));
            }
        };
        _keyPoll.Start();
    }

    private static bool IsToggled(int key) => (Native.GetKeyState(key) & 1) != 0;

    // ---------- shared ----------

    private void Show(IndicatorViewModel vm, TimeSpan duration) =>
        _context?.Post("indicator", vm, ActivityPriority.High, duration);

    private void OnUi(Action action) => _context?.Dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        _keyPoll?.Stop();
        PowerManager.PowerSupplyStatusChanged -= OnPowerSupplyChanged;
        PowerManager.RemainingChargePercentChanged -= OnChargeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        if (_bluetooth is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
            _bluetooth.Stop();
        _brightness?.Stop();
        _brightness?.Dispose();
        if (_device is not null)
        {
            _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            _device.Dispose();
        }
        _deviceNotifications?.Dispose();
        _enumerator?.Dispose();
    }
}
