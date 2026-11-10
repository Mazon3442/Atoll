using System.Windows.Media;
using Atoll.Activities;
using Atoll.Modules.Claude;
using Atoll.Modules.Clock;
using Atoll.Modules.Indicators;
using Atoll.Shell;

namespace Atoll.Tests;

public sealed class SideChipTests
{
    [Fact]
    public void Timer_is_a_circle_with_time_left()
    {
        var timer = new TimerViewModel(TimeProvider.System);
        timer.StartCountdownCommand.Execute(25);
        using var chip = new SideChipViewModel(
            new Activity("clock.timer", "clock", timer, ActivityPriority.Normal), Colors.White);

        Assert.True(chip.IsRing);
        Assert.Equal("25:00", chip.RingText);
        Assert.Equal(1, chip.RingProgress, 3);
        Assert.False(chip.ShowLabel);

        chip.SetWide(true);
        Assert.False(chip.ShowLabel); // the ring never carries a separate label
    }

    [Fact]
    public void Claude_plan_limit_shows_percent_left_in_the_ring()
    {
        var claude = new ClaudeViewModel(new ClaudeSessionTracker(TimeProvider.System));
        claude.Usage.SetLimits([new PlanLimit("5-hour", 42, null)], DateTimeOffset.Now);
        using var chip = new SideChipViewModel(
            new Activity("claude.work", "claude", claude, ActivityPriority.Normal), Colors.White);

        Assert.True(chip.IsRing);
        Assert.Equal("58%", chip.RingText);
        Assert.Contains("5-hour", chip.Label);
        Assert.Contains("58% left", chip.Label);
    }

    [Fact]
    public void Dots_hide_the_label_and_wide_capsules_show_it()
    {
        using var chip = new SideChipViewModel(
            new Activity("weather.cpu", "weather", new IndicatorViewModel("", "CPU 34%"), ActivityPriority.Ambient), Colors.White);

        Assert.False(chip.IsRing);
        chip.SetWide(false);
        Assert.False(chip.ShowLabel);
        chip.SetWide(true);
        Assert.True(chip.ShowLabel);
    }
}
