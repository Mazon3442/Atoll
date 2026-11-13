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
    public void The_custom_length_steps_clamps_and_starts()
    {
        var timer = new TimerViewModel(_time);
        Assert.Equal(15, timer.CustomMinutes);

        timer.CustomMinutes = 24; // typed
        timer.SubtractCustomMinuteCommand.Execute(null);
        timer.AddCustomMinuteCommand.Execute(null);
        Assert.Equal(24, timer.CustomMinutes);
        timer.CustomMinutes = 0;
        Assert.Equal(1, timer.CustomMinutes);
        timer.CustomMinutes = 99999;
        Assert.Equal(1440, timer.CustomMinutes);

        timer.CustomMinutes = 17;
        timer.StartCustomCommand.Execute(null);
        Assert.Equal("17:00", timer.Display);
        Assert.False(timer.IsAlarm);
    }

    [Theory]
    // 4:00 pm now: later today, else tomorrow.
    [InlineData(17, 36, "2026-11-12T17:36")]
    [InlineData(5, 36, "2026-11-13T05:36")]
    [InlineData(16, 0, "2026-11-13T16:00")] // right now has passed
    [InlineData(0, 30, "2026-11-13T00:30")]
    public void An_alarm_is_the_next_time_the_clock_shows_that_time(int hour, int minute, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 12, 16, 0, 0, TimeSpan.Zero);
        Assert.Equal(DateTimeOffset.Parse(expected + ":00+00:00"),
            TimerViewModel.NextOccurrence(new TimeOnly(hour, minute), now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void The_alarm_picker_wraps_hours_and_minutes_and_flips_am_pm()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 12, 16, 2, 0, TimeSpan.Zero));
        var timer = new TimerViewModel(_time);

        // Starts at the next five minutes: 4:05 PM, which rings today.
        Assert.Equal(("4", "05", 1), (timer.AlarmHourText, timer.AlarmMinuteText, timer.AlarmPmIndex));
        Assert.StartsWith("Rings today at", timer.AlarmSummary);

        for (var i = 0; i < 8; i++) timer.AddAlarmHourCommand.Execute(null); // 4 PM + 8 hours = 12 AM
        Assert.Equal(("12", 0), (timer.AlarmHourText, timer.AlarmPmIndex));
        timer.AddAlarmHourCommand.Execute(null);
        Assert.Equal(("1", 0), (timer.AlarmHourText, timer.AlarmPmIndex));

        timer.SubtractAlarmMinuteCommand.Execute(null);
        timer.SubtractAlarmMinuteCommand.Execute(null);
        timer.SubtractAlarmMinuteCommand.Execute(null);
        timer.SubtractAlarmMinuteCommand.Execute(null);
        timer.SubtractAlarmMinuteCommand.Execute(null);
        timer.SubtractAlarmMinuteCommand.Execute(null);
        Assert.Equal(("1", "59", 0), (timer.AlarmHourText, timer.AlarmMinuteText, timer.AlarmPmIndex)); // minutes wrap, the hour stays
        Assert.StartsWith("Rings tomorrow at", timer.AlarmSummary);

        timer.AlarmPmIndex = 1;
        Assert.Equal(("1", 1), (timer.AlarmHourText, timer.AlarmPmIndex));
    }

    [Fact]
    public void The_alarm_picker_sets_any_hour_and_minute_in_two_taps()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 12, 16, 2, 0, TimeSpan.Zero));
        var timer = new TimerViewModel(_time);

        timer.AlarmPmIndex = 0;
        timer.AlarmHour12 = 7;
        timer.AlarmMinute = 35;
        Assert.Equal(("7", "35", 0), (timer.AlarmHourText, timer.AlarmMinuteText, timer.AlarmPmIndex));
        Assert.StartsWith("Rings tomorrow at", timer.AlarmSummary);

        timer.AlarmPmIndex = 1;
        timer.AlarmHour12 = 12; // 12 PM is noon, not midnight
        Assert.Equal(12, timer.AlarmHour12);
        Assert.Equal(1, timer.AlarmPmIndex);
        Assert.Equal(12, new TimeOnly(12, 35).Hour);
        Assert.Equal(["1", "12"], [timer.AlarmHours[0].ToString(), timer.AlarmHours[^1].ToString()]);
        Assert.Equal(12, timer.AlarmMinutes.Count);
    }

    [Fact]
    public void An_alarm_goes_off_at_its_time_and_cannot_be_paused()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 12, 16, 0, 0, TimeSpan.Zero));
        var timer = new TimerViewModel(_time);
        var done = new List<string>();
        timer.Completed += (title, _) => done.Add(title);
        // Picker starts at 4:05 PM; move to 4:30 PM.
        for (var i = 0; i < 25; i++) timer.AddAlarmMinuteCommand.Execute(null);

        timer.StartAlarmCommand.Execute(null);
        Assert.True(timer.IsAlarm);
        Assert.Equal("30:00", timer.Display);
        Assert.StartsWith("Alarm", timer.Label);

        timer.PauseResumeCommand.Execute(null); // ignored
        Assert.True(timer.IsRunning);
        timer.AddMinuteCommand.Execute(null);
        Assert.Equal("31:00", timer.Display);

        _time.Advance(TimeSpan.FromMinutes(31) + TimeSpan.FromSeconds(1));
        timer.Tick();
        Assert.Equal(["Alarm"], done);
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
