using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Atoll.Modules.Providers;
using Atoll.Settings;

namespace Atoll.Modules.Claude;

/// <summary>
/// The Agents tab's Ask backend. It resolves the first installed CLI agent, then your own API keys, and streams the
/// answer. It never logs prompts, answers or keys: failures leave as one readable delta.
/// </summary>
public sealed class AgentChatService : IAgentChat, IDisposable
{
    private readonly IProcessRunner _processes;
    private readonly HttpClient _client;
    private readonly Dictionary<AgentCli, ProcessCommand?> _commands = [];

    public AgentChatService(IProcessRunner? processes = null, HttpMessageHandler? handler = null)
    {
        _processes = processes ?? new DesktopProcessRunner();
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The chosen source first, otherwise Claude Code, Codex, OpenCode, then a saved API key.</summary>
    public Task<AgentChatSelection> ResolveAsync(AgentChatOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var settings = options.Settings;
        var order = Order(settings.Preferred);

        foreach (var id in order)
        {
            if (id == "claude" && Command(AgentCli.Claude) is { } claude)
            {
                var provider = new ClaudeCodeChatProvider(_processes, claude, settings.ClaudeModel, settings.AllowTools);
                return Task.FromResult(new AgentChatSelection(provider, settings.AllowTools
                    ? "Tools are on: Claude can run commands and edit files."
                    : "Chat only. Claude's tools are blocked."));
            }
            if (id == "codex" && Command(AgentCli.Codex) is { } codex)
            {
                var provider = new CodexChatProvider(_processes, codex, settings.CodexModel, settings.AllowTools);
                return Task.FromResult(new AgentChatSelection(provider, settings.AllowTools
                    ? "Tools are on: Codex can edit files in its working folder and run commands."
                    : "Chat only request, but Codex always runs in a read-only sandbox and can still read files."));
            }
            if (id == "opencode" && Command(AgentCli.OpenCode) is { } openCode)
            {
                var provider = new OpenCodeChatProvider(_processes, openCode, settings.OpenCodeModel);
                return Task.FromResult(new AgentChatSelection(provider, settings.AllowTools
                    ? "Tools are on."
                    : "OpenCode can't be restricted in one-shot runs, so it may still use its tools."));
            }
        }

        foreach (var account in AccountsFor(options))
        {
            ProviderEndpoint endpoint;
            try { endpoint = ProviderEndpoint.From(account); }
            catch (ArgumentException) { continue; } // an invalid URL or missing key; the next saved key may work
            var model = ApiModel(settings.ApiModel, endpoint);
            IAgentChatProvider provider = endpoint.IsAnthropic
                ? new AnthropicChatProvider(endpoint, model, _client)
                : new OpenAiChatProvider(endpoint, model, _client);
            return Task.FromResult(new AgentChatSelection(provider,
                "Chat only. Your API key answers directly; no agent runs on this PC."));
        }

        throw new AgentChatException(
            "No coding agent or API key is available. Install Claude Code, Codex or OpenCode, or add an API key in Settings › Agents.");
    }

    /// <summary>Resolves and streams, turning a resolution failure into the same single error delta.</summary>
    public async IAsyncEnumerable<AgentChatDelta> AskAsync(AgentChatOptions options, string prompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        AgentChatSelection? selection = null;
        string? failure = null;
        try { selection = await ResolveAsync(options, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { failure = Describe(ex, TimeoutSeconds(options.Settings)); }
        if (failure is not null)
        {
            yield return new AgentChatDelta(failure, true);
            yield break;
        }
        await foreach (var delta in StreamAsync(selection!, prompt, options.Settings, ct)) yield return delta;
    }

    /// <summary>Streams one answer. A provider failure becomes a single error delta; Stop throws cancellation.</summary>
    public async IAsyncEnumerable<AgentChatDelta> StreamAsync(AgentChatSelection selection, string prompt,
        AgentChatSettings settings, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seconds = TimeoutSeconds(settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var token = timeout.Token;

        await using var answers = selection.Provider.StreamAsync(prompt, token).GetAsyncEnumerator(token);
        while (true)
        {
            bool moved;
            string? failure = null;
            try { moved = await answers.MoveNextAsync(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                failure = $"No answer after {seconds} seconds. Try a shorter question or raise the timeout in Settings › Agents.";
                moved = false;
            }
            catch (Exception ex)
            {
                failure = Describe(ex, seconds);
                moved = false;
            }
            if (failure is not null)
            {
                yield return new AgentChatDelta(failure, true);
                yield break;
            }
            if (!moved) yield break;
            yield return answers.Current;
        }
    }

    /// <summary>Probes a CLI at most once per session: an installed-now agent stays found for this run of ATOLL.</summary>
    private ProcessCommand? Command(AgentCli cli)
    {
        if (_commands.TryGetValue(cli, out var cached)) return cached;
        ProcessCommand? command = null;
        try
        {
            command = _processes.Resolve(cli switch
            {
                AgentCli.Claude => "claude",
                AgentCli.Codex => "codex",
                _ => "opencode",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        _commands[cli] = command;
        return command;
    }

    /// <summary>Claude, Codex, OpenCode in the usual order, with the chosen agent pulled to the front.</summary>
    internal static IReadOnlyList<string> Order(string preferred)
    {
        var order = new List<string> { "claude", "codex", "opencode" };
        var index = order.IndexOf(preferred);
        if (index > 0) order.Insert(0, order[index]); // the loop skips the duplicate by id
        if (index > 0) order.RemoveAt(index + 1);
        return order;
    }

    /// <summary>The chosen API account first, then the rest in settings order.</summary>
    private static IEnumerable<ProviderAccount> AccountsFor(AgentChatOptions options)
    {
        var preferred = options.Settings.Preferred;
        if (!preferred.StartsWith("api:", StringComparison.Ordinal)) return options.Accounts;
        var id = preferred[4..];
        return options.Accounts.OrderBy(account => account.Id == id ? 0 : 1);
    }

    internal static int TimeoutSeconds(AgentChatSettings settings) => Math.Clamp(settings.TimeoutSeconds, 15, 600);

    private static string ApiModel(string configured, ProviderEndpoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        if (endpoint.IsAnthropic) return "claude-sonnet-5";
        if (endpoint.IsDeepSeek) return "deepseek-chat";
        return "gpt-4o-mini";
    }

    internal static string Describe(Exception ex, int seconds) => ex switch
    {
        AgentChatException known => known.Message,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "The provider rejected the API key. Check it in Settings › Agents.",
        HttpRequestException { StatusCode: { } status } => $"The provider returned HTTP {(int)status}. Try again in a moment.",
        HttpRequestException => "Could not reach the provider. Check the connection and try again.",
        TaskCanceledException or TimeoutException => $"No answer after {seconds} seconds. Try a shorter question or raise the timeout in Settings › Agents.",
        JsonException => "The agent sent a reply ATOLL could not read.",
        Win32Exception => "The agent could not start. Check that it is installed and signed in.",
        IOException => "The agent's output could not be read.",
        _ => "The agent stopped before answering.",
    };

    public void Dispose() => _client.Dispose();

    private enum AgentCli
    {
        Claude,
        Codex,
        OpenCode,
    }
}
