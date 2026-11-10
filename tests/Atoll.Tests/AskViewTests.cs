using Atoll.Modules.Claude;
using Atoll.Settings;

namespace Atoll.Tests;

public sealed class AskViewTests
{
    [Fact]
    public async Task Sending_keeps_what_you_typed_in_the_box()
    {
        using var vm = new AskViewModel(new FakeChat(), () => new AgentChatOptions(new AgentChatSettings(), []));
        vm.Draft = "hello";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("hello", vm.Draft); // the question stays put so it can be edited or asked again
        Assert.Equal("Hello back.", vm.Answer);
        Assert.Equal("Claude Code", vm.Provider);
        Assert.True(vm.IsEmpty == false);
        Assert.False(vm.HasError);
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
    public void The_empty_state_offers_three_questions_that_fill_the_box()
    {
        using var vm = new AskViewModel();

        Assert.True(vm.IsEmpty);
        Assert.Equal(3, vm.Suggestions.Count);
        Assert.Empty(vm.Draft);

        vm.UseSuggestionCommand.Execute(vm.Suggestions[1]);

        Assert.Equal("How does Ask AI work?", vm.Draft);
        Assert.True(vm.HasDraft);
        Assert.True(vm.IsEmpty); // typing alone does not replace the empty state
    }

    [Fact]
    public void The_title_names_who_answers_once_an_agent_has_replied()
    {
        using var vm = new AskViewModel();
        Assert.Equal("Ask anything", vm.Title);

        vm.Provider = "Claude Code";
        Assert.Equal("Ask Claude Code anything", vm.Title);

        vm.Note = "chat only";
        Assert.Equal("Claude Code · chat only", vm.SourceLine);
        Assert.True(vm.HasStatusLine);
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
