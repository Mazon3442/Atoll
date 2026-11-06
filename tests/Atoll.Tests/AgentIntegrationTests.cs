using System.Text.Json;
using Atoll.Claude;
using Atoll.Modules.Claude;
using Microsoft.Extensions.Time.Testing;

namespace Atoll.Tests;

public class AgentIntegrationTests
{
    [Fact]
    public void Sessions_disappear_when_their_agent_process_exits_without_a_final_event()
    {
        var running = new HashSet<int> { 4242 };
        var tracker = new ClaudeSessionTracker(new FakeTimeProvider(), running.Contains);
        tracker.Apply(new HookMessage { Agent = "hermes", SessionId = "h", Event = "UserPromptSubmit", AgentPid = 4242 });
        tracker.Apply(new HookMessage { Agent = "claude", SessionId = "c", Event = "UserPromptSubmit" }); // no pid reported
        Assert.True(tracker.Sessions.Single(s => s.Agent == "hermes").IsWorking);

        tracker.Tick();
        Assert.Equal(2, tracker.Sessions.Count);

        running.Clear(); // Hermes is closed mid-turn: no Stop, Interrupt or SessionEnd is ever sent
        tracker.Tick();
        Assert.Equal("claude", Assert.Single(tracker.Sessions).Agent);
        Assert.DoesNotContain(tracker.Sessions, s => s.Agent == "hermes");
    }

    [Fact]
    public void Hook_input_carries_the_agent_pid_only_when_it_is_a_positive_number()
    {
        static int Pid(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return HookProtocol.FromHookInput(doc.RootElement, 0, "hermes").AgentPid;
        }
        Assert.Equal(4242, Pid("""{"hook_event_name":"Stop","session_id":"s","agent_pid":4242}"""));
        Assert.Equal(0, Pid("""{"hook_event_name":"Stop","session_id":"s"}"""));
        Assert.Equal(0, Pid("""{"hook_event_name":"Stop","session_id":"s","agent_pid":"4242"}"""));
        Assert.Equal(0, Pid("""{"hook_event_name":"Stop","session_id":"s","agent_pid":-1}"""));
    }

    [Fact]
    public void Agents_with_matching_session_ids_stay_independent_and_do_not_report_interruptions_as_success()
    {
        var tracker = new ClaudeSessionTracker(new FakeTimeProvider());
        foreach (var agent in HookProtocol.Agents)
            tracker.Apply(new HookMessage { Agent = agent, SessionId = "same", Event = "UserPromptSubmit" });
        Assert.Equal(4, tracker.Sessions.Count);
        tracker.Apply(new HookMessage { Agent = "codex", SessionId = "same", Event = "PermissionRequest" });
        Assert.Equal("Codex", tracker.Focus!.AgentName);
        tracker.Apply(new HookMessage { Agent = "hermes", SessionId = "same", Event = "Interrupt" });
        Assert.False(tracker.Sessions.Single(s => s.Agent == "hermes").IsActive);
        Assert.True(tracker.Sessions.Single(s => s.Agent == "opencode").IsWorking);
        Assert.Equal(ClaudeTransition.Finished, tracker.Apply(new HookMessage { Agent = "opencode", SessionId = "same", Event = "Stop" }).Transition);
        Assert.Equal(ClaudeTransition.None, tracker.Apply(new HookMessage { Agent = "opencode", SessionId = "same", Event = "Stop" }).Transition);
        tracker.Apply(new HookMessage { Agent = "codex", SessionId = "same", Event = "SessionEnd" });
        Assert.Equal(3, tracker.Sessions.Count);
        Assert.Null(tracker.Apply(new HookMessage { Agent = "unknown", SessionId = "same", Event = "Stop" }).Session);
    }

    [Fact]
    public void Legacy_hooks_default_to_claude_and_provider_tool_inputs_are_summarized()
    {
        var legacy = JsonSerializer.Deserialize<HookMessage>("""{"event":"SessionStart","sessionId":"s1"}""", HookProtocol.Json)!;
        Assert.Equal("claude", legacy.Agent);
        using var json = JsonDocument.Parse("""
            {"hook_event_name":"PreToolUse","session_id":"s1","tool_name":"write",
             "tool_input":{"filePath":"C:\\code\\app.cs","content":"private file contents"}}
            """);
        var message = HookProtocol.FromHookInput(json.RootElement, 0, "opencode");
        Assert.Equal("Editing app.cs", message.Detail);
        Assert.DoesNotContain("private file contents", JsonSerializer.Serialize(message, HookProtocol.Json));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("opencode")]
    public async Task Installing_and_removing_preserves_other_integrations(string agent)
    {
        var dir = Path.Combine(Path.GetTempPath(), "atoll-agent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "Atoll.ClaudeHook.exe");
            File.WriteAllText(exe, "test executable placeholder");
            var installer = new AgentIntegrationInstaller(dir, exe);
            var path = installer.PathFor(agent);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var other = Path.Combine(Path.GetDirectoryName(path)!, "other-plugin.txt");
            File.WriteAllText(other, "keep me");
            if (agent != "opencode")
                File.WriteAllText(path, """{"setting":"keep","hooks":{"PreToolUse":[{"hooks":[{"type":"command","command":"my-guard"}]}]}}""");
            Assert.Null(await installer.InstallAsync(agent));
            Assert.Null(await installer.InstallAsync(agent));
            Assert.True(installer.IsInstalled(agent));
            if (agent != "opencode")
            {
                using var root = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Equal("keep", root.RootElement.GetProperty("setting").GetString());
                Assert.Equal(2, root.RootElement.GetProperty("hooks").GetProperty("PreToolUse").GetArrayLength());
            }
            Assert.Null(await installer.RemoveAsync(agent));
            Assert.False(installer.IsInstalled(agent));
            Assert.Equal("keep me", File.ReadAllText(other));
            if (agent != "opencode") Assert.Contains("my-guard", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("{ bad json")]
    [InlineData("{\"hooks\":[]}")]
    [InlineData("{\"hooks\":{\"Stop\":42}}")]
    public async Task Invalid_existing_hook_settings_are_left_unchanged(string original)
    {
        var dir = Path.Combine(Path.GetTempPath(), "atoll-agent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "Atoll.ClaudeHook.exe");
            File.WriteAllText(exe, "test executable placeholder");
            var installer = new AgentIntegrationInstaller(dir, exe);
            var path = installer.PathFor("codex");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, original);
            Assert.NotNull(await installer.InstallAsync("codex"));
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Adapter_assets_are_packaged_and_executable_paths_are_escaped()
    {
        var installer = new AgentIntegrationInstaller(hookExe: "C:\\Apps & tools\\Atoll.ClaudeHook.exe");
        foreach (var agent in new[] { "opencode", "hermes" })
        {
            var script = installer.Script(agent);
            Assert.Contains("ATOLL agent integration", script);
            Assert.DoesNotContain("__ATOLL_HOOK_EXE__", script);
            Assert.Contains("Apps", script);
        }
    }

    [Fact]
    public void Hook_setup_does_not_overwrite_a_concurrent_edit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atoll-agent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "hooks.json");
        try
        {
            File.WriteAllText(path, "{}");
            const string edited = "{\"changedElsewhere\":true}";
            Assert.NotNull(ClaudeHookInstaller.Update(_ => File.WriteAllText(path, edited), path));
            Assert.Equal(edited, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
