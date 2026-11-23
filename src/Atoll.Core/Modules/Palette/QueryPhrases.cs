using System.Globalization;
using System.Text.RegularExpressions;

namespace Atoll.Modules.Palette;

/// <summary>"100 usd to eur". Rates come from the caller, so this stays pure and testable.</summary>
public static partial class CurrencyConverter
{
    /// <summary>Splits "100 usd to eur" into amount and ISO codes. False when it does not look like a conversion.</summary>
    public static bool TryParse(string? text, out double amount, out string from, out string to)
    {
        amount = 0;
        from = to = "";
        if (text is null) return false;
        var match = Phrase().Match(text.Trim());
        if (!match.Success) return false;
        amount = double.Parse(match.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        from = match.Groups["a"].Value.ToUpperInvariant();
        to = match.Groups["b"].Value.ToUpperInvariant();
        return from != to;
    }

    /// <summary>
    /// Converts through the base currency of <paramref name="ratesPerBase"/> (units of each currency per one base unit).
    /// False when either code is unknown.
    /// </summary>
    public static bool TryConvert(double amount, string from, string to, IReadOnlyDictionary<string, double> ratesPerBase, out double result)
    {
        result = 0;
        if (!ratesPerBase.TryGetValue(from, out var fromRate) || !ratesPerBase.TryGetValue(to, out var toRate) || fromRate <= 0) return false;
        result = amount / fromRate * toRate;
        return true;
    }

    [GeneratedRegex(@"^(?<n>\d+(?:[.,]\d+)?)\s*(?<a>[a-zA-Z]{3})\s+(?:to|in|into|=|->)\s+(?<b>[a-zA-Z]{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex Phrase();
}

/// <summary>"timer 10 min", "timer 1h 30m", "5 minute timer", "stopwatch".</summary>
public static partial class TimerPhrase
{
    public static bool IsStopwatch(string? text) =>
        text is not null && StopwatchWord().IsMatch(text.Trim());

    /// <summary>The countdown length named by <paramref name="text"/>, or false when it names none.</summary>
    public static bool TryParse(string? text, out TimeSpan length)
    {
        length = default;
        if (text is null) return false;
        var match = Countdown().Match(text.Trim());
        if (!match.Success) return false;
        var body = match.Groups["a"].Success ? match.Groups["a"].Value : match.Groups["b"].Value;
        var total = TimeSpan.Zero;
        var any = false;
        foreach (Match part in Part().Matches(body))
        {
            var amount = double.Parse(part.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var unit = part.Groups["u"].Value.ToLowerInvariant();
            total += (unit.Length > 0 ? unit[0] : 'm') switch
            {
                'h' => TimeSpan.FromHours(amount),
                's' => TimeSpan.FromSeconds(amount),
                _ => TimeSpan.FromMinutes(amount), // "m", "min" and a bare number are minutes
            };
            any = true;
        }
        if (!any || total <= TimeSpan.Zero || total > TimeSpan.FromHours(24)) return false;
        length = total;
        return true;
    }

    [GeneratedRegex(@"^(?:stopwatch|stop watch)$", RegexOptions.IgnoreCase)]
    private static partial Regex StopwatchWord();

    [GeneratedRegex(@"^(?:(?:timer|countdown)\s+(?<a>.+)|(?<b>.+?)\s+(?:timer|countdown))$", RegexOptions.IgnoreCase)]
    private static partial Regex Countdown();

    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)?", RegexOptions.IgnoreCase)]
    private static partial Regex Part();
}

/// <summary>"&gt; ipconfig /all" runs in PowerShell; "&gt;cmd dir" in the classic prompt.</summary>
public static class ShellPhrase
{
    public enum Shell { PowerShell, Cmd }

    public static bool TryParse(string? text, out Shell shell, out string command)
    {
        shell = Shell.PowerShell;
        command = "";
        if (text is null) return false;
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('>')) return false;
        var body = trimmed[1..].Trim();
        if (body.StartsWith("cmd ", StringComparison.OrdinalIgnoreCase))
        {
            shell = Shell.Cmd;
            body = body[4..].Trim();
        }
        if (body.Length == 0) return false;
        command = body;
        return true;
    }
}
