using System.Globalization;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
// "Calendar" alone would resolve to this file's own namespace (Atoll.Modules.Calendar).
using IcalCalendar = Ical.Net.Calendar;

namespace Atoll.Modules.Calendar;

/// <summary>One event from one calendar, with recurrence already expanded to a concrete occurrence.</summary>
public sealed record CalendarEventItem(
    string Uid,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string Title,
    string? Location,
    string CalendarName,
    string Color)
{
    /// <summary>"10:30 AM" in the user's time format, or "All day".</summary>
    public string StartText => AllDay ? "All day" : IcsCalendar.ShortTime(Start);

    /// <summary>"10:00 – 10:30 AM", or "All day".</summary>
    public string TimeRange => AllDay ? "All day" : IcsCalendar.TimeRange(Start, End);

    /// <summary>The location when present, otherwise the calendar's name.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Location) ? CalendarName : Location;

    /// <summary>"10:00 – 10:30 AM · Room 4B": the second line of a row.</summary>
    public string Detail => $"{TimeRange} · {Subtitle}";

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "(No title)" : Title;

    public string OccurrenceKey => $"{Uid}|{Start.UtcTicks}";
}

/// <summary>
/// Downloads are handled by the module; this only turns ICS text into a list of events with
/// recurrences expanded inside <paramref name="windowStart"/>..<paramref name="windowEnd"/>.
/// </summary>
public static class IcsCalendar
{
    /// <summary>Parses one calendar. Malformed input yields an empty list rather than throwing.</summary>
    public static IReadOnlyList<CalendarEventItem> Parse(
        string ics, string sourceName, string color, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        TryParse(ics, sourceName, color, windowStart, windowEnd, out var events);
        return events;
    }

    internal static bool TryParse(string ics, string sourceName, string color,
        DateTimeOffset windowStart, DateTimeOffset windowEnd, out IReadOnlyList<CalendarEventItem> events)
    {
        events = [];
        if (string.IsNullOrWhiteSpace(ics) || !ics.TrimStart().StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (IcalCalendar.Load(ics) is not { } calendar) return false;

            var results = new List<CalendarEventItem>();
            foreach (var occurrence in calendar.GetOccurrences(ToUtc(windowStart)).TakeWhileBefore(ToUtc(windowEnd)))
            {
                if (occurrence.Source is not CalendarEvent ev) continue;
                var start = occurrence.Period.StartTime;
                var allDay = ev.IsAllDay;
                // EndTime is null for most occurrences; EffectiveEndTime honors DTEND/DURATION (RFC 5545).
                var end = occurrence.Period.EffectiveEndTime ?? start;
                results.Add(new CalendarEventItem(
                    ev.Uid ?? "",
                    ToOffset(start, allDay),
                    ToOffset(end, allDay),
                    allDay,
                    ev.Summary ?? "",
                    string.IsNullOrWhiteSpace(ev.Location) ? null : ev.Location.Trim(),
                    sourceName,
                    color));
            }
            events = results;
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Calendar parse failed for \"{sourceName}\": {ex.Message}");
            return false;
        }
    }

    private static CalDateTime ToUtc(DateTimeOffset t) =>
        new(t.UtcDateTime.Year, t.UtcDateTime.Month, t.UtcDateTime.Day, t.UtcDateTime.Hour, t.UtcDateTime.Minute, t.UtcDateTime.Second, CalDateTime.UtcTzId);

    /// <summary>Converts a CalDateTime to local time. All-day events stay on their calendar date.</summary>
    internal static DateTimeOffset ToOffset(CalDateTime dt, bool allDay)
    {
        if (allDay)
        {
            var d = dt.Value.Date;
            return new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(d));
        }
        return new DateTimeOffset(DateTime.SpecifyKind(dt.AsUtc, DateTimeKind.Utc)).ToLocalTime();
    }

    /// <summary>Short countdown for the pill: "now", "in 12 min", "in 3 h", "tomorrow 09:00", "Fri 09:00".</summary>
    internal static string Relative(DateTimeOffset start, DateTimeOffset now)
    {
        var d = start - now;
        if (d <= TimeSpan.Zero) return "now";
        if (d < TimeSpan.FromMinutes(1)) return "now";
        if (d < TimeSpan.FromHours(1)) return $"in {(int)Math.Round(d.TotalMinutes)} min";
        if (d < TimeSpan.FromHours(24) && start.Date == now.Date) return $"in {d.Hours} h";
        if (start.Date == now.Date.AddDays(1)) return $"tomorrow {ShortTime(start)}";
        return $"{start:ddd} {ShortTime(start)}";
    }

    /// <summary>The user's short time format ("9:40 AM" or "09:40").</summary>
    internal static string ShortTime(DateTimeOffset time, CultureInfo? culture = null) =>
        time.ToString("t", culture ?? CultureInfo.CurrentCulture);

    /// <summary>"10:00 – 10:30 AM": the AM/PM designator is written once when both ends share it.</summary>
    internal static string TimeRange(DateTimeOffset start, DateTimeOffset end, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var pattern = culture.DateTimeFormat.ShortTimePattern;
        var first = ShortTime(start, culture);
        if (pattern.Contains("tt") && start.ToString("tt", culture) == end.ToString("tt", culture))
            first = start.ToString(pattern.Replace("tt", "").Trim(), culture);
        return $"{first} – {ShortTime(end, culture)}";
    }
}
