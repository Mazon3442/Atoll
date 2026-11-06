using Island.Modules.Clock;
using Island.Modules.Media;
using Microsoft.Extensions.Time.Testing;

namespace Island.Tests;

public class TimerViewModelTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Countdown_completes_once_and_resets()
    {
        var timer = new TimerViewModel(_time);
        var done = new List<string>();
        var activeChanges = 0;
        timer.Completed += (title, _) => done.Add(title);
        timer.ActiveChanged += () => activeChanges++;

        timer.StartCountdownCommand.Execute(1.0);
        Assert.Equal("1:00", timer.Display);

        _time.Advance(TimeSpan.FromSeconds(59.5));
        timer.Tick();
        Assert.Equal("0:01", timer.Display); // rounds up; never shows 0:00 early
        Assert.Empty(done);

        _time.Advance(TimeSpan.FromSeconds(1));
        timer.Tick();
        timer.Tick();
        Assert.Equal(["Timer done"], done);
        Assert.False(timer.IsActive);
        Assert.Equal(2, activeChanges); // became active, then inactive
    }

    [Fact]
    public void Pause_freezes_remaining_time()
    {
        var timer = new TimerViewModel(_time);
        timer.StartCountdownCommand.Execute(5.0);
        _time.Advance(TimeSpan.FromMinutes(1));
        timer.PauseResumeCommand.Execute(null);

        _time.Advance(TimeSpan.FromMinutes(10));
        timer.Tick();
        Assert.Equal("4:00", timer.Display);
        Assert.False(timer.IsRunning);

        timer.PauseResumeCommand.Execute(null);
        _time.Advance(TimeSpan.FromMinutes(1));
        timer.Tick();
        Assert.Equal("3:00", timer.Display);
    }

    [Fact]
    public void Pomodoro_alternates_focus_and_breaks_with_long_break_every_fourth()
    {
        var timer = new TimerViewModel(_time);
        var labels = new List<string>();
        timer.StartPomodoroCommand.Execute(null);

        for (var i = 0; i < 8; i++)
        {
            labels.Add(timer.Label);
            _time.Advance(timer.Remaining + TimeSpan.FromMilliseconds(1));
            timer.Tick();
        }

        Assert.Equal(["Focus", "Break", "Focus", "Break", "Focus", "Break", "Focus", "Long break"], labels);
        Assert.True(timer.IsActive);
    }

    [Fact]
    public void Stopwatch_counts_up()
    {
        var timer = new TimerViewModel(_time);
        timer.StartStopwatchCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(75));
        timer.Tick();
        Assert.Equal("1:15", timer.Display);
    }

    [Theory]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Media_app_names_are_shortened(string? aumid, string expected) =>
        Assert.Equal(expected, MediaModule.FriendlyAppName(aumid));
}
