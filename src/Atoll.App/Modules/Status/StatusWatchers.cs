using System.Net.NetworkInformation;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules.Games;
using Atoll.Modules.Indicators;
using Atoll.Settings;

namespace Atoll.Modules.Status;

/// <summary>
/// A quiet pill while a VPN is connected. It finds VPN adapters locally (no network request); showing the country
/// and address needs one request to ipwho.is, which is off until Settings turns it on.
/// </summary>
internal sealed class VpnWatcher : IDisposable
{
    private const string Key = "vpn";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly ActivityHost _activities;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
    private string _connected = "";
    private string _place = "";

    public VpnWatcher(ActivityHost activities, SettingsService settings, Dispatcher dispatcher)
    {
        _activities = activities;
        _settings = settings;
        _dispatcher = dispatcher;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        settings.Changed += OnSettingsChanged;
        Refresh();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
    private void OnSettingsChanged() => _dispatcher.BeginInvoke(Refresh);

    /// <summary>The names of VPN adapters that are up, or empty.</summary>
    internal static IReadOnlyList<string> Connected() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(nic => VpnAdapters.IsVpn(nic.Name, nic.Description, nic.NetworkInterfaceType == NetworkInterfaceType.Ppp))
            .Select(nic => nic.Name)
            .ToList();

    private void Refresh()
    {
        var names = _settings.Current.Vpn.Enabled ? Connected() : [];
        var now = string.Join(", ", names);
        if (names.Count == 0)
        {
            _connected = "";
            _place = "";
            _activities.Remove(Key);
            return;
        }
        var changed = now != _connected;
        _connected = now;
        Post(announce: changed);
        if (changed && _settings.Current.Vpn.Lookup) _ = LookUpAsync(now);
    }

    private void Post(bool announce)
    {
        var detail = _place.Length > 0 ? $"{_place} · {_connected}" : _connected;
        var card = new IndicatorViewModel("", "VPN connected", detail);
        if (announce) _activities.Post(new Activity($"{Key}.joined", Key, card, ActivityPriority.High, TimeSpan.FromSeconds(4), alert: true));
        _activities.Post(new Activity(Key, Key, card, ActivityPriority.Ambient));
    }

    private async Task LookUpAsync(string forAdapters)
    {
        try
        {
            using var response = await Http.GetAsync("https://ipwho.is/");
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = json.RootElement;
            var country = root.TryGetProperty("country", out var c) ? c.GetString() : null;
            var ip = root.TryGetProperty("ip", out var i) ? i.GetString() : null;
            var place = string.Join(" · ", new[] { country, ip }.Where(part => !string.IsNullOrEmpty(part)));
            await _dispatcher.BeginInvoke(() =>
            {
                if (_connected != forAdapters) return; // the VPN changed while the answer was on its way
                _place = place;
                Post(announce: false);
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            Log.Info($"The VPN lookup failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _settings.Changed -= OnSettingsChanged;
        _debounce.Stop();
        _activities.Remove(Key);
        _activities.Remove($"{Key}.joined");
    }
}

/// <summary>Shows a Steam download's progress beside the island, read from Steam's own manifest files. Nothing is sent anywhere.</summary>
internal sealed class SteamDownloadWatcher : IDisposable
{
    private const string Key = "steam-download";
    private readonly ActivityHost _activities;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(6) };
    private double _lastProgress = -1;
    private string _lastName = "";

    public SteamDownloadWatcher(ActivityHost activities, SettingsService settings)
    {
        _activities = activities;
        _settings = settings;
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private async void Poll()
    {
        if (!_settings.Current.ShowSteamDownloads) { Clear(); return; }
        SteamDownload? active;
        try { active = await Task.Run(Find); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (active is null) { Clear(); return; }
        if (active.Name == _lastName && Math.Abs(active.Progress - _lastProgress) < 0.005) return;
        _lastName = active.Name;
        _lastProgress = active.Progress;
        var detail = $"{active.Progress:P0} · {SteamDownloads.Size(active.Downloaded)} of {SteamDownloads.Size(active.Total)}";
        _activities.Post(new Activity(Key, Key, new IndicatorViewModel("", active.Name, detail, active.Progress), ActivityPriority.Ambient));
    }

    private static SteamDownload? Find()
    {
        SteamDownload? best = null;
        var newest = DateTime.MinValue;
        foreach (var folder in GameLibrary.SteamAppsFolders())
        {
            foreach (var manifest in Directory.EnumerateFiles(folder, "appmanifest_*.acf"))
            {
                var info = new FileInfo(manifest);
                if (Steam(info) is { } download && info.LastWriteTimeUtc > newest)
                {
                    best = download;
                    newest = info.LastWriteTimeUtc;
                }
            }
        }
        return best;
    }

    private static SteamDownload? Steam(FileInfo manifest)
    {
        // Steam holds the file open while it writes, so a read can fail; the next poll tries again.
        try { return SteamDownloads.Parse(File.ReadAllText(manifest.FullName), DateTime.UtcNow - manifest.LastWriteTimeUtc); }
        catch (IOException) { return null; }
    }

    private void Clear()
    {
        if (_lastProgress < 0) return;
        _lastProgress = -1;
        _lastName = "";
        _activities.Remove(Key);
    }

    public void Dispose()
    {
        _timer.Stop();
        _activities.Remove(Key);
    }
}
