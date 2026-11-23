using System.Globalization;
using System.Text.RegularExpressions;

namespace Atoll.Modules.Notes;

/// <summary>A reminder read from one phrase. <see cref="RepeatDays"/> uses bit 0 = Sunday, like alarms; 0 means once.</summary>
public sealed record ParsedReminder(string Title, DateTimeOffset Due, int RepeatDays);

/// <summary>
/// Reads "call mum tomorrow at 9", "in 20 minutes turn the stove off" or "pills every day at 23:00".
/// Pure and local: no network, no model. Returns false when the phrase names no time at all.
/// </summary>
public static partial class ReminderPhrase
{
    private static readonly string[] Days = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
    private const int AllDays = 0x7F, Weekdays = 0b0111110;
    private static readonly TimeSpan DefaultTime = TimeSpan.FromHours(9);

    public static bool TryParse(string? phrase, DateTimeOffset now, out ParsedReminder reminder)
    {
        reminder = new ParsedReminder("", now, 0);
        if (string.IsNullOrWhiteSpace(phrase)) return false;
        var text = " " + phrase.Trim() + " ";
        text = LeadingRemind().Replace(text, " ");

        // "in 20 minutes", "in an hour", "in half an hour", "in 2 days"
        if (TryRelative(ref text, out var span))
        {
            var due = now + span;
            reminder = new ParsedReminder(Title(text), due, 0);
            return reminder.Title.Length > 0;
        }

        var repeat = TakeRepeat(ref text);
        var time = TakeTime(ref text);
        var date = TakeDay(ref text, now);
        if (repeat == 0 && time is null && date is null) return false;

        var clock = time ?? DefaultTime;
        var title = Title(text);
        if (title.Length == 0) return false;
        DateTimeOffset result;
        if (repeat != 0)
        {
            result = NextRepeat(repeat, clock, now);
        }
        else if (date is { } day)
        {
            result = At(day, clock);
        }
        else
        {
            result = At(now.Date, clock); // "at 7" alone: the next 7 o'clock
            if (result <= now) result = At(now.Date.AddDays(1), clock);
        }
        reminder = new ParsedReminder(title, result, repeat);
        return true;
    }

    /// <summary>The next time a repeating reminder is due after <paramref name="after"/>.</summary>
    public static DateTimeOffset NextRepeat(int days, TimeSpan clock, DateTimeOffset after)
    {
        for (var i = 0; i <= 7; i++)
        {
            var date = after.Date.AddDays(i);
            if ((days >> (int)date.DayOfWeek & 1) == 0) continue;
            var candidate = At(date, clock);
            if (candidate > after) return candidate;
        }
        return At(after.Date.AddDays(1), clock);
    }

