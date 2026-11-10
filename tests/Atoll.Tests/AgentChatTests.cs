using System.Net;
using System.Net.Http;
using System.Text;
using Atoll.Modules.Claude;
using Atoll.Settings;

namespace Atoll.Tests;

/// <summary>Ask: provider resolution, chat-only flags, streaming, error text, cancellation and the view-model section.</summary>
public sealed class AgentChatTests
{
    [Fact]
    public void Settings_default_to_tools_on_and_clamp_the_timeout()
    {
        var settings = new AgentChatSettings();
        Assert.True(settings.AllowTools);
        Assert.Equal(120, settings.TimeoutSeconds);
        Assert.Equal("", settings.ClaudeModel);
        Assert.Equal(15, AgentChatService.TimeoutSeconds(new AgentChatSettings { TimeoutSeconds = 1 }));
        Assert.Equal(600, AgentChatService.TimeoutSeconds(new AgentChatSettings { TimeoutSeconds = 9_000 }));
    }

    [Fact]
    public void Agents_tab_has_a_sessions_switch_and_an_ask_switch()
    {
        var vm = new ClaudeViewModel(new ClaudeSessionTracker(TimeProvider.System));
        Assert.Equal(["Sessions", "Ask"], vm.Sections);
        Assert.True(vm.IsSessions);
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.SectionIndex = 1;
        Assert.True(vm.IsAsk);
        Assert.Contains(nameof(ClaudeViewModel.IsAsk), changed);
    }

    [Fact]
    public async Task Resolution_tries_claude_then_codex_then_opencode_then_saved_keys()
    {
        using (var chat = new AgentChatService(new FakeRunner().Install("claude"), new NoHttp()))
            Assert.Equal(AgentChatKind.ClaudeCode, (await chat.ResolveAsync(Options())).Provider.Kind);

        using (var chat = new AgentChatService(new FakeRunner().Install("codex").Install("opencode"), new NoHttp()))
        {
            var selection = await chat.ResolveAsync(Options(new AgentChatSettings { AllowTools = false }));
            Assert.Equal(AgentChatKind.Codex, selection.Provider.Kind);
            Assert.Contains("read-only", selection.Note);
            Assert.Contains("read files", selection.Note);
        }

        using (var chat = new AgentChatService(new FakeRunner().Install("opencode"), new NoHttp()))
        {
            var selection = await chat.ResolveAsync(Options(new AgentChatSettings { AllowTools = false }));
            Assert.Equal(AgentChatKind.OpenCode, selection.Provider.Kind);
            Assert.Contains("can't be restricted", selection.Note);
        }

        var runner = new FakeRunner();
        using (var chat = new AgentChatService(runner, new NoHttp()))
        {
            var selection = await chat.ResolveAsync(Options(new AgentChatSettings(), Account(OpenAi)));
            Assert.Equal(AgentChatKind.Api, selection.Provider.Kind);
            Assert.Equal("My OpenAI", selection.Provider.Name);
        }

        using (var chat = new AgentChatService(runner, new NoHttp()))
            await Assert.ThrowsAsync<AgentChatException>(() => chat.ResolveAsync(Options()));
    }

    [Fact]
    public async Task Claude_streams_partial_text_once_and_blocks_every_tool_when_tools_are_off()
    {
        var runner = new FakeRunner().Install("claude",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hel"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"lo"}}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"Hello"}""");
        using var chat = new AgentChatService(runner, new NoHttp());
        var options = Options(new AgentChatSettings { AllowTools = false });
        var selection = await chat.ResolveAsync(options);
        Assert.Equal("Chat only. Claude's tools are blocked.", selection.Note);

        var text = await CollectAsync(chat.StreamAsync(selection, "Say hello", options.Settings));
        Assert.Equal("Hello", text);

        var started = Assert.Single(runner.Started);
        Assert.Equal("Say hello", started.Session.Written);
        Assert.Contains("-p", started.Command.Arguments);
        Assert.Equal("--disallowedTools", started.Command.Arguments[^4]);
        Assert.Equal("*", started.Command.Arguments[^3]);
        Assert.Equal("--max-turns", started.Command.Arguments[^2]);
        Assert.Equal("1", started.Command.Arguments[^1]);
    }

