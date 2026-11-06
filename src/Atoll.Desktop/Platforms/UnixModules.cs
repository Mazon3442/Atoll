using Atoll.Activities;
using Atoll.Desktop;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using System.Net.NetworkInformation;

namespace Atoll.Modules.Media
{
    public sealed partial class MediaModule : IIslandModule
    {
        private readonly MediaViewModel _vm;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
        private ModuleContext? _context;
        private bool _busy;
        private bool _disposed;
        public MediaModule() => _vm = new MediaViewModel(() => Control("play-pause"), () => Control("next"), () => Control("previous"), Seek);
        public object Panel => _vm;
        public void Start(ModuleContext context) { _context = context; _timer.Tick += OnTick; _timer.Start(); OnTick(null, EventArgs.Empty); }
        private static string MacPlayerScript(string action) =>
            "if application \"Spotify\" is running then\n tell application \"Spotify\"\n" + action + "\n end tell\nelse if application \"Music\" is running then\n tell application \"Music\"\n" + action + "\n end tell\nend if";
        private async void OnTick(object? sender, EventArgs e)
        {
            if (_busy || _disposed) return; _busy = true;
            try
            {
                var raw = OperatingSystem.IsMacOS()
                    ? await Commands.RunAsync("osascript", "-e", MacPlayerScript("if player state is stopped then return \"\"\nreturn (name of current track) & linefeed & (artist of current track) & linefeed & (player state as text)"))
                    : await Commands.RunAsync("playerctl", "metadata", "--format", "{{title}}\n{{artist}}\n{{status}}");
                if (_disposed) return;
                var parts = raw.Split('\n');
                _vm.HasSession = parts.Length >= 3;
                _vm.Title = parts.ElementAtOrDefault(0) ?? ""; _vm.Artist = parts.ElementAtOrDefault(1) ?? "";
                _vm.IsPlaying = parts.ElementAtOrDefault(2)?.Equals("playing", StringComparison.OrdinalIgnoreCase) == true;
                _vm.Status = _vm.HasSession ? "" : "Play something in a media app to see it here.";
                if (_vm.HasSession && _vm.IsPlaying) _context?.Post("now", _vm, ActivityPriority.Normal);
                else _context?.Remove("now");
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            {
                _vm.HasSession = false; _context?.Remove("now");
                _vm.Status = OperatingSystem.IsMacOS() ? "Open Music or Spotify and allow ATOLL in System Settings > Privacy & Security > Automation."
                    : "No MPRIS player is active. Media controls require playerctl and a player that exposes MPRIS.";
            }
            finally { _busy = false; }
        }
        private async Task Control(string action)
        {
            try
            {
                if (OperatingSystem.IsMacOS()) await Commands.RunAsync("osascript", "-e", MacPlayerScript(action switch { "play-pause" => "playpause", "next" => "next track", _ => "previous track" }));
                else await Commands.RunAsync("playerctl", action);
                OnTick(null, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { _vm.Status = "The media player could not complete that command."; }
        }
        private Task Seek(double value) => Task.CompletedTask;
        public void Dispose() { _disposed = true; _timer.Stop(); _timer.Tick -= OnTick; }
    }
}

namespace Atoll.Modules.Indicators
{
    public sealed partial class SystemPanelViewModel : ObservableObject
    {
        private readonly Action<double> _setVolume;
        private readonly Action _toggleMute;
        private bool _reading;
        public SystemPanelViewModel(Action<double> setVolume, Action toggleMute) { _setVolume = setVolume; _toggleMute = toggleMute; }
        [ObservableProperty] private double _volume;
        [ObservableProperty] private bool _isMuted;
        [ObservableProperty] private string _battery = "";
        [ObservableProperty] private string _network = "";
        [ObservableProperty] private string _bluetooth = "Use your desktop's Bluetooth controls.";
        public bool HasBattery => Battery.Length > 0;
        partial void OnVolumeChanged(double value) { if (!_reading) _setVolume(value / 100); }
        public void ShowDeviceVolume(double volume, bool muted) { _reading = true; Volume = volume * 100; IsMuted = muted; _reading = false; }
        [RelayCommand] private void ToggleMute() => _toggleMute();
    }

    public sealed class IndicatorsModule : IIslandModule
    {
        private readonly SystemPanelViewModel _vm;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
        private ModuleContext? _context;
        private bool _busy;
        private bool _disposed;
        public IndicatorsModule() => _vm = new SystemPanelViewModel(v => _ = SetVolume(v), () => _ = ToggleMute());
        public object Panel => _vm;
        public void Start(ModuleContext context) { _context = context; _timer.Tick += OnTick; _timer.Start(); OnTick(null, EventArgs.Empty); }
        private async void OnTick(object? sender, EventArgs e)
        {
            if (_busy || _disposed) return; _busy = true;
            try
            {
                _vm.Network = NetworkInterface.GetIsNetworkAvailable() ? "Connected" : "Offline";
                if (OperatingSystem.IsMacOS())
                {
                    var raw = await Commands.RunAsync("osascript", "-e", "get volume settings");
                    var volume = System.Text.RegularExpressions.Regex.Match(raw, @"output volume:(\d+)");
                    if (volume.Success) _vm.ShowDeviceVolume(double.Parse(volume.Groups[1].Value, CultureInfo.InvariantCulture) / 100, raw.Contains("output muted:true", StringComparison.Ordinal));
                    _vm.Battery = await Commands.RunAsync("pmset", "-g", "batt");
                }
                else
                {
                    var volume = await Commands.RunAsync("pactl", "get-sink-volume", "@DEFAULT_SINK@");
                    var number = System.Text.RegularExpressions.Regex.Match(volume, @"(\d+)%");
                    var mute = await Commands.RunAsync("pactl", "get-sink-mute", "@DEFAULT_SINK@");
                    if (number.Success) _vm.ShowDeviceVolume(double.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture) / 100, mute.EndsWith("yes", StringComparison.Ordinal));
                    var battery = Directory.Exists("/sys/class/power_supply") ? Directory.EnumerateDirectories("/sys/class/power_supply").FirstOrDefault(p => File.Exists(Path.Combine(p, "capacity"))) : null;
                    if (battery is not null) _vm.Battery = (await File.ReadAllTextAsync(Path.Combine(battery, "capacity"))).Trim() + "% battery";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { _vm.Bluetooth = "Some device controls are unavailable. On Linux, install pulseaudio-utils (Ubuntu) or libpulse (Arch)."; }
            finally { _busy = false; }
        }
        private async Task SetVolume(double value)
        {
            try
            {
                var percent = ((int)(Math.Clamp(value, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture);
                if (OperatingSystem.IsMacOS()) await Commands.RunAsync("osascript", "-e", "set volume output volume " + percent);
                else await Commands.RunAsync("pactl", "set-sink-volume", "@DEFAULT_SINK@", percent + "%");
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { _context?.Post("error", new IndicatorViewModel("", "Volume control is unavailable"), ActivityPriority.High, TimeSpan.FromSeconds(4)); }
        }
        private async Task ToggleMute()
        {
            try
            {
                if (OperatingSystem.IsMacOS()) await Commands.RunAsync("osascript", "-e", "set volume output muted not (output muted of (get volume settings))");
                else await Commands.RunAsync("pactl", "set-sink-mute", "@DEFAULT_SINK@", "toggle");
                OnTick(null, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { Log.Error("Mute failed", ex); }
        }
        public void Dispose() { _disposed = true; _timer.Stop(); _timer.Tick -= OnTick; }
    }
}

namespace Atoll.Modules.Actions
{
    public sealed partial class QuickActionsViewModel(QuickActionsModule module) : ObservableObject
    {
        public string MicLabel => "Toggle microphone mute";
        [RelayCommand] private void Screenshot() => module.Run("screenshot");
        [RelayCommand] private void ToggleMic() => module.Run("microphone");
        [RelayCommand] private void Lock() => module.Run("lock");
        [RelayCommand] private void ScreenOff() => module.Run("screen");
        [RelayCommand] private void Downloads() => Commands.Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
    }
    public sealed class QuickActionsModule : IIslandModule
    {
        private ModuleContext? _context;
        public object Panel { get; }
        public QuickActionsModule() => Panel = new QuickActionsViewModel(this);
        public void Start(ModuleContext context) => _context = context;
        internal async void Run(string action)
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    if (action == "screenshot") await Commands.RunAsync("screencapture", "-i", "-c");
                    else if (action == "lock") await Commands.RunAsync("/System/Library/CoreServices/Menu Extras/User.menu/Contents/Resources/CGSession", "-suspend");
                    else if (action == "screen") await Commands.RunAsync("pmset", "displaysleepnow");
                    else await Commands.RunAsync("osascript", "-e", "if input volume of (get volume settings) is 0 then\nset volume input volume 100\nelse\nset volume input volume 0\nend if");
                }
                else
                {
                    if (action == "screenshot") await Commands.RunAsync("gnome-screenshot", "-a", "-c");
                    else if (action == "lock") await Commands.RunAsync("loginctl", "lock-session");
                    else if (action == "screen") await Commands.RunAsync("xset", "dpms", "force", "off");
                    else await Commands.RunAsync("pactl", "set-source-mute", "@DEFAULT_SOURCE@", "toggle");
                }
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            { _context?.Post("error", new IndicatorViewModel("", "Action unavailable", "Your desktop may need its native screenshot or display tool."), ActivityPriority.High, TimeSpan.FromSeconds(5), true); }
        }
        public void Dispose() { }
    }
}
