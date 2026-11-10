using Atoll.Modules.Claude;

namespace Atoll.Tests;

public sealed class AskViewTests
{
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
