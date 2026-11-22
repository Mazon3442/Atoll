using System.Globalization;
using System.Text.RegularExpressions;

namespace Atoll.Modules.Screen;

/// <summary>One recognised line of text and where it sits (top and height in pixels).</summary>
public sealed record OcrLine(string Text, double Top, double Height);

/// <summary>
/// Merges the results of several OCR languages into one text. Windows recognises one language at a time, so a screen
/// with English and Russian is read twice; for each line of the page the reading that looks most like words wins.
/// </summary>
public static class OcrMerge
{
    public static string Merge(IEnumerable<IReadOnlyList<OcrLine>> readings)
    {
        var all = readings.SelectMany(reading => reading).Where(line => line.Text.Trim().Length > 0).OrderBy(line => line.Top).ToList();
        var rows = new List<List<OcrLine>>();
        foreach (var line in all)
        {
            var row = rows.FirstOrDefault(existing => Overlaps(existing[0], line));
            if (row is null) rows.Add([line]);
            else row.Add(line);
        }
        return string.Join(Environment.NewLine, rows.OrderBy(row => row[0].Top).Select(row => Best(row).Text.Trim()));
    }

    private static bool Overlaps(OcrLine a, OcrLine b)
    {
        var overlap = Math.Min(a.Top + a.Height, b.Top + b.Height) - Math.Max(a.Top, b.Top);
        return overlap > 0.5 * Math.Min(a.Height, b.Height);
    }

    /// <summary>The reading with the highest share of letters and digits, then the longest.</summary>
    private static OcrLine Best(IReadOnlyList<OcrLine> candidates) =>
        candidates.OrderByDescending(line => Quality(line.Text)).ThenByDescending(line => line.Text.Length).First();

    private static double Quality(string text)
    {
        var letters = text.Count(char.IsLetterOrDigit);
        var odd = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && !char.IsPunctuation(c));
        return text.Length == 0 ? 0 : (letters - 2.0 * odd) / text.Length;
    }
}

public enum QrKind { Text, Link, Email, Phone, Wifi }

/// <summary>What a scanned QR code holds, so the card can offer the right button (open a link, mail someone, join Wi-Fi).</summary>
public static partial class QrPayload
{
    public static QrKind Classify(string text)
    {
        var value = text.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return QrKind.Link;
        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return QrKind.Email;
        if (value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return QrKind.Phone;
        if (value.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase)) return QrKind.Wifi;
        return QrKind.Text;
    }

    /// <summary>The network name in a "WIFI:T:WPA;S:Home;P:secret;;" code, or null.</summary>
    public static string? WifiName(string text)
    {
        var match = WifiSsid().Match(text);
        return match.Success ? match.Groups["s"].Value.Replace("\\;", ";") : null;
    }

    [GeneratedRegex(@"(?:^|;)S:(?<s>(?:\\;|[^;])*)", RegexOptions.IgnoreCase)]
    private static partial Regex WifiSsid();
}

/// <summary>Where pictures and recordings are saved and what they are called.</summary>
public static class ScreenFiles
{
    /// <summary>"Atoll 2026-11-23 10.00.05.png", with " (2)" added when that name is taken.</summary>
    public static string NextPath(string folder, DateTime now, string extension, Func<string, bool> exists)
    {
        var stem = "Atoll " + now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, stem + extension);
        for (var i = 2; exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}){extension}");
        return path;
    }

    /// <summary>The chosen folder, or Pictures\Atoll when none is set.</summary>
    public static string Folder(string chosen) =>
        string.IsNullOrWhiteSpace(chosen)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Atoll")
            : Environment.ExpandEnvironmentVariables(chosen.Trim());
}
