using Atoll.Modules.Clock;
using Atoll.Modules.Media;
using Microsoft.Extensions.Time.Testing;

namespace Atoll.Tests;

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
    public void Pomodoro_uses_saved_lengths_and_break_cadence()
    {
        var settings = new Atoll.Settings.PomodoroSettings
        {
            FocusMinutes = 40,
            ShortBreakMinutes = 2,
            LongBreakMinutes = 8,
            SessionsBeforeLongBreak = 2,
        };
        var timer = new TimerViewModel(_time, settings);
        timer.StartPomodoroCommand.Execute(null);
        Assert.Equal("40:00", timer.Display);

        _time.Advance(TimeSpan.FromMinutes(40) + TimeSpan.FromMilliseconds(1));
        timer.Tick();
        Assert.Equal("Break", timer.Label);
        Assert.Equal("2:00", timer.Display);

        _time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromMilliseconds(1));
        timer.Tick();
        Assert.Equal("Focus", timer.Label);
        Assert.Equal("40:00", timer.Display);

        _time.Advance(TimeSpan.FromMinutes(40) + TimeSpan.FromMilliseconds(1));
        timer.Tick();
        Assert.Equal("Long break", timer.Label);
        Assert.Equal("8:00", timer.Display);
    }

    [Fact]
    public void Pomodoro_settings_clamp_and_save()
    {
        var settings = new Atoll.Settings.PomodoroSettings();
        var saved = 0;
        var timer = new TimerViewModel(_time);
        timer.UsePomodoroSettings(settings, () => saved++);

        timer.FocusMinutes = 40;
        Assert.Equal(40, settings.FocusMinutes);
        Assert.Equal("Start 40 min focus", timer.PomodoroStartLabel);
        Assert.Equal(1, saved);

        timer.ShortBreakMinutes = 0;
        Assert.Equal(1, settings.ShortBreakMinutes);

        timer.SessionsBeforeLongBreak = 99;
        Assert.Equal(12, settings.SessionsBeforeLongBreak);

        timer.FocusMinutes = 999;
        Assert.Equal(120, settings.FocusMinutes);
    }

    [Fact]
    public void Clock_panel_forwards_timer_section_changes()
    {
        var timer = new TimerViewModel(_time);
        var panel = new ClockPanelViewModel(new ClockViewModel(), timer);
        var changed = new List<string?>();
        ((System.ComponentModel.INotifyPropertyChanged)panel).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        timer.SectionIndex = 1;

        Assert.Contains(nameof(TimerViewModel.SectionIndex), changed);
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
    [InlineData("com.wimpysworld.sidra", "Sidra")]
    [InlineData("org.mozilla.Firefox", "Firefox")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Microsoft.ZuneMusic")]
    [InlineData("308046B0AF4A39CB", "308046B0AF4A39CB")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Media_app_names_are_shortened(string? aumid, string expected) =>
        Assert.Equal(expected, MediaModule.FriendlyAppName(aumid));
}
