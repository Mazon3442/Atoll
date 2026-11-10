using Atoll.Modules;
using Atoll.Modules.Claude;
using Atoll.Settings;

namespace Atoll.Tests;

public sealed class AskViewTests
{
    [Fact]
    public void Opening_a_panel_tells_its_module()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        using var host = new Atoll.Activities.ActivityHost();
        var module = new Watcher();
        using var manager = new ModuleManager(
            [new ModuleDescriptor("watcher", "Watcher", "?", () => module)], host, System.Windows.Threading.Dispatcher.CurrentDispatcher, service);
        manager.Apply();

        manager.NotifyPanelShown("watcher");
        Assert.True(module.Shown);

        manager.NotifyPanelHidden("watcher");
        Assert.True(module.Hidden);
    }

    private sealed class Watcher : IIslandModule
    {
        public object? Panel => null;
        public bool Shown { get; private set; }
        public bool Hidden { get; private set; }
        public void Start(ModuleContext context) { }
        public void OnPanelShown() => Shown = true;
        public void OnPanelHidden() => Hidden = true;
        public void Dispose() { }
    }

    [Fact]
    public void Choosing_a_source_switches_the_model_list()
    {
        var settings = new AgentChatSettings();
        var saves = 0;
        using var vm = new AskViewModel(options: () => new AgentChatOptions(settings, []), save: () => saves++);

        Assert.Equal("Auto", vm.SourceLabel);
        Assert.Equal(AgentChatKind.ClaudeCode, vm.ModelKind);
        Assert.Contains(vm.Models, m => m.Value == "sonnet");

        vm.ChooseSourceCommand.Execute(vm.Sources.Single(s => s.Value == "codex"));

        Assert.Equal("Codex", vm.SourceLabel);
        Assert.Equal(AgentChatKind.Codex, vm.ModelKind);
        Assert.Equal("codex", settings.Preferred);
        Assert.DoesNotContain(vm.Models, m => m.Value == "sonnet"); // Claude aliases would be wrong here
        Assert.True(saves > 0);
    }

    [Fact]
    public void A_saved_key_offers_models_for_that_provider()
    {
        var settings = new AgentChatSettings { Preferred = "api:deep" };
        var account = new ProviderAccount { Id = "deep", Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1/" };
        using var vm = new AskViewModel(options: () => new AgentChatOptions(settings, [account]));

        Assert.Equal("DeepSeek", vm.SourceLabel);
        Assert.Contains(vm.Models, m => m.Value == "deepseek-chat");
        Assert.Contains(vm.Models, m => m.Value == "deepseek-reasoner");
    }

    [Fact]
    public void The_tools_switch_is_saved()
    {
        var settings = new AgentChatSettings();
        var saves = 0;
        using var vm = new AskViewModel(options: () => new AgentChatOptions(settings, []), save: () => saves++);

        Assert.True(vm.AllowTools); // on by default
        vm.AllowTools = false;

        Assert.False(settings.AllowTools);
        Assert.True(saves > 0);
    }

    [Theory]
    [InlineData("", "claude,codex,opencode")]
    [InlineData("claude", "claude,codex,opencode")]
    [InlineData("codex", "codex,claude,opencode")]
    [InlineData("opencode", "opencode,claude,codex")]
    public void The_chosen_agent_is_tried_first(string preferred, string expected) =>
        Assert.Equal(expected, string.Join(",", AgentChatService.Order(preferred)));

    [Fact]
    public async Task Sending_empties_the_box()
    {
        using var vm = new AskViewModel(new FakeChat(), () => new AgentChatOptions(new AgentChatSettings(), []));
        vm.Draft = "hello";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("", vm.Draft);
        Assert.Equal("Hello back.", vm.Answer);
        Assert.Equal("Claude Code", vm.Provider);
        Assert.True(vm.IsEmpty == false);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task The_answer_types_out_and_ends_complete_when_motion_is_on()
    {
        var shown = new List<string>();
        using var vm = new AskViewModel(new FakeChat(), () => new AgentChatOptions(new AgentChatSettings(), []), animate: () => true);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AskViewModel.ShownAnswer)) shown.Add(vm.ShownAnswer); };
        vm.Draft = "hi";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("Hello back.", vm.ShownAnswer);
        Assert.Contains(shown, text => text.Length > 0 && text.Length < "Hello back.".Length); // it was partial on the way
    }

    [Fact]
    public async Task A_reply_that_announces_itself_leaves_the_empty_state()
    {
        using var vm = new AskViewModel(new FakeChat(), () => new AgentChatOptions(new AgentChatSettings(), []));
        Assert.True(vm.IsEmpty);

        vm.Draft = "hi";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(vm.IsEmpty);
        Assert.True(vm.HasAnswer);
    }

    private sealed class FakeChat : IAgentChat
    {
        public Task<AgentChatSelection> ResolveAsync(AgentChatOptions options, CancellationToken ct = default) =>
            Task.FromResult(new AgentChatSelection(new FakeProvider(), "Chat only."));

        public async IAsyncEnumerable<AgentChatDelta> StreamAsync(AgentChatSelection selection, string prompt,
            AgentChatSettings settings, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new AgentChatDelta("Hello ");
            await Task.Yield();
            yield return new AgentChatDelta("back.");
        }
    }

    private sealed class FakeProvider : IAgentChatProvider
    {
        public string Name => "Claude Code";
        public AgentChatKind Kind => AgentChatKind.ClaudeCode;

        public async IAsyncEnumerable<AgentChatDelta> StreamAsync(string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }
    }

    [Fact]
    public void Clearing_goes_back_to_the_empty_state()
    {
        using var vm = new AskViewModel();
        vm.Provider = "Codex";
        vm.Answer = "42";
        Assert.False(vm.IsEmpty);

        vm.ClearCommand.Execute(null);

        Assert.True(vm.IsEmpty);
        Assert.Equal("", vm.Answer);
    }
}