    private static DateTimeOffset At(DateTime date, TimeSpan clock)
    {
        var local = DateTime.SpecifyKind(date.Date + clock, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static bool TryRelative(ref string text, out TimeSpan span)
    {
        span = default;
        var half = HalfHour().Match(text);
        if (half.Success)
        {
            span = TimeSpan.FromMinutes(30);
            text = text.Remove(half.Index, half.Length).Insert(half.Index, " ");
            return true;
        }
        var match = RelativeSpan().Match(text);
        if (!match.Success) return false;
        var amount = match.Groups["n"].Value is { Length: > 0 } n && int.TryParse(n, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
        var unit = match.Groups["u"].Value.ToLowerInvariant();
        span = unit[0] switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            'w' => TimeSpan.FromDays(7 * amount),
            _ => TimeSpan.FromMinutes(amount),
        };
        text = text.Remove(match.Index, match.Length).Insert(match.Index, " ");
        return true;
    }

    private static int TakeRepeat(ref string text)
    {
        var daily = Daily().Match(text);
        if (daily.Success)
        {
            text = text.Remove(daily.Index, daily.Length).Insert(daily.Index, " ");
            return AllDays;
        }
        var weekdays = EveryWeekday().Match(text);
        if (weekdays.Success)
        {
            text = text.Remove(weekdays.Index, weekdays.Length).Insert(weekdays.Index, " ");
            return Weekdays;
        }
        var every = EveryNames().Match(text);
        if (!every.Success) return 0;
        var mask = 0;
        foreach (var name in DayNames().Matches(every.Groups["list"].Value).Select(m => m.Value.ToLowerInvariant()))
            mask |= 1 << Array.FindIndex(Days, day => day.StartsWith(name[..3], StringComparison.Ordinal));
        text = text.Remove(every.Index, every.Length).Insert(every.Index, " ");
        return mask;
    }

    private static TimeSpan? TakeTime(ref string text)
    {
        var named = NamedTime().Match(text);
        if (named.Success)
        {
            text = text.Remove(named.Index, named.Length).Insert(named.Index, " ");
            return named.Groups["w"].Value.Equals("noon", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(12) : TimeSpan.Zero;
        }
        var match = ClockTime().Match(text);
        if (!match.Success) return null;
        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var meridiem = match.Groups["ap"].Value.Replace(".", "").ToLowerInvariant();
        if (minute > 59 || hour > 23 || (meridiem.Length > 0 && hour is 0 or > 12)) return null;
        if (meridiem == "pm" && hour < 12) hour += 12;
        if (meridiem == "am" && hour == 12) hour = 0;
        text = text.Remove(match.Index, match.Length).Insert(match.Index, " ");
        return new TimeSpan(hour, minute, 0);
    }

    private static DateTime? TakeDay(ref string text, DateTimeOffset now)
    {
        var today = now.Date;
        foreach (var (pattern, offset) in new[] { (Tomorrow(), 1), (TodayOrTonight(), 0) })
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            text = text.Remove(match.Index, match.Length).Insert(match.Index, " ");
            return today.AddDays(offset);
        }
        var weekday = WeekdayName().Match(text);
        if (weekday.Success)
        {
            var target = Array.FindIndex(Days, day => day.StartsWith(weekday.Groups["d"].Value[..3].ToLowerInvariant(), StringComparison.Ordinal));
            var ahead = (target - (int)today.DayOfWeek + 7) % 7;
            if (ahead == 0) ahead = 7;
            text = text.Remove(weekday.Index, weekday.Length).Insert(weekday.Index, " ");
            return today.AddDays(ahead);
        }
        return null;
    }

    private static string Title(string text)
    {
        var cleaned = Spaces().Replace(Connectors().Replace(text, " "), " ").Trim(' ', ',', '.', ';', '-');
        cleaned = LeadingTo().Replace(cleaned, "");
        return cleaned.Length == 0 ? "" : char.ToUpper(cleaned[0], CultureInfo.CurrentCulture) + cleaned[1..];
    }

    [GeneratedRegex(@"^\s*(?:please\s+)?remind\s+me\s+(?:to\s+|about\s+|that\s+)?", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingRemind();

    [GeneratedRegex(@"\bin\s+half\s+an?\s+hour\b", RegexOptions.IgnoreCase)]
    private static partial Regex HalfHour();

    [GeneratedRegex(@"\bin\s+(?:(?<n>\d+)|an?)\s*(?<u>minutes?|mins?|m|hours?|hrs?|h|days?|d|weeks?|w)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeSpan();

    [GeneratedRegex(@"\b(?:every\s*day|daily|each\s+day)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Daily();

    [GeneratedRegex(@"\bevery\s+week\s*day\b|\bon\s+weekdays\b|\bevery\s+weekday\b", RegexOptions.IgnoreCase)]
    private static partial Regex EveryWeekday();

    [GeneratedRegex(@"\bevery\s+(?<list>(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*(?:\s*(?:,|and|&)\s*(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*)*)", RegexOptions.IgnoreCase)]
    private static partial Regex EveryNames();

    [GeneratedRegex(@"(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*", RegexOptions.IgnoreCase)]
    private static partial Regex DayNames();

    [GeneratedRegex(@"\b(?:at\s+)?(?<w>noon|midnight)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NamedTime();

    [GeneratedRegex(@"\b(?:at\s+(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?\s*(?<ap>a\.?m\.?|p\.?m\.?)?|(?<h>\d{1,2})[:.](?<m>\d{2})\s*(?<ap>a\.?m\.?|p\.?m\.?)?|(?<h>\d{1,2})\s*(?<ap>a\.?m\.?|p\.?m\.?))(?=\s|$|[,.])", RegexOptions.IgnoreCase)]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"\btomorrow\b", RegexOptions.IgnoreCase)]
    private static partial Regex Tomorrow();

    [GeneratedRegex(@"\b(?:today|tonight)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TodayOrTonight();

    [GeneratedRegex(@"\b(?:on\s+)?(?<next>next\s+)?(?<d>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayName();

    [GeneratedRegex(@"\b(?:at|on|by)\b(?=\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex Connectors();

    [GeneratedRegex(@"^(?:to|that|about)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingTo();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
