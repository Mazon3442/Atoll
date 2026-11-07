using System.Net.Http;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Settings;
#if AVALONIA
using Avalonia.Threading;
#else
using System.Windows.Threading;
#endif
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Modules.Providers;

public sealed partial class ProviderRow(string id, string name) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string BaseUrl => $"http://127.0.0.1:{ProviderGateway.Port}/providers/{Id}/v1";
    [ObservableProperty] private string _activity = "No API requests in progress";
    [ObservableProperty] private string _totals = "";
    [ObservableProperty] private string _metrics = "Provider metrics have not been refreshed.";
}

public sealed partial class ProviderViewModel : ObservableObject
{
    [ObservableProperty] private string _status = "Local gateway is off. Enable it in Settings > Agents.";
    [ObservableProperty] private string _activity = "API activity";
    public ObservableCollection<ProviderRow> Providers { get; } = [];
}

public sealed class ProviderModule : IIslandModule
{
    private readonly ProviderViewModel _vm = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _metrics = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private ModuleContext? _context;
    private ProviderGateway? _gateway;
    private ProviderLedger? _ledger;
    private ProviderEndpoint[] _providers = [];
    private DispatcherTimer? _timer;
    private bool _disposed;
    private bool _refreshing;
    private int _revision;
    private string? _configuration;
    public object Panel => _vm;
    public void Start(ModuleContext context)
    {
        _context = context;
        _ledger = new ProviderLedger(Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "provider-totals.json"));
        _gateway = new ProviderGateway(_ledger); _gateway.Changed += GatewayChanged;
        context.Settings.Changed += Configure;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) }; _timer.Tick += RefreshMetrics; _timer.Start();
        Configure();
    }
    private async void Configure()
    {
        if (_disposed || _context is null || _gateway is null) return;
        var settings = _context.Settings.Current.Providers;
        var configuration = System.Text.Json.JsonSerializer.Serialize(settings);
        if (configuration == _configuration) return;
        _configuration = configuration;
        var revision = ++_revision;
        var providers = new List<ProviderEndpoint>(); var errors = new List<string>();
        foreach (var account in settings.Accounts)
        {
            try { providers.Add(ProviderEndpoint.From(account)); }
            catch (ArgumentException ex) { errors.Add(account.Name + ": " + ex.Message); }
        }
        var token = SecretStore.Unprotect(settings.EncryptedGatewayToken);
        var enabled = settings.GatewayEnabled;
        _providers = providers.ToArray();
        var old = _vm.Providers.ToDictionary(p => p.Id);
        _vm.Providers.Clear(); foreach (var p in providers) _vm.Providers.Add(old.TryGetValue(p.Id, out var row) && row.Name == p.Name ? row : new(p.Id, p.Name));
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed || revision != _revision) return;
            if (token is { Length: >= 32 }) _gateway.Configure(_providers, token);
            if (enabled)
            {
                if (token is not { Length: >= 32 }) { _vm.Status = "Save a gateway token in Settings > Agents before enabling it."; return; }
                await _gateway.StartAsync(ct: _shutdown.Token);
                _vm.Status = "Local gateway is listening on 127.0.0.1:" + ProviderGateway.Port + ". Only requests routed here appear below.";
            }
            else { await _gateway.StopAsync(); _vm.Status = "Local gateway is off. Enable it in Settings > Agents."; }
            if (errors.Count > 0) _vm.Status += " " + string.Join(" ", errors);
            Update();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { if (!_disposed) _vm.Status = "The local gateway could not start. Check whether port " + ProviderGateway.Port + " is already in use."; }
        finally { _lifecycle.Release(); }
        if (!_disposed) RefreshMetrics(null, EventArgs.Empty);
    }
    // The gateway raises Changed on request threads.
    private void GatewayChanged() => _ = _context?.Dispatcher.InvokeAsync(Update);
    private void Update()
    {
        if (_disposed || _gateway is null || _ledger is null) return;
        var count = 0;
        foreach (var row in _vm.Providers)
        {
            var active = _gateway.Active(row.Id); count += active;
            row.Activity = active > 0 ? $"{active} API request(s) in progress" : "No API requests in progress";
            var totals = _ledger.Get(row.Id);
            row.Totals = $"Through ATOLL: {totals.Requests:N0} requests · {totals.Failed:N0} failed · {totals.InputTokens:N0} input · {totals.OutputTokens:N0} output tokens";
            if (totals.UnknownUsage > 0) row.Totals += $" · usage unavailable for {totals.UnknownUsage:N0} requests";
            if (_ledger.Error is { } error) row.Totals += " · " + error;
        }
        _vm.Activity = $"{count} API request(s) in progress";
        if (count > 0) _context?.Post("requests", _vm, ActivityPriority.Normal); else _context?.Remove("requests");
    }
    private async void RefreshMetrics(object? sender, EventArgs e)
    {
        if (_refreshing || _disposed) return; _refreshing = true;
        var revision = _revision;
        try
        {
            foreach (var provider in _providers)
            {
                string text;
                try { text = await ProviderMetrics.ReadAsync(provider, _metrics, _shutdown.Token); }
                catch (HttpRequestException ex) { text = ex.StatusCode is { } code ? $"Provider metrics unavailable (HTTP {(int)code}). Check the metrics key and permissions." : "Could not reach the provider metrics service."; }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or IOException or FormatException or OperationCanceledException)
                { text = "Provider metrics unavailable. Retry after checking the account and connection."; }
                if (_disposed || revision != _revision) return;
                if (_vm.Providers.FirstOrDefault(p => p.Id == provider.Id) is { } row) row.Metrics = text;
            }
        }
        finally { _refreshing = false; if (!_disposed && revision != _revision) RefreshMetrics(null, EventArgs.Empty); }
    }
    public void Dispose()
    {
        _disposed = true; _revision++; _shutdown.Cancel(); _timer?.Stop();
        if (_context is not null) _context.Settings.Changed -= Configure;
        if (_gateway is { } gateway) { gateway.Changed -= GatewayChanged; _ = Close(gateway); }
    }
    private async Task Close(ProviderGateway gateway)
    {
        await _lifecycle.WaitAsync();
        try { await gateway.DisposeAsync(); }
        catch (Exception ex) { Log.Error("Could not stop provider gateway", ex); }
        finally { _lifecycle.Release(); _metrics.Dispose(); _shutdown.Dispose(); }
    }
}