    [Fact]
    public async Task Allowing_tools_removes_claudes_chat_only_flags()
    {
        var runner = new FakeRunner().Install("claude",
            """{"type":"result","subtype":"success","is_error":false,"result":"Done"}""");
        using var chat = new AgentChatService(runner, new NoHttp());
        var options = Options(new AgentChatSettings { AllowTools = true, ClaudeModel = "opus" });
        var selection = await chat.ResolveAsync(options);
        Assert.Contains("Tools are on", selection.Note);
        Assert.Equal("Done", await CollectAsync(chat.StreamAsync(selection, "Do it", options.Settings)));

        var arguments = Assert.Single(runner.Started).Command.Arguments;
        Assert.DoesNotContain("--disallowedTools", arguments);
        Assert.Equal("bypassPermissions", arguments[arguments.ToList().IndexOf("--permission-mode") + 1]);
        Assert.Equal("opus", arguments[arguments.ToList().IndexOf("--model") + 1]);
    }

    [Fact]
    public async Task Codex_uses_a_read_only_sandbox_when_tools_are_off_and_reads_stdin()
    {
        var runner = new FakeRunner().Install("codex",
            """{"type":"item.completed","item":{"type":"agent_message","text":"Read-only answer"}}""");
        using var chat = new AgentChatService(runner, new NoHttp());
        var options = Options(new AgentChatSettings { AllowTools = false });
        var selection = await chat.ResolveAsync(options);
        Assert.Contains("Chat only request", selection.Note);
        Assert.Contains("can still read files", selection.Note);
        Assert.Equal("Read-only answer", await CollectAsync(chat.StreamAsync(selection, "Explain", options.Settings)));

        var started = Assert.Single(runner.Started);
        Assert.Equal("Explain", started.Session.Written);
        Assert.Contains("--json", started.Command.Arguments);
        Assert.Equal("read-only", started.Command.Arguments[started.Command.Arguments.ToList().IndexOf("--sandbox") + 1]);
        Assert.Equal("-", started.Command.Arguments[^1]);
    }

    [Fact]
    public async Task Codex_can_edit_its_working_folder_when_tools_are_on()
    {
        var runner = new FakeRunner().Install("codex",
            """{"type":"item.completed","item":{"type":"agent_message","text":"Done"}}""");
        using var chat = new AgentChatService(runner, new NoHttp());
        var options = Options();
        var selection = await chat.ResolveAsync(options);
        await CollectAsync(chat.StreamAsync(selection, "Fix it", options.Settings));

        var arguments = Assert.Single(runner.Started).Command.Arguments;
        Assert.Equal("workspace-write", arguments[arguments.ToList().IndexOf("--sandbox") + 1]);
    }

    private static readonly AgentChatImage Pixel = new([1, 2, 3, 4], "image/png");

