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

    [Theory]
    [InlineData("24", 24 * 60)]
    [InlineData("17.5", 17.5 * 60)]
    [InlineData(" 90 ", 90 * 60)]
    [InlineData("0.5", 30)]
    public void Custom_lengths_are_minutes(string text, double seconds)
    {
        Assert.True(TimerViewModel.TryParseLength(text, out var length));
        Assert.Equal(TimeSpan.FromSeconds(seconds), length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1:30")] // a colon means a time of day, never a length
    [InlineData("1441")]
    [InlineData("NaN")]
    public void Nonsense_lengths_are_refused(string text) => Assert.False(TimerViewModel.TryParseLength(text, out _));

    [Fact]
    public void A_custom_timer_starts_from_typed_minutes_and_reports_bad_input()
    {
        var timer = new TimerViewModel(_time) { EntryText = "17" };
        timer.StartEntryCommand.Execute(null);
        Assert.Equal("17:00", timer.Display);
        Assert.Equal("", timer.EntryText);
        Assert.False(timer.IsAlarm);

        timer.ResetCommand.Execute(null);
        timer.EntryText = "soon";
        timer.StartEntryCommand.Execute(null);
        Assert.False(timer.IsActive);
        Assert.True(timer.HasMessage);
    }

    [Theory]
    // 4:00 pm now: a bare 5:36 means 5:36 pm today; 17:36 and 5:36 pm are exact; 3:15 is the next 3:15 (am, tomorrow).
    [InlineData("5:36", "2026-11-12T17:36")]
    [InlineData("17:36", "2026-11-12T17:36")]
    [InlineData("5:36 pm", "2026-11-12T17:36")]
    [InlineData("5:36 am", "2026-11-13T05:36")]
    [InlineData("3:15", "2026-11-13T03:15")]
    [InlineData("4:00 pm", "2026-11-13T16:00")] // right now has passed: tomorrow
    [InlineData("0:30", "2026-11-13T00:30")]
    public void An_alarm_is_the_next_time_the_clock_shows_that_time(string text, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 12, 16, 0, 0, TimeSpan.Zero);
        Assert.True(TimerViewModel.TryNextTime(text, now, TimeZoneInfo.Utc, out var at));
        Assert.Equal(DateTimeOffset.Parse(expected + ":00+00:00"), at);
    }

    [Theory]
    [InlineData("")]
    [InlineData("later")]
    [InlineData("25:00")]
    public void Nonsense_alarm_times_are_refused(string text) =>
        Assert.False(TimerViewModel.TryNextTime(text, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, out _));

    [Fact]
    public void An_alarm_goes_off_at_its_time_even_after_the_clock_jumps_and_cannot_be_paused()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 12, 16, 0, 0, TimeSpan.Zero));
        var timer = new TimerViewModel(_time) { EntryText = "4:30 pm" };
        var done = new List<(string, string)>();
        timer.Completed += (title, detail) => done.Add((title, detail));

        timer.StartEntryCommand.Execute(null);
        Assert.True(timer.IsAlarm);
        Assert.Equal("30:00", timer.Display);
        Assert.StartsWith("Alarm", timer.Label);

        timer.PauseResumeCommand.Execute(null); // ignored
        Assert.True(timer.IsRunning);

        timer.AddMinuteCommand.Execute(null);
        Assert.Equal("31:00", timer.Display);

        _time.Advance(TimeSpan.FromMinutes(31) + TimeSpan.FromSeconds(1));
        timer.Tick();
        Assert.Equal("Alarm", Assert.Single(done).Item1);
        Assert.False(timer.IsActive);
        Assert.False(timer.IsAlarm);
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
