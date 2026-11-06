using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atoll.Claude;
using Atoll.Modules.Claude;
using Microsoft.Extensions.Time.Testing;

namespace Atoll.Tests;

public class HookProtocolTests
{
    private static HookMessage Parse(string json) =>
        HookProtocol.FromHookInput(JsonDocument.Parse(json).RootElement, 42);

    [Fact]
    public void Tool_calls_are_summarized_without_their_payload()
    {
        var m = Parse("""
            {"hook_event_name":"PreToolUse","session_id":"s1","cwd":"C:\\code\\island",
             "tool_name":"Write","tool_input":{"file_path":"C:\\code\\island\\App.xaml.cs","content":"...huge..."}}
            """);

        Assert.Equal("PreToolUse", m.Event);
        Assert.Equal("Editing App.xaml.cs", m.Detail);
        Assert.Equal(42, m.Hwnd);
        Assert.DoesNotContain("huge", JsonSerializer.Serialize(m, HookProtocol.Json));
    }

    [Theory]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"npm test\nmore","description":"Run tests"}}""", "Running Run tests")]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"npm test\nmore"}}""", "Running npm test")]
    [InlineData("""{"tool_name":"Grep","tool_input":{"pattern":"TODO"}}""", "Searching TODO")]
    [InlineData("""{"tool_name":"WebFetch","tool_input":{"url":"https://docs.example.com/a"}}""", "Fetching docs.example.com")]
    [InlineData("""{"tool_name":"mcp__github__create_issue","tool_input":{}}""", "Using create_issue")]
    [InlineData("""{"tool_name":"Read","tool_input":{}}""", "Reading a file")]
    public void Tool_descriptions(string json, string expected) => Assert.Equal(expected, Parse(json).Detail);
}

public class ClaudeSessionTrackerTests
{
    private readonly FakeTimeProvider _time = new();

    private static HookMessage Hook(string ev, string? detail = null, string? message = null, string? type = null) =>
        new() { Event = ev, SessionId = "s1", Cwd = @"C:\code\island", Detail = detail, Message = message, NotificationType = type };

    [Fact]
    public void Full_turn_with_permission_prompt()
    {
        var t = new ClaudeSessionTracker(_time);

        t.Apply(Hook("UserPromptSubmit"));
        var s = Assert.Single(t.Sessions);
        Assert.Equal("island", s.Project);
        Assert.Equal(ClaudeState.Thinking, s.State);

        t.Apply(Hook("PreToolUse", detail: "Running npm test"));
        Assert.Equal("Running npm test", s.Detail);
        Assert.True(s.IsWorking);

        var (_, needs) = t.Apply(Hook("Notification", message: "Claude needs your permission to use Bash", type: "permission_prompt"));
        Assert.Equal(ClaudeTransition.NeedsYou, needs);
        Assert.Same(s, t.Focus);

        // A repeated prompt for the same wait does not alert twice.
        Assert.Equal(ClaudeTransition.None, t.Apply(Hook("Notification", message: "Claude needs your permission to use Bash", type: "permission_prompt")).Transition);

        t.Apply(Hook("PostToolUse"));
        Assert.Equal(ClaudeState.Thinking, s.State);

        _time.Advance(TimeSpan.FromSeconds(95));
        var (_, done) = t.Apply(Hook("Stop"));
        Assert.Equal(ClaudeTransition.Finished, done);
        Assert.Equal("Done in 1m 35s", s.Detail);
        Assert.Null(t.Focus);

        t.Apply(Hook("SessionEnd"));
        Assert.Empty(t.Sessions);
    }

    [Fact]
    public void Idle_prompt_notification_is_not_a_permission_request()
    {
        var t = new ClaudeSessionTracker(_time);
        t.Apply(Hook("Stop"));
        var (_, tr) = t.Apply(Hook("Notification", message: "Claude is waiting for your input", type: "idle_prompt"));
        Assert.Equal(ClaudeTransition.None, tr);
        Assert.Equal(ClaudeState.Done, t.Sessions[0].State);
    }
}

