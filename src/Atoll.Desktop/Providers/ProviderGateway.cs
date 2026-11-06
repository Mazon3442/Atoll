using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Atoll.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Atoll.Desktop.Providers;

public sealed record ProviderEndpoint(string Id, string Name, Uri BaseUri, string Key, string? AdminKey)
{
    public static ProviderEndpoint From(ProviderAccount account)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(account.Id, "^[a-zA-Z0-9_-]{1,64}$")) throw new ArgumentException("Invalid provider identifier.");
        if (!Uri.TryCreate(account.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a public HTTPS API base URL without credentials, query or fragment.");
        var key = SecretStore.Unprotect(account.EncryptedKey);
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new ArgumentException("Save an API key for this provider.");
        return new(account.Id, account.Name, uri, key, SecretStore.Unprotect(account.EncryptedAdminKey));
    }
    public bool IsOpenAI => BaseUri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);
    public bool IsDeepSeek => BaseUri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
    public bool IsAnthropic => BaseUri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase);
}

public sealed class ProviderGateway : IAsyncDisposable
{
    public const int Port = 47831;
    private readonly HttpClient _client;
    private readonly ProviderLedger _ledger;
    private readonly ConcurrentDictionary<string, int> _active = new();
    private ProviderEndpoint[] _providers = [];
    private string _token = "";
    private WebApplication? _app;
    public Uri? Address { get; private set; }
    public event Action? Changed;
    public int Active(string id) => _active.GetValueOrDefault(id);
    public ProviderGateway(ProviderLedger ledger, HttpMessageHandler? handler = null)
    {
        _ledger = ledger;
        _client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public void Configure(ProviderEndpoint[] providers, string token)
    {
        if (token.Length < 32) throw new ArgumentException("Gateway token is invalid.");
        Volatile.Write(ref _providers, providers.ToArray()); Volatile.Write(ref _token, token);
    }
    public async Task StartAsync(int port = Port, CancellationToken ct = default)
    {
        if (_app is not null) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(ProviderGateway).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, port); options.Limits.MaxRequestBodySize = 32 * 1024 * 1024; options.Limits.MaxConcurrentConnections = 64; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15); });
        var app = builder.Build(); app.Run(Forward);
        try { await app.StartAsync(ct); _app = app; Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()); }
        catch { await app.DisposeAsync(); throw; }
    }
    private static readonly HashSet<string> Paths = ["chat/completions", "responses", "messages", "models"];
    private async Task Forward(HttpContext context)
    {
        var request = context.Request; var response = context.Response;
        if (request.Headers.ContainsKey("Origin") || request.Host.Host != "127.0.0.1" || request.Host.Port != Address?.Port)
        { response.StatusCode = 403; return; }
        var auth = request.Headers.Authorization.ToString();
        if (auth.Length == 0 && request.Headers.TryGetValue("x-api-key", out var apiKey)) auth = "Bearer " + apiKey.ToString();
        var expected = "Bearer " + Volatile.Read(ref _token);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth), Encoding.UTF8.GetBytes(expected)))
        { response.StatusCode = 401; return; }
        var parts = (request.Path.Value ?? "").Split('/', 5);
        if (parts.Length != 5 || parts[1] != "providers" || parts[3] != "v1" || !Paths.Contains(parts[4]) || request.QueryString.HasValue
            || (parts[4] == "models" ? request.Method != "GET" : request.Method != "POST"))
        { response.StatusCode = 404; return; }
        var provider = Volatile.Read(ref _providers).FirstOrDefault(p => p.Id == parts[2]);
        if (provider is null) { response.StatusCode = 404; return; }
        var counted = request.Method == "POST";
        var failed = true; UsageCapture? capture = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        if (counted) { _active.AddOrUpdate(provider.Id, 1, (_, n) => n + 1); Changed?.Invoke(); }
        try
        {
            using var upstream = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(provider.BaseUri, parts[4]));
            if (request.Method == "POST")
            {
                if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true) { response.StatusCode = 415; return; }
                upstream.Content = new StreamContent(request.Body);
                upstream.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
            }
            // Only allow protocol headers. Caller authorization, cookies, routing and forwarding headers never leave loopback.
            upstream.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (provider.IsAnthropic)
            {
                upstream.Headers.Add("x-api-key", provider.Key);
                upstream.Headers.Add("anthropic-version", request.Headers.TryGetValue("anthropic-version", out var version) ? version.ToString() : "2023-06-01");
                if (request.Headers.TryGetValue("anthropic-beta", out var beta)) upstream.Headers.TryAddWithoutValidation("anthropic-beta", beta.ToArray());
            }
            else upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.Key);
            using var result = await _client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.StatusCode = (int)result.StatusCode;
            response.ContentType = result.Content.Headers.ContentType?.ToString() ?? "application/json";
            response.Headers.CacheControl = "no-store";
            if (result.Headers.TryGetValues("retry-after", out var retry)) response.Headers.RetryAfter = retry.ToArray();
            capture = new UsageCapture(response.ContentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase));
            await using var stream = await result.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (counted) capture.Add(bytes.AsSpan(0, read));
                await response.Body.WriteAsync(bytes.AsMemory(0, read), timeout.Token);
                await response.Body.FlushAsync(timeout.Token);
            }
            failed = !result.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (!response.HasStarted) { response.StatusCode = context.RequestAborted.IsCancellationRequested ? 499 : 502; await response.WriteAsync("{\"error\":{\"message\":\"Provider request failed or timed out.\"}}", CancellationToken.None); }
            else context.Abort();
        }
        finally
        {
            capture?.Complete();
            if (counted)
            {
                _ledger.Record(provider.Id, failed, capture?.Usage ?? new(null, null, null));
                _active.AddOrUpdate(provider.Id, 0, (_, n) => Math.Max(0, n - 1)); Changed?.Invoke();
            }
        }
    }
    public async Task StopAsync()
    {
        var app = _app; _app = null; Address = null;
        if (app is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); _client.Dispose(); }
}
