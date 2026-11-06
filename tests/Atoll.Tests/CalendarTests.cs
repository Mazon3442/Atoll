using System.Globalization;
using Atoll.Modules.Calendar;
using Atoll.Settings;

namespace Atoll.Tests;

public class CalendarParsingTests
{
    // Windows span the whole of November 2026 so only the events under test appear.
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<CalendarEventItem> Parse(string ics) =>
        IcsCalendar.Parse(ics, "Work", "#0A84FF", Start, End);

    [Fact]
    public void Parses_a_simple_timed_event()
    {
        var events = Parse("""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:1
            SUMMARY:Standup
            DTSTART:20261110T090000Z
            DTEND:20261110T093000Z
            LOCATION:Zoom
            END:VEVENT
            END:VCALENDAR
            """);

        var e = Assert.Single(events);
        Assert.Equal("Standup", e.Title);
        Assert.Equal("Zoom", e.Location);
        Assert.Equal("Work", e.CalendarName);
        Assert.False(e.AllDay);
        Assert.Equal(new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc), e.Start.UtcDateTime);
        Assert.Equal(new DateTime(2026, 11, 10, 9, 30, 0, DateTimeKind.Utc), e.End.UtcDateTime);
    }

    [Fact]
    public void Expands_a_weekly_recurrence()
    {
        var events = Parse("""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:2
            SUMMARY:Team sync
            DTSTART:20261102T100000Z
            DTEND:20261102T103000Z
            RRULE:FREQ=WEEKLY;COUNT=4
            END:VEVENT
            END:VCALENDAR
            """);

        Assert.Equal(4, events.Count);
        var days = events.Select(e => e.Start.UtcDateTime.Day).ToList();
        Assert.Equal([2, 9, 16, 23], days);
    }

    [Fact]
    public void Skips_and_moves_occurrences_via_exdate_and_rdate()
    {
        // Nov 9 is moved from 10:00 (skipped) to 14:00 (RDATE).
        var events = Parse("""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:3
            SUMMARY:Every Monday
            DTSTART:20261102T100000Z
            DTEND:20261102T103000Z
            RRULE:FREQ=WEEKLY;COUNT=4
            EXDATE:20261109T100000Z
            RDATE:20261109T140000Z
            END:VEVENT
            END:VCALENDAR
            """);

        Assert.Equal(4, events.Count);
        Assert.Contains(events, e => e.Start.UtcDateTime == new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc));
        Assert.DoesNotContain(events, e => e.Start.UtcDateTime == new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void All_day_events_are_marked_and_keep_their_date()
    {
        var events = Parse("""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:4
            SUMMARY:Holiday
            DTSTART;VALUE=DATE:20261111
            DTEND;VALUE=DATE:20261112
            END:VEVENT
            END:VCALENDAR
            """);

        var e = Assert.Single(events);
        Assert.True(e.AllDay);
        Assert.Equal(new DateTime(2026, 11, 11), e.Start.Date);
        Assert.Equal("All day", e.StartText);
    }

    [Fact]
    public void Includes_events_that_started_before_the_window_but_are_still_running()
    {
        var events = IcsCalendar.Parse("""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:overnight
            DTSTART:20261109T230000Z
            DTEND:20261110T140000Z
            END:VEVENT
            BEGIN:VEVENT
            UID:multi-day
            DTSTART;VALUE=DATE:20261109
            DTEND;VALUE=DATE:20261112
            END:VEVENT
            END:VCALENDAR
            """, "Work", "#007AFF", new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero), End);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Distinguishes_an_empty_calendar_from_an_invalid_feed()
    {
        Assert.True(IcsCalendar.TryParse("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR", "Work", "#007AFF", Start, End, out var empty));
        Assert.Empty(empty);
        Assert.False(IcsCalendar.TryParse("<html>Sign in</html>", "Work", "#007AFF", Start, End, out _));
    }

    [Fact]
    public void Missing_titles_fall_back_and_malformed_input_is_empty()
    {
        var events = Parse("""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:5
            DTSTART:20261112T090000Z
            END:VEVENT
            END:VCALENDAR
            """);
        Assert.Equal("(No title)", Assert.Single(events).DisplayTitle);

        Assert.Empty(IcsCalendar.Parse("not an ics file", "X", "#fff", Start, End));
        Assert.Empty(IcsCalendar.Parse("", "X", "#fff", Start, End));
    }

    [Theory]
    [InlineData(-5, "now")]                 // already started
    [InlineData(12, "in 12 min")]
    [InlineData(180, "in 3 h")]
    public void Relative_countdown_formats_minutes_and_hours(int minutesFromNow, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, IcsCalendar.Relative(now.AddMinutes(minutesFromNow), now));
    }

    [Fact]
    public void Relative_rounds_sub_minute_to_now()
    {
        var now = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal("now", IcsCalendar.Relative(now.AddSeconds(30), now));
    }

    [Fact]
    public void Relative_uses_tomorrow_and_weekday_for_later_events()
    {
        var now = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        var tomorrow = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
        var sunday = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal($"tomorrow {tomorrow:t}", IcsCalendar.Relative(tomorrow, now));
        Assert.Equal($"Sun {sunday:t}", IcsCalendar.Relative(sunday, now));
    }

    [Fact]
    public void Time_range_follows_the_culture_and_writes_a_shared_designator_once()
    {
        var us = CultureInfo.GetCultureInfo("en-US");
        var start = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        // ICU and Windows NLS use different spaces before AM/PM.
        static string Spaces(string text) => text.Replace('\u202f', ' ').Replace('\u00a0', ' ');
        Assert.Equal("10:00 – 10:30 AM", Spaces(IcsCalendar.TimeRange(start, start.AddMinutes(30), us)));
        Assert.Equal("11:30 AM – 12:30 PM", Spaces(IcsCalendar.TimeRange(start.AddMinutes(90), start.AddMinutes(150), us)));
        Assert.Equal("10:00 – 10:30", IcsCalendar.TimeRange(start, start.AddMinutes(30), CultureInfo.GetCultureInfo("de-DE")));
    }
}

public class SecretStoreTests
{
    [Fact]
    public void Round_trips_a_secret()
    {
        var secret = "https://calendar.google.com/calendar/ical/secret/basic.ics";
        Assert.Equal(secret, SecretStore.Unprotect(SecretStore.Protect(secret)));
    }

    [Fact]
    public void Blank_or_garbage_decrypts_to_null()
    {
        Assert.Null(SecretStore.Unprotect(""));
        Assert.Null(SecretStore.Unprotect(null));
        Assert.Null(SecretStore.Unprotect("not-base64!"));
    }
}
