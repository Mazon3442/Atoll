using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atoll.Modules.Media;

public sealed record LyricLine(TimeSpan At, string Text);

/// <summary>Time-synced lyrics from lrclib.net (free, no key), cached on disk so each song is looked up once.</summary>
public static partial class Lyrics
{
    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atoll", "lyrics");

    [GeneratedRegex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]")]
    private static partial Regex TimeTag();

    /// <summary>Parses LRC text. A line may carry several time tags ("[00:12.00][01:30.50]chorus").</summary>
    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in (lrc ?? "").Split('\n'))
        {
            var tags = TimeTag().Matches(raw);
            if (tags.Count == 0) continue;
            var text = raw[(tags[^1].Index + tags[^1].Length)..].Trim();
            foreach (Match tag in tags)
            {
                var seconds = double.Parse(tag.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromMinutes(int.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture)) + TimeSpan.FromSeconds(seconds), text));
            }
        }
        lines.Sort((a, b) => a.At.CompareTo(b.At));
        return lines;
    }

    /// <summary>The line being sung at <paramref name="position"/>: "" before the first line, "♪" for an instrumental gap.</summary>
    public static string LineAt(IReadOnlyList<LyricLine> lines, TimeSpan position) =>
        IndexAt(lines, position) is var i and >= 0 ? Display(lines[i]) : "";

    /// <summary>Index of the line being sung, or -1 before the first line.</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        var i = -1;
        while (i + 1 < lines.Count && lines[i + 1].At <= position) i++;
        return i;
    }

    /// <summary>The line's text, or "♪" for an instrumental gap.</summary>
    public static string Display(LyricLine line) => line.Text.Length > 0 ? line.Text : "♪";

    public static async Task<IReadOnlyList<LyricLine>> FindAsync(HttpClient http, string artist, string title, TimeSpan duration, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{artist}\n{title}".ToLowerInvariant())))[..32];
        var cached = Path.Combine(CacheDirectory, key + ".lrc");
        if (File.Exists(cached)) return Parse(await File.ReadAllTextAsync(cached, ct)); // empty file: looked up, none found

        var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return []; // try again next time
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var lrc = Pick(doc.RootElement, duration) ?? "";
        await File.WriteAllTextAsync(cached, lrc, ct);
        return Parse(lrc);
    }

    /// <summary>The synced lyrics whose recording is closest in length (versions differ: radio edit, live, remaster).</summary>
    internal static string? Pick(JsonElement results, TimeSpan duration)
    {
        if (results.ValueKind != JsonValueKind.Array) return null;
        return results.EnumerateArray()
            .Where(r => r.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String && s.GetString()!.Length > 0)
            .Select(r => (Lrc: r.GetProperty("syncedLyrics").GetString()!,
                Off: duration > TimeSpan.Zero && r.TryGetProperty("duration", out var d) && d.TryGetDouble(out var sec) ? Math.Abs(sec - duration.TotalSeconds) : 0))
            .Where(r => r.Off <= 5)
            .OrderBy(r => r.Off)
            .Select(r => r.Lrc)
            .FirstOrDefault();
    }
}
