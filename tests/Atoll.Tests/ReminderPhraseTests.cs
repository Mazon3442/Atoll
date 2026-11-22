using Atoll.Modules.Notes;

namespace Atoll.Tests;

public class ReminderPhraseTests
{
    // Monday 23 November 2026, 10:00 local.
    private static readonly DateTime Local = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTimeOffset Now = new(Local, TimeZoneInfo.Local.GetUtcOffset(Local));

    private static DateTimeOffset At(int day, int hour, int minute = 0)
    {
        var local = new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [Theory]
    [InlineData("call mum tomorrow at 9", "Call mum", 24, 9, 0, 0)]
    [InlineData("Remind me to call mom tomorrow at 9", "Call mom", 24, 9, 0, 0)]
    [InlineData("in 20 minutes turn the stove off", "Turn the stove off", 23, 10, 20, 0)]
    [InlineData("pick up order in 2 hours", "Pick up order", 23, 12, 0, 0)]
    [InlineData("stretch in half an hour", "Stretch", 23, 10, 30, 0)]
    [InlineData("pills every day at 23:00", "Pills", 23, 23, 0, 0x7F)]
    [InlineData("pills every day at 9 am", "Pills", 24, 9, 0, 0x7F)]
    [InlineData("take out bins on friday at 7 pm", "Take out bins", 27, 19, 0, 0)]
    [InlineData("gym every monday and wednesday at 18:30", "Gym", 23, 18, 30, 0b0001010)]
    [InlineData("standup every weekday at 9:15", "Standup", 24, 9, 15, 0b0111110)]
    [InlineData("water the plants at 7 pm", "Water the plants", 23, 19, 0, 0)]
    [InlineData("water the plants at 7", "Water the plants", 24, 7, 0, 0)]
    [InlineData("lunch at noon", "Lunch", 23, 12, 0, 0)]
    [InlineData("send the report tomorrow", "Send the report", 24, 9, 0, 0)]
    [InlineData("prep the slides next monday", "Prep the slides", 30, 9, 0, 0)]
    public void Phrases_become_a_title_a_due_time_and_a_repeat(string phrase, string title, int day, int hour, int minute, int repeat)
    {
        Assert.True(ReminderPhrase.TryParse(phrase, Now, out var parsed), phrase);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(At(day, hour, minute), parsed.Due);
        Assert.Equal(repeat, parsed.RepeatDays);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("buy milk")]
    [InlineData("buy 2 apples")]
    [InlineData("tomorrow")] // a time but no title
    public void Phrases_without_a_time_or_a_title_are_refused(string phrase) =>
        Assert.False(ReminderPhrase.TryParse(phrase, Now, out _));

    [Fact]
    public void A_repeat_moves_to_the_next_listed_day()
    {
        // Monday 10:00; Mon/Wed 18:30 -> same day. After that time passes -> Wednesday.
        var monWed = 0b0001010;
        Assert.Equal(At(23, 18, 30), ReminderPhrase.NextRepeat(monWed, new TimeSpan(18, 30, 0), Now));
        Assert.Equal(At(25, 18, 30), ReminderPhrase.NextRepeat(monWed, new TimeSpan(18, 30, 0), At(23, 19)));
    }
}
