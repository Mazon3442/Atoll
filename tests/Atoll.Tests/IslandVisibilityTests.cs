using System.Windows;
using Atoll.Activities;
using Atoll.Settings;
using Atoll.Shell;

namespace Atoll.Tests;

public class IslandVisibilityTests
{
    private static readonly Int32Rect Monitor = new(0, 0, 1920, 1080);

    [Theory]
    // F11 or a borderless game: not maximized, covers the monitor.
    [InlineData(0, 0, 1920, 1080, false, true)]
    // A browser video that went fullscreen from a maximized window keeps the maximized flag.
    [InlineData(0, 0, 1920, 1080, true, true)]
    // An ordinary maximized window stops at the taskbar...
    [InlineData(-8, -8, 1928, 1040, true, false)]
    // ...or, with an auto-hidden taskbar, overhangs the monitor by its frame.
    [InlineData(-8, -8, 1928, 1088, true, false)]
    // A window that does not cover the monitor.
    [InlineData(100, 100, 900, 700, false, false)]
    public void Fullscreen_windows_are_told_apart_from_maximized_ones(int left, int top, int right, int bottom, bool maximized, bool expected) =>
        Assert.Equal(expected, FullscreenWatcher.IsFullscreen(left, top, right, bottom, Monitor, maximized));

    [Fact]
    public void Maximized_windows_follow_their_own_setting()
    {
        var settings = new AppSettings { FullscreenMode = FullscreenMode.Stay, MaximizedMode = FullscreenMode.PopUp };
        Assert.Equal(FullscreenMode.PopUp, IslandWindow.ModeFor(ForegroundKind.Maximized, settings));
        Assert.Equal(FullscreenMode.Stay, IslandWindow.ModeFor(ForegroundKind.Fullscreen, settings));
        Assert.Equal(FullscreenMode.Hide, IslandWindow.ModeFor(ForegroundKind.Game, settings));
        Assert.Equal(FullscreenMode.Stay, IslandWindow.ModeFor(ForegroundKind.Normal, settings));
    }

    [Fact]
    public void Pop_up_mode_shows_only_notifications_and_alerts()
    {
        var steady = new Activity("now", "media", new object(), ActivityPriority.Normal);
        var notification = new Activity("n1", "notifications", new object(), ActivityPriority.Normal, TimeSpan.FromSeconds(5));
        Assert.False(IslandWindow.ShownWithoutReveal(FullscreenMode.PopUp, false, null, false));
        Assert.False(IslandWindow.ShownWithoutReveal(FullscreenMode.PopUp, false, steady, false));
        Assert.True(IslandWindow.ShownWithoutReveal(FullscreenMode.PopUp, false, notification, false));
        Assert.False(IslandWindow.ShownWithoutReveal(FullscreenMode.Hide, false, notification, false));
    }

    [Fact]
    public void Hide_when_idle_hides_only_an_empty_closed_island()
    {
        var steady = new Activity("now", "media", new object(), ActivityPriority.Normal);
        Assert.True(IslandWindow.ShownWithoutReveal(FullscreenMode.Stay, false, null, false));
        Assert.False(IslandWindow.ShownWithoutReveal(FullscreenMode.Stay, true, null, false));
        Assert.True(IslandWindow.ShownWithoutReveal(FullscreenMode.Stay, true, steady, false));
        Assert.True(IslandWindow.ShownWithoutReveal(FullscreenMode.Stay, true, null, open: true));
        // The clock is always posted at idle priority; on its own it counts as nothing to show.
        var clock = new Activity("time", "clock", new object(), ActivityPriority.Idle);
        Assert.False(IslandWindow.ShownWithoutReveal(FullscreenMode.Stay, true, clock, false));
        Assert.True(IslandWindow.ShownWithoutReveal(FullscreenMode.Stay, false, clock, false));
    }
}
