using Atoll.Modules.Controls;
using Microsoft.Extensions.Time.Testing;

namespace Atoll.Tests;

public class ConfirmPressTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void The_first_press_arms_and_the_second_runs()
    {
        var confirm = new ConfirmPress(_time);
        Assert.False(confirm.Press("restart"));
        Assert.Equal("restart", confirm.Armed);
        Assert.True(confirm.Press("restart"));
        Assert.Null(confirm.Armed);
    }

    [Fact]
    public void Arming_expires_after_the_window()
    {
        var confirm = new ConfirmPress(_time, TimeSpan.FromSeconds(3));
        confirm.Press("sleep");
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.Null(confirm.Armed);
        Assert.False(confirm.Press("sleep")); // arms again instead of running
        Assert.Equal("sleep", confirm.Armed);
    }

    [Fact]
    public void Pressing_another_action_moves_the_arming()
    {
        var confirm = new ConfirmPress(_time);
        confirm.Press("restart");
        Assert.False(confirm.Press("shutdown"));
        Assert.Equal("shutdown", confirm.Armed);
        Assert.False(confirm.Press("restart")); // restart is no longer armed
    }
}
