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
    /// <summary>"10:30" or "All day".</summary>
    public string StartText => AllDay ? "All day" : Start.ToString("HH:mm");

    /// <summary>"–11:30", empty for all-day.</summary>
    public string EndText => AllDay ? "" : "\u2013" + End.ToString("HH:mm");

    /// <summary>The location when present, otherwise the calendar's name.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Location) ? CalendarName : Location;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "(No title)" : Title;
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
        if (string.IsNullOrWhiteSpace(ics)) return [];
        try
        {
            if (IcalCalendar.Load(ics) is not { } calendar) return [];

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
            return results;
        }
        catch (Exception ex)
        {
            Log.Info($"Calendar parse failed for \"{sourceName}\": {ex.Message}");
            return [];
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
        if (start.Date == now.Date.AddDays(1)) return $"tomorrow {start:HH:mm}";
        return $"{start:ddd} {start:HH:mm}";
    }
}
