using Atoll.Modules;
using Microsoft.Extensions.Time.Testing;

namespace Atoll.Tests;

public sealed class FocusServiceTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Timed_focus_expires_and_clear_turns_it_off()
    {
        var focus = new FocusService(_time);
        Assert.False(focus.IsActive);

        focus.FocusFor(TimeSpan.FromHours(1));
        Assert.True(focus.IsActive);
        Assert.Contains("Do Not Disturb", focus.Status);

        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.True(focus.IsActive);
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(focus.IsActive);

        focus.FocusIndefinitely();
        Assert.True(focus.IsActive);
        focus.Clear();
        Assert.False(focus.IsActive);
    }

    [Fact]
    public void Windows_focus_is_mirrored_only_when_both_switches_allow_it()
    {
        var focus = new FocusService(_time);
        focus.SetWindowsFocus(true);
        Assert.True(focus.IsActive);

        focus.Configure(false, followWindows: true);
        Assert.False(focus.IsActive);

        focus.Configure(true, followWindows: false);
        Assert.False(focus.IsActive);

        focus.Configure(true, followWindows: true);
        Assert.True(focus.IsActive);

        focus.SetWindowsFocus(false);
        Assert.False(focus.IsActive);
    }

    [Fact]
    public void Tick_expires_a_window_and_raises_the_public_state()
    {
        var focus = new FocusService(_time);
        var raised = 0;
        focus.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FocusService.IsActive)) raised++; };

        focus.FocusFor(TimeSpan.FromMinutes(5));
        Assert.True(raised > 0);
        _time.Advance(TimeSpan.FromMinutes(6));
        focus.Tick();

        Assert.False(focus.IsActive);
        Assert.Null(focus.Until);
        Assert.True(raised > 1);
    }
}
