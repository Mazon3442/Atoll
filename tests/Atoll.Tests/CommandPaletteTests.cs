using Atoll.Shell;

namespace Atoll.Tests;

public sealed class CommandPaletteTests
{
    private static CommandPaletteViewModel Create(out List<string> ran)
    {
        var executed = new List<string>();
        ran = executed;
        var items = new List<CommandItem>
        {
            new("Agents", "Section", "\uE8D6", "", CommandKind.Tab, () => executed.Add("agents")),
            new("Media", "Section", "\uE8D6", "", CommandKind.Tab, () => executed.Add("media")),
            new("Screenshot", "Action", "\uE722", "", CommandKind.Action, () => executed.Add("screenshot")),
            new("Start a 5 minute timer", "Timer", "\uE916", "", CommandKind.Timer, () => executed.Add("timer")),
            new("Visual Studio Code", "Development", "", "V", CommandKind.App, () => executed.Add("vscode")),
        };
        return new CommandPaletteViewModel(() => items);
    }

    [Fact]
    public void Palette_starts_with_tabs_then_actions_then_timers_then_apps()
    {
        var vm = Create(out _);
        vm.Refresh();

        Assert.Equal(["Agents", "Media", "Screenshot", "Start a 5 minute timer", "Visual Studio Code"],
            vm.Results.Select(item => item.Title));
        Assert.Same(vm.Results[0], vm.Selected);
    }

    [Fact]
    public void Typing_filters_and_ranks_prefix_matches_first()
    {
        var vm = Create(out _);
        vm.Refresh();

        vm.Query = "med";
        Assert.Equal(["Media"], vm.Results.Select(item => item.Title));

        vm.Query = "st";
        Assert.Equal(["Start a 5 minute timer", "Visual Studio Code"], vm.Results.Select(item => item.Title));

        vm.Query = "scr";
        Assert.Equal(["Screenshot"], vm.Results.Select(item => item.Title));

        vm.Query = "development";
        Assert.Equal(["Visual Studio Code"], vm.Results.Select(item => item.Title));

        vm.Query = "zzz";
        Assert.Empty(vm.Results);
        Assert.False(vm.HasResults);
    }

    [Fact]
    public void Enter_closes_the_palette_before_running_the_command()
    {
        var vm = Create(out var ran);
        vm.Refresh();
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.Query = "screenshot";
        vm.RunSelected();

        Assert.True(closed);
        Assert.Equal(["screenshot"], ran);
    }
}
