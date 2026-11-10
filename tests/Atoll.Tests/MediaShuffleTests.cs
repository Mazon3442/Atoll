using Atoll.Modules.Media;

namespace Atoll.Tests;

public sealed class MediaShuffleTests
{
    private static MediaViewModel Create(List<bool> shuffles, List<MediaRepeat> repeats) =>
        new(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask,
            on => { shuffles.Add(on); return Task.CompletedTask; },
            mode => { repeats.Add(mode); return Task.CompletedTask; });

    [Fact]
    public void Shuffle_asks_the_player_for_the_opposite_of_what_it_reports()
    {
        List<bool> shuffles = [];
        var vm = Create(shuffles, []);
        vm.CanShuffle = true;

        vm.ToggleShuffleCommand.Execute(null);
        vm.IsShuffling = true; // the player reports back
        vm.ToggleShuffleCommand.Execute(null);

        Assert.Equal([true, false], shuffles);
    }

    [Fact]
    public void Repeat_goes_off_then_all_then_one_then_off()
    {
        List<MediaRepeat> repeats = [];
        var vm = Create([], repeats);
        vm.CanRepeat = true;

        foreach (var reported in new[] { MediaRepeat.All, MediaRepeat.One, MediaRepeat.Off })
        {
            vm.CycleRepeatCommand.Execute(null);
            vm.Repeat = reported;
        }

        Assert.Equal([MediaRepeat.All, MediaRepeat.One, MediaRepeat.Off], repeats);
        Assert.False(vm.IsRepeating);
    }

    [Fact]
    public void The_buttons_stay_disabled_until_the_player_offers_them()
    {
        var vm = Create([], []);

        Assert.False(vm.ToggleShuffleCommand.CanExecute(null));
        Assert.False(vm.CycleRepeatCommand.CanExecute(null));
        vm.CanShuffle = true;
        vm.CanRepeat = true;
        Assert.True(vm.ToggleShuffleCommand.CanExecute(null));
        Assert.True(vm.CycleRepeatCommand.CanExecute(null));
    }
}