    [Fact]
    public async Task Claude_takes_pictures_as_one_stream_json_message()
    {
        var runner = new FakeRunner().Install("claude",
            """{"type":"result","subtype":"success","is_error":false,"result":"A cat"}""");
        using var chat = new AgentChatService(runner, new NoHttp());
        var options = Options();
        var selection = await chat.ResolveAsync(options);
        await CollectAsync(chat.StreamAsync(selection, "What is this?", options.Settings, default, [Pixel]));

        var started = Assert.Single(runner.Started);
        var arguments = started.Command.Arguments.ToList();
        Assert.Equal("stream-json", arguments[arguments.IndexOf("--input-format") + 1]);
        using var message = System.Text.Json.JsonDocument.Parse(started.Session.Written.Trim());
        var content = message.RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal(Convert.ToBase64String(Pixel.Data), content[0].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("What is this?", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Codex_gets_pictures_as_files_and_they_are_deleted_afterwards()
    {
        var runner = new FakeRunner().Install("codex",
            """{"type":"item.completed","item":{"type":"agent_message","text":"A cat"}}""");
        using var chat = new AgentChatService(runner, new NoHttp());
        var options = Options();
        var selection = await chat.ResolveAsync(options);
        await CollectAsync(chat.StreamAsync(selection, "What is this?", options.Settings, default, [Pixel]));

        var arguments = Assert.Single(runner.Started).Command.Arguments.ToList();
        var path = arguments[arguments.IndexOf("--image") + 1];
        Assert.EndsWith(".png", path);
        Assert.False(File.Exists(path)); // the temp copy is gone once the run ends
        Assert.Equal(["--", "-"], arguments.TakeLast(2));
    }

    [Fact]
    public async Task OpenCode_says_it_cannot_take_pictures()
    {
        using var chat = new AgentChatService(new FakeRunner().Install("opencode", "x"), new NoHttp());
        var options = Options();
        var selection = await chat.ResolveAsync(options);

        var error = await Assert.ThrowsAsync<AgentChatException>(async () =>
            await CollectAsync(chat.StreamAsync(selection, "What is this?", options.Settings, default, [Pixel])));
        Assert.Contains("OpenCode can't take pasted images", error.Message);
    }

    [Fact]
    public void API_requests_carry_pictures_in_each_providers_own_shape()
    {
        using var openAi = System.Text.Json.JsonDocument.Parse(OpenAiChatProvider.OpenAiBody("hi", "gpt", [Pixel]));
        var parts = openAi.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", parts[1].GetProperty("image_url").GetProperty("url").GetString());

        using var anthropic = System.Text.Json.JsonDocument.Parse(AnthropicChatProvider.Body("hi", "claude", [Pixel]));
        var blocks = anthropic.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("image", blocks[0].GetProperty("type").GetString());
        Assert.Equal("base64", blocks[0].GetProperty("source").GetProperty("type").GetString());

        // No picture: the plain string body every provider already accepted.
        using var plain = System.Text.Json.JsonDocument.Parse(OpenAiChatProvider.OpenAiBody("hi", "gpt", null));
        Assert.Equal("hi", plain.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task OpenCode_streams_plain_stdout_and_admits_tools_cannot_be_turned_off()
    {
        var runner = new FakeRunner().Install("opencode", "line one", "line two");
        using var chat = new AgentChatService(runner, new NoHttp());
        var options = Options(new AgentChatSettings { AllowTools = false });
        var selection = await chat.ResolveAsync(options);
        Assert.Contains("can't be restricted", selection.Note);
        Assert.Equal("line one\nline two\n", await CollectAsync(chat.StreamAsync(selection, "List", options.Settings)));

        var started = Assert.Single(runner.Started);
        Assert.Equal("run", started.Command.Arguments[0]);
        Assert.Equal("List", started.Session.Written);
    }

    [Fact]
    public async Task Api_fallback_streams_openai_sse_with_the_saved_key_and_default_model()
    {
        string? body = null;
        string? authorization = null;
        using var handler = new Handler(async (request, ct) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            body = await request.Content!.ReadAsStringAsync(ct);
            Assert.EndsWith("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream"),
            };
        });
        using var chat = new AgentChatService(new FakeRunner(), handler);
        var options = Options(new AgentChatSettings(), Account(OpenAi));
        var selection = await chat.ResolveAsync(options);
        Assert.Equal("Hi there", await CollectAsync(chat.StreamAsync(selection, "private question", options.Settings)));
        Assert.Equal("Bearer " + Key, authorization);
        Assert.Contains("\"model\":\"gpt-4o-mini\"", body);
        Assert.Contains("private question", body);
    }

    [Fact]
    public async Task Api_fallback_uses_anthropic_headers_and_stream_events()
    {
        string? body = null;
        string? keyHeader = null;
        string? version = null;
        using var handler = new Handler(async (request, ct) =>
        {
            keyHeader = request.Headers.TryGetValues("x-api-key", out var values) ? string.Join("", values) : null;
            version = request.Headers.TryGetValues("anthropic-version", out var versions) ? string.Join("", versions) : null;
            body = await request.Content!.ReadAsStringAsync(ct);
            Assert.EndsWith("/v1/messages", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n" +
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
                    Encoding.UTF8, "text/event-stream"),
            };
        });
        using var chat = new AgentChatService(new FakeRunner(), handler);
        var options = Options(new AgentChatSettings(), Account(Anthropic));
        var selection = await chat.ResolveAsync(options);
        Assert.Equal("Hi", await CollectAsync(chat.StreamAsync(selection, "Hello", options.Settings)));
        Assert.Equal(Key, keyHeader);
        Assert.Equal("2023-06-01", version);
        Assert.Contains("\"model\":\"claude-sonnet-5\"", body);
    }

    [Fact]
    public async Task Api_failures_become_one_readable_delta_without_the_key_or_prompt()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":{\"message\":\"invalid key\"}}", Encoding.UTF8, "application/json"),
        }));
        using var chat = new AgentChatService(new FakeRunner(), handler);
        var options = Options(new AgentChatSettings(), Account(OpenAi));
        var deltas = await CollectDeltasAsync(chat.AskAsync(options, "private question"));
        var delta = Assert.Single(deltas);
        Assert.True(delta.IsError);
        Assert.Contains("rejected the API key", delta.Text);
        Assert.DoesNotContain(Key, delta.Text);
        Assert.DoesNotContain("private question", delta.Text);
    }

    [Fact]
    public async Task No_agent_and_no_key_is_one_readable_delta()
    {
        using var chat = new AgentChatService(new FakeRunner(), new NoHttp());
        var delta = Assert.Single(await CollectDeltasAsync(chat.AskAsync(Options(), "private question")));
        Assert.True(delta.IsError);
        Assert.Contains("No coding agent or API key", delta.Text);
        Assert.DoesNotContain("private question", delta.Text);
    }

    [Fact]
    public async Task Stop_cancels_the_cli_and_kills_the_process_tree()
    {
        var runner = new FakeRunner().Install("claude", new GatedSession());
        using var chat = new AgentChatService(runner, new NoHttp());
        var options = Options();
        var selection = await chat.ResolveAsync(options);
        using var stop = new CancellationTokenSource();
        var reading = CollectDeltasAsync(chat.StreamAsync(selection, "Wait", options.Settings, stop.Token));
        for (var i = 0; i < 100 && runner.Started.Count == 0; i++) await Task.Delay(10);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reading);
        Assert.True(Assert.Single(runner.Started).Session.Killed);
    }

    [Fact]
    public async Task Ask_view_streams_and_escape_stops_then_clears_before_the_island_closes()
    {
        var runner = new FakeRunner().Install("opencode", new GatedSession());
        using var chat = new AgentChatService(runner, new NoHttp());
        using var vm = new AskViewModel(chat, () => Options(), null) { Draft = "First question" };
        var answered = 0;
        vm.Answered += _ => answered++;

        var sending = vm.SendCommand.ExecuteAsync(null);
        for (var i = 0; i < 100 && (vm.Answer.Length == 0 || !vm.IsRunning); i++) await Task.Delay(10);
        Assert.True(vm.IsRunning);
        Assert.Equal("partial\n", vm.Answer);
        Assert.True(vm.HandleEscape()); // stop the run, not collapse the island
        await sending;
        Assert.False(vm.IsRunning);
        Assert.Equal("partial\n", vm.Answer);

        vm.Draft = "Next question";
        Assert.True(vm.HandleEscape()); // clears the draft next
        Assert.Equal("", vm.Draft);
        Assert.True(vm.HandleEscape()); // then the leftover answer
        Assert.Equal("", vm.Answer);
        Assert.False(vm.HandleEscape()); // an empty Ask box lets Escape close the island
        Assert.Equal(0, answered); // a stopped run is not announced as a finished answer
    }

    [Fact]
    public async Task Ask_view_announces_a_finished_answer_once()
    {
        var runner = new FakeRunner().Install("opencode", "The answer");
        using var chat = new AgentChatService(runner, new NoHttp());
        using var vm = new AskViewModel(chat, () => Options(), null) { Draft = "Question" };
        var answered = 0;
        vm.Answered += _ => answered++;
        await vm.SendCommand.ExecuteAsync(null);
        Assert.False(vm.IsRunning);
        Assert.Equal("The answer\n", vm.Answer);
        Assert.Equal("OpenCode", vm.Provider);
        Assert.Equal(1, answered);
    }

    [Fact]
    public void Timeout_and_rejected_keys_map_to_readable_messages()
    {
        Assert.Contains("No answer after 30 seconds", AgentChatService.Describe(new TaskCanceledException(), 30));
        Assert.Contains("rejected the API key", AgentChatService.Describe(new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized), 30));
        Assert.Contains("Could not reach", AgentChatService.Describe(new HttpRequestException(), 30));
    }

    [Fact]
    public async Task Ask_view_shows_a_readable_error_and_keeps_the_question_out_of_it()
    {
        using var chat = new AgentChatService(new FakeRunner(), new NoHttp());
        using var vm = new AskViewModel(chat, () => Options(), null) { Draft = "private question" };
        await vm.SendCommand.ExecuteAsync(null);
        Assert.False(vm.IsRunning);
        Assert.Contains("No coding agent", vm.Error);
        Assert.DoesNotContain("private question", vm.Error);
        Assert.Equal("", vm.Answer);
    }

    // ---------- fakes ----------

    private const string Key = "sk-test-never-real";
    private const string OpenAi = "https://api.openai.com/v1/";
    private const string Anthropic = "https://api.anthropic.com/v1/";

    private static ProviderAccount Account(string baseUrl) => new()
    {
        Name = baseUrl.Contains("anthropic") ? "My Anthropic" : "My OpenAI",
        BaseUrl = baseUrl,
        EncryptedKey = SecretStore.Protect(Key),
    };

    private static AgentChatOptions Options(AgentChatSettings? settings = null, params ProviderAccount[] accounts) =>
        new(settings ?? new AgentChatSettings(), accounts);

    private static async Task<string> CollectAsync(IAsyncEnumerable<AgentChatDelta> stream, CancellationToken ct = default)
    {
        var text = new StringBuilder();
        await foreach (var delta in stream.WithCancellation(ct))
        {
            Assert.False(delta.IsError, delta.Text);
            text.Append(delta.Text);
        }
        return text.ToString();
    }

    private static async Task<List<AgentChatDelta>> CollectDeltasAsync(IAsyncEnumerable<AgentChatDelta> stream, CancellationToken ct = default)
    {
        var deltas = new List<AgentChatDelta>();
        await foreach (var delta in stream.WithCancellation(ct)) deltas.Add(delta);
        return deltas;
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly Dictionary<string, TestSession> _next = [];
        private readonly HashSet<string> _installed = [];
        public List<(ProcessCommand Command, TestSession Session)> Started { get; } = [];

        public FakeRunner Install(string name, params string[] output) => Install(name, new FakeSession(output));

        public FakeRunner Install(string name, TestSession session)
        {
            _installed.Add(name);
            _next[name] = session;
            return this;
        }

        public ProcessCommand? Resolve(string command) => _installed.Contains(command) ? new ProcessCommand(command + ".exe", []) : null;

        public IProcessSession Start(ProcessCommand command)
        {
            var name = Path.GetFileNameWithoutExtension(command.FileName);
            if (!_next.Remove(name, out var session)) session = new FakeSession([]);
            Started.Add((command, session));
            return session;
        }
    }

    private abstract class TestSession : IProcessSession
    {
        private readonly StringWriter _input = new();
        public string Written => _input.ToString();
        public TextWriter StandardInput => _input;
        public abstract TextReader StandardOutput { get; }
        public TextReader StandardError { get; } = new StringReader("");
        public int ExitCode { get; init; }
        public Task<int> Completion => Task.FromResult(ExitCode);
        public bool Killed { get; private set; }
        public void Kill() => Killed = true;
        public virtual void Dispose() => Kill();
    }

    private sealed class FakeSession(params string[] lines) : TestSession
    {
        public override TextReader StandardOutput { get; } = new StringReader(lines.Length == 0 ? "" : string.Join("\n", lines) + "\n");
    }

    private sealed class GatedSession : TestSession
    {
        public override TextReader StandardOutput { get; } = new GatedReader();
    }

    /// <summary>One line of partial text, then a pipe that only ends when the run is cancelled.</summary>
    private sealed class GatedReader : TextReader
    {
        private int _lines;
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _lines) == 1) return "partial";
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("This test must not reach HTTP.");
    }
}
