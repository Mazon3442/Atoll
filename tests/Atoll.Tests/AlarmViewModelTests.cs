using Atoll.Modules.Clock;
using Atoll.Settings;
using Microsoft.Extensions.Time.Testing;

namespace Atoll.Tests;

public class AlarmViewModelTests
{
    private readonly FakeTimeProvider _time = new();

    private AlarmViewModel Create(List<AlarmEntry>? entries = null, Action? save = null)
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        // Thursday 12 Nov 2026, 4:02 PM.
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 12, 16, 2, 0, TimeSpan.Zero));
        var alarms = new AlarmViewModel(_time);
        alarms.Use(entries ?? [], save);
        return alarms;
    }

    [Theory]
    [InlineData("730", false, 7, 30)]
    [InlineData("7 30", true, 19, 30)]   // no am/pm: keeps the half already chosen
    [InlineData("7:30pm", false, 19, 30)]
    [InlineData("7:30 AM", true, 7, 30)]
    [InlineData("19:45", false, 19, 45)]
    [InlineData("0645", false, 6, 45)]
    [InlineData("12am", false, 0, 0)]
    [InlineData("12", true, 12, 0)]
    [InlineData("9", false, 9, 0)]
    public void Typed_times_are_understood(string text, bool pm, int hour, int minute)
    {
        Assert.True(AlarmViewModel.TryParse(text, pm, out var time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("25:00")]
    [InlineData("7:75")]
    [InlineData("12345")]
    public void Nonsense_is_refused_and_the_old_time_comes_back(string text)
    {
        var alarms = Create();
        var before = alarms.TimeInput;
        alarms.TimeInput = text;
        Assert.Equal(before, alarms.TimeInput);
    }

    [Fact]
    public void The_editor_starts_at_the_next_five_minutes_and_presets_set_it()
    {
        var alarms = Create();
        Assert.Equal(("4:05", 1), (alarms.TimeInput, alarms.PmIndex));
        Assert.StartsWith("Rings today at", alarms.Summary);

        alarms.UsePresetCommand.Execute("7:30");
        Assert.Equal("7:30", alarms.TimeInput);
        Assert.Equal(0, alarms.PmIndex);
        Assert.StartsWith("Rings tomorrow at 7:30", alarms.Summary);

        alarms.UsePresetCommand.Execute("In 8h"); // 12:05 AM, five-minute steps
        Assert.Equal(("12:05", 0), (alarms.TimeInput, alarms.PmIndex));
    }

    [Fact]
    public void Minutes_step_and_the_half_of_the_day_flips()
    {
        var alarms = Create();
        alarms.TimeInput = "11:59 pm";
        alarms.AddMinuteCommand.Execute(null);
        Assert.Equal(("12:00", 0), (alarms.TimeInput, alarms.PmIndex)); // midnight, next day's am
        alarms.PmIndex = 1;
        Assert.Equal("12:00", alarms.TimeInput); // 12 PM is noon
        alarms.SubtractMinuteCommand.Execute(null);
        Assert.Equal(("11:59", 0), (alarms.TimeInput, alarms.PmIndex));
    }

    [Fact]
    public void Saving_adds_an_alarm_with_its_days_and_label_and_writes_settings()
    {
        var entries = new List<AlarmEntry>();
        var saved = 0;
        var alarms = Create(entries, () => saved++);
        alarms.TimeInput = "7:30";
        alarms.PmIndex = 0;
        alarms.RepeatWeekdaysCommand.Execute(null);
        alarms.Label = " Wake up ";
        alarms.SaveCommand.Execute(null);

        var entry = Assert.Single(entries);
        Assert.Equal((7, 30, "Wake up", true), (entry.Hour, entry.Minute, entry.Label, entry.Enabled));
        Assert.Equal(0b0111110, entry.Days);
        Assert.Equal("Weekdays", alarms.Items[0].RepeatText);
        Assert.Equal(1, saved);

        alarms.Items[0].IsEnabled = false;
        Assert.False(entry.Enabled);
        Assert.Equal(2, saved);

        alarms.Items[0].DeleteCommand.Execute(null);
        Assert.Empty(entries);
        Assert.False(alarms.HasAlarms);
    }

    [Fact]
    public void Editing_updates_the_same_alarm()
    {
        var entries = new List<AlarmEntry> { new() { Hour = 6, Minute = 0, Label = "Run" } };
        var alarms = Create(entries);
        alarms.Items[0].EditCommand.Execute(null);
        Assert.Equal(("6:00", "Run", "Update alarm"), (alarms.TimeInput, alarms.Label, alarms.SaveLabel));

        alarms.TimeInput = "6:15";
        alarms.SaveCommand.Execute(null);
        Assert.Equal(15, Assert.Single(entries).Minute);
        Assert.Equal("Save alarm", alarms.SaveLabel);
    }

    [Fact]
    public void A_one_time_alarm_rings_once_then_turns_off()
    {
        var entries = new List<AlarmEntry> { new() { Hour = 16, Minute = 30, Label = "Tea" } };
        var alarms = Create(entries);
        var changes = 0;
        alarms.StateChanged += () => changes++;

        _time.Advance(TimeSpan.FromMinutes(27));
        alarms.Tick();
        Assert.Equal(RingState.Idle, alarms.State);

        _time.Advance(TimeSpan.FromMinutes(2));
        alarms.Tick();
        Assert.Equal(RingState.Ringing, alarms.State);
        Assert.Equal("Tea", alarms.RingTitle);
        Assert.False(entries[0].Enabled); // spent

        alarms.DismissCommand.Execute(null);
        Assert.Equal(RingState.Idle, alarms.State);
        Assert.Equal(2, changes);
        _time.Advance(TimeSpan.FromDays(1));
        alarms.Tick();
        Assert.Equal(RingState.Idle, alarms.State); // does not ring tomorrow
    }

    [Fact]
    public void Snooze_waits_nine_minutes_then_rings_again()
    {
        var entries = new List<AlarmEntry> { new() { Hour = 16, Minute = 5, Days = 0b1111111 } };
        var alarms = Create(entries);
        _time.Advance(TimeSpan.FromMinutes(4));
        alarms.Tick();
        Assert.Equal(RingState.Ringing, alarms.State);

        alarms.SnoozeCommand.Execute(null);
        Assert.Equal(RingState.Snoozed, alarms.State);
        Assert.Equal("9:00", alarms.SnoozeText);

        _time.Advance(TimeSpan.FromMinutes(8));
        alarms.Tick();
        Assert.Equal(RingState.Snoozed, alarms.State);

        _time.Advance(TimeSpan.FromMinutes(1.1));
        alarms.Tick();
        Assert.Equal(RingState.Ringing, alarms.State);

        alarms.DismissCommand.Execute(null);
        Assert.True(entries[0].Enabled); // a repeating alarm stays on
    }

    [Fact]
    public void A_repeating_alarm_skips_days_it_does_not_repeat_on()
    {
        // Thursday 4:02 PM. Alarm at 4:30 PM on Mondays only: next is Monday 16 Nov.
        var at = AlarmViewModel.Next(new TimeOnly(16, 30), 1 << (int)DayOfWeek.Monday,
            new DateTimeOffset(2026, 11, 12, 16, 2, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 16, 30, 0, TimeSpan.Zero), at);

        // Right now has passed: tomorrow.
        at = AlarmViewModel.Next(new TimeOnly(16, 2), 0, new DateTimeOffset(2026, 11, 12, 16, 2, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 16, 2, 0, TimeSpan.Zero), at);
    }

    [Theory]
    [InlineData(0, "Once")]
    [InlineData(0b1111111, "Every day")]
    [InlineData(0b0111110, "Weekdays")]
    [InlineData(0b1000001, "Weekends")]
    public void Repeat_days_have_short_names(int days, string name) => Assert.Equal(name, AlarmViewModel.RepeatName(days));
}
