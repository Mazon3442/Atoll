using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Atoll.Desktop.Providers;
using Atoll.Modules.Claude;
using Atoll.Settings;

namespace Atoll.Desktop.Tests;

public sealed class ProviderTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Provider_settings_save_encrypted_keys_keep_blank_edits_and_render_the_editor()
    {
        var path = Temp(); using var settings = new SettingsService(new SettingsStore(path));
        foreach (var entry in DesktopModules.Catalog) settings.Current.Modules.Add(new ModuleEntry { Id = entry.Id, Enabled = false });
        using var activities = new Atoll.Activities.ActivityHost(); using var modules = new DesktopModules(settings, activities);
        var window = new SettingsWindow(settings, modules, () => { }); window.SelectPage(4); window.Show(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Controls.TextBox Field(string name) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == name);
        Field("Provider name").Text = "Fake provider"; Field("API base URL").Text = "https://provider.example/v1/"; Field("Provider API key").Text = "test-key-never-real";
        var add = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().Single(b => Equals(b.Content, "Add provider"));
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var account = Assert.Single(settings.Current.Providers.Accounts); Assert.Equal("test-key-never-real", SecretStore.Unprotect(account.EncryptedKey));
        settings.Flush(); Assert.DoesNotContain("test-key-never-real", File.ReadAllText(path));
        var save = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().Single(b => Equals(b.Content, "Save provider"));
        save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Assert.Equal(account.EncryptedKey, Assert.Single(settings.Current.Providers.Accounts).EncryptedKey);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var scroll = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.ScrollViewer>().Single(s => s.Content is Avalonia.Controls.ContentControl control && control.Content is Avalonia.Controls.StackPanel);
        scroll.ScrollToEnd(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("ATOLL_TEST_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "atoll-desktop-review"); Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "provider-editor.png"));
        window.Close();
    }

    [Fact]
    public void Copied_gateway_tokens_are_marked_private_for_clipboard_history()
    {
        using var data = SettingsWindow.GatewayClipboardData(Token);
        Assert.True(ClipboardModule.IsPrivate(data.Formats.Select(f => f.Identifier)));
    }

    private const string Token = "local-test-token-01234567890123456789";
    private static ProviderEndpoint Endpoint => new("test", "Test provider", new Uri("https://provider.example/v1/"), "upstream-secret", "admin-secret");
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "atoll-provider-tests", Guid.NewGuid().ToString("N"), "totals.json");
    private static HttpRequestMessage Request(Uri address, string path = "chat/completions")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, "providers/test/v1/" + path)) { Content = new StringContent("{\"model\":\"my-model\",\"messages\":[{\"content\":\"private prompt\"}]}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token); return request;
    }

    [Fact]
    public async Task Gateway_streams_unchanged_replaces_auth_and_persists_only_totals()
    {
        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"private answer\"}}]}\n\ndata: {\"model\":\"my-model\",\"usage\":{\"prompt_tokens\":13,\"completion_tokens\":7}}\n\ndata: [DONE]\n\n";
        var path = Temp(); var ledger = new ProviderLedger(path);
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal("https://provider.example/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer upstream-secret", request.Headers.Authorization!.ToString());
            Assert.False(request.Headers.Contains("Cookie")); Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Contains("private prompt", await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        });
        await using var gateway = new ProviderGateway(ledger, handler); gateway.Configure([Endpoint], Token); await gateway.StartAsync(0);
        using var client = new HttpClient(); using var request = Request(gateway.Address!); request.Headers.Add("Cookie", "private-cookie");
        using var response = await client.SendAsync(request);
        Assert.Equal(sse, await response.Content.ReadAsStringAsync()); Assert.Equal(0, gateway.Active("test"));
        Assert.Equal(new ProviderTotals(1, 0, 13, 7, 0), ledger.Get("test"));
        var saved = File.ReadAllText(path); Assert.DoesNotContain("private", saved); Assert.DoesNotContain("secret", saved); Assert.DoesNotContain("my-model", saved);
        Assert.Equal(ledger.Get("test"), new ProviderLedger(path).Get("test"));
    }

    [Fact]
    public async Task Gateway_rejects_browser_origins_bad_hosts_auth_and_unapproved_paths_before_upstream()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        await using var gateway = new ProviderGateway(new ProviderLedger(Temp()), handler); gateway.Configure([Endpoint], Token); await gateway.StartAsync(0);
        using var client = new HttpClient();
        foreach (var kind in new[] { "origin", "host", "auth", "path", "query" })
        {
            using var request = Request(gateway.Address!, kind == "path" ? "organization/costs" : kind == "query" ? "chat/completions?api_key=bad" : "chat/completions");
            if (kind == "origin") request.Headers.Add("Origin", "https://evil.example");
            if (kind == "host") request.Headers.Host = "evil.example";
            if (kind == "auth") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
            using var response = await client.SendAsync(request); Assert.InRange((int)response.StatusCode, 400, 499);
        }
        Assert.Equal(0, calls); Assert.Equal(0, gateway.Active("test"));
    }

    [Fact]
    public async Task Gateway_tracks_errors_missing_usage_and_parallel_requests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var handler = new Handler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2) started.SetResult();
            await release.Task.WaitAsync(ct);
            return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":\"rate limited\"}", Encoding.UTF8, "application/json") };
        });
        var ledger = new ProviderLedger(Temp()); await using var gateway = new ProviderGateway(ledger, handler); gateway.Configure([Endpoint], Token); await gateway.StartAsync(0);
        using var client = new HttpClient(); using var one = Request(gateway.Address!); using var two = Request(gateway.Address!);
        var first = client.SendAsync(one); var second = client.SendAsync(two);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(2, gateway.Active("test")); release.SetResult();
        using var a = await first; using var b = await second;
        Assert.Equal(HttpStatusCode.TooManyRequests, a.StatusCode); Assert.Equal(new ProviderTotals(2, 2, 0, 0, 2), ledger.Get("test")); Assert.Equal(0, gateway.Active("test"));
    }

    [Fact]
    public async Task Client_cancellation_cancels_upstream_and_clears_activity()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return new(HttpStatusCode.OK);
        });
        await using var gateway = new ProviderGateway(new ProviderLedger(Temp()), handler); gateway.Configure([Endpoint], Token); await gateway.StartAsync(0);
        using var client = new HttpClient(); using var request = Request(gateway.Address!); using var stop = new CancellationTokenSource();
        var send = client.SendAsync(request, stop.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 100 && gateway.Active("test") != 0; i++) await Task.Delay(10);
        Assert.Equal(0, gateway.Active("test"));
    }

    [Fact]
    public void Accounting_handles_partial_sse_anthropic_and_oversized_unknown_usage()
    {
        var capture = new UsageCapture(true);
        var bytes = Encoding.UTF8.GetBytes("data: {\"message\":{\"usage\":{\"input_tokens\":3,\"cache_read_input_tokens\":5}}}\n\ndata: {\"usage\":{\"output_tokens\":9}}\n\n");
        foreach (var b in bytes) capture.Add([b]); capture.Complete();
        Assert.Equal(8, capture.Usage.InputTokens); Assert.Equal(9, capture.Usage.OutputTokens);
        var huge = new UsageCapture(false); huge.Add(new byte[4 * 1024 * 1024 + 1]); huge.Add("{\"usage\":{\"input_tokens\":1}}"u8); huge.Complete(); Assert.Null(huge.Usage.InputTokens);
        var json = new UsageCapture(false); json.Add("{\"response\":{\"model\":\"m\",\"usage\":{\"input_tokens\":2,\"output_tokens\":4}}}"u8); json.Complete(); Assert.Equal(new RequestUsage("m", 2, 4), json.Usage);
    }

    [Fact]
    public async Task Metrics_use_separate_credentials_and_keep_provider_currency()
    {
        var seen = new List<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            seen.Add(request.RequestUri!.Host + " " + request.Headers.Authorization);
            var json = request.RequestUri.Host == "api.deepseek.com" ? """{"balance_infos":[{"currency":"CNY","total_balance":"12.34"}]}"""
                : request.RequestUri.AbsolutePath.EndsWith("costs") ? """{"data":[{"results":[{"amount":{"value":1.25,"currency":"usd"}}]}],"has_more":false}"""
                : """{"data":[{"results":[{"input_tokens":10,"output_tokens":20}]}],"has_more":false}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        Assert.Contains("12.34 CNY", await ProviderMetrics.ReadAsync(Endpoint with { BaseUri = new("https://api.deepseek.com/v1/") }, client, default));
        var openai = Endpoint with { BaseUri = new("https://api.openai.com/v1/") };
        var text = await ProviderMetrics.ReadAsync(openai, client, default); Assert.Contains("organization today", text); Assert.Contains("1.25 usd", text);
        Assert.Equal(["api.deepseek.com Bearer upstream-secret", "api.openai.com Bearer admin-secret", "api.openai.com Bearer admin-secret"], seen);
        Assert.Contains("separate admin key", await ProviderMetrics.ReadAsync(openai with { AdminKey = null }, client, default)); Assert.Equal(3, seen.Count);
    }

    [Theory]
    [InlineData("http://provider.example/v1/")]
    [InlineData("https://user:password@provider.example/v1/")]
    [InlineData("https://127.0.0.1/v1/")]
    [InlineData("https://provider.example/v1/?key=secret")]
    public void Provider_configuration_rejects_unsafe_base_urls(string url)
    { Assert.Throws<ArgumentException>(() => ProviderEndpoint.From(new ProviderAccount { BaseUrl = url, EncryptedKey = SecretStore.Protect("test") })); }

    [Fact]
    public void Codex_existing_sessions_report_activity_without_retaining_prompt_or_replaying_done_alerts()
    {
        var root = Path.GetDirectoryName(Temp())!; var directory = Path.Combine(root, DateTime.UtcNow.ToString("yyyy/MM/dd")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout.jsonl");
        string Event(string type, string payload) => $"{{\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"type\":\"{type}\",\"payload\":{payload}}}\n";
        File.WriteAllText(path, Event("session_meta", "{\"id\":\"session\",\"cwd\":\"project\",\"instructions\":\"" + new string('x', 24 * 1024) + "\"}")
            + Event("response_item", "{\"type\":\"message\",\"content\":\"" + new string('x', 5 * 1024 * 1024) + "\"}")
            + Event("event_msg", "{\"type\":\"task_started\",\"prompt\":\"private prompt\"}"));
        using var monitor = new CodexSessionMonitor(root); var messages = new List<Atoll.Claude.HookMessage>(); monitor.Received += messages.Add; monitor.Poll();
        Assert.Equal("UserPromptSubmit", Assert.Single(messages).Event); Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(messages));
        File.AppendAllText(path, Event("response_item", "{\"type\":\"custom_tool_call\",\"name\":\"apply_patch\",\"input\":\"private content\"}")); monitor.Poll(); Assert.Equal("PreToolUse", messages.Last().Event);
        File.AppendAllText(path, Event("event_msg", "{\"type\":\"task_complete\"}")); monitor.Poll(); Assert.Equal("Stop", messages.Last().Event);
        messages.Clear(); using var restart = new CodexSessionMonitor(root); restart.Received += messages.Add; restart.Poll(); Assert.Equal("SessionStart", Assert.Single(messages).Event);
    }
}