public class UsageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 14, 20, 0, TimeSpan.Zero);

    private static UsageEntry Entry(DateTimeOffset t, long output = 1_000_000, string model = "claude-opus-5-5") =>
        new(t, model, 0, output, 0, 0, 0);

    [Fact]
    public void Parses_assistant_lines_and_ignores_others()
    {
        var line = """
            {"type":"assistant","timestamp":"2026-11-05T14:20:00Z","requestId":"r1","message":{"id":"m1","model":"claude-opus-5-5",
             "usage":{"input_tokens":4,"output_tokens":100,"cache_creation_input_tokens":200,"cache_read_input_tokens":300,
                      "cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":200}}}}
            """.ReplaceLineEndings("");
        var e = UsageEntry.TryParse(Encoding.UTF8.GetBytes(line), out var key);

        Assert.NotNull(e);
        Assert.Equal("m1:r1", key);
        Assert.Equal(200, e.CacheWrite1h);
        Assert.Equal(604, e.TotalTokens);

        Assert.Null(UsageEntry.TryParse("""{"type":"user","message":{"content":"hi"}}"""u8, out _));
        Assert.Null(UsageEntry.TryParse("""{"type":"assistant","usage" """u8, out _)); // truncated line
    }

    [Fact]
    public void Prices_use_the_longest_matching_model_prefix()
    {
        Assert.Equal(20m, ClaudePricing.Cost(Entry(T0, model: "claude-opus-5-5")));   // $20 / M output
        Assert.Equal(25m, ClaudePricing.Cost(Entry(T0, model: "claude-opus-5")));
        Assert.Equal(15m, ClaudePricing.Cost(Entry(T0, model: "claude-sonnet-4-5-20250929")));
        Assert.Equal(0m, ClaudePricing.Cost(Entry(T0, model: "some-other-model")));
    }

    [Fact]
    public void Duplicate_lines_count_once()
    {
        var calc = new UsageCalculator();
        calc.Add(Entry(T0), "m1:r1");
        calc.Add(Entry(T0), "m1:r1");
        Assert.Equal(1_000_000, calc.Summarize(T0.AddMinutes(1), TimeZoneInfo.Utc).TodayTokens);
    }

    [Fact]
    public void Five_hour_block_starts_on_the_hour_and_resets_after_a_gap()
    {
        var calc = new UsageCalculator();
        calc.Add(Entry(T0.AddHours(-8)), "old");            // earlier block, ended long ago
        calc.Add(Entry(T0), "a");                           // 14:20 → block 14:00-19:00
        calc.Add(Entry(T0.AddHours(2)), "b");               // 16:20, same block

        var s = calc.Summarize(T0.AddHours(3), TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero), s.BlockStart);
        Assert.Equal(2_000_000, s.BlockTokens);
        Assert.Equal(40m, s.BlockCost);

        var later = calc.Summarize(T0.AddHours(6), TimeZoneInfo.Utc); // 20:20, block over
        Assert.Null(later.BlockStart);
        Assert.Equal(0, later.BlockTokens);
    }
}

public class ClaudeHookInstallerTests
{
    private const string Cmd = "\"C:/apps/Atoll/Atoll.ClaudeHook.exe\"";

    [Fact]
    public void Install_keeps_existing_hooks_and_is_idempotent()
    {
        var root = JsonNode.Parse("""
            {"model":"opus","hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"my-guard.sh"}]}]}}
            """)!.AsObject();

        ClaudeHookInstaller.AddHooks(root, Cmd);
        ClaudeHookInstaller.RemoveHooks(root);
        ClaudeHookInstaller.AddHooks(root, Cmd);

        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal(ClaudeHookInstaller.Events.Length, ClaudeHookInstaller.OurCommands(root).Count());
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count);
        Assert.Equal("my-guard.sh", pre[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("*", pre[1]!["matcher"]!.GetValue<string>());
        Assert.Null(root["hooks"]!["Stop"]![0]!["matcher"]); // non-tool events take no matcher
    }

    [Fact]
    public void Remove_leaves_other_hooks_and_drops_empty_sections()
    {
        var root = JsonNode.Parse("""
            {"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"my-guard.sh"}]}]}}
            """)!.AsObject();
        ClaudeHookInstaller.AddHooks(root, Cmd);

        ClaudeHookInstaller.RemoveHooks(root);

        Assert.Empty(ClaudeHookInstaller.OurCommands(root));
        var hooks = root["hooks"]!.AsObject();
        Assert.Single(hooks);
        Assert.Equal("my-guard.sh", hooks["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }
}
