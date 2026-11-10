using Atoll.Updates;

namespace Atoll.Tests;

public sealed class UpdatePromptTests
{
    [Fact]
    public void Restart_dismisses_the_prompt_then_restarts()
    {
        var steps = new List<string>();
        var prompt = new UpdatePromptViewModel("1.4.0", () => steps.Add("restart"), () => steps.Add("dismiss"));

        Assert.Equal("ATOLL 1.4.0 is ready", prompt.Title);
        prompt.RestartCommand.Execute(null);

        Assert.Equal(["dismiss", "restart"], steps);
    }

    [Fact]
    public void Later_only_dismisses_so_the_update_applies_on_the_next_start()
    {
        var steps = new List<string>();
        var prompt = new UpdatePromptViewModel("1.4.0", () => steps.Add("restart"), () => steps.Add("dismiss"));

        prompt.LaterCommand.Execute(null);

        Assert.Equal(["dismiss"], steps);
    }
}
