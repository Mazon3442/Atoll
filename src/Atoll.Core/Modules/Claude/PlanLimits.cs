using System.Net.Http.Headers;
using System.Text.Json;

namespace Atoll.Modules.Claude;

/// <summary>One subscription usage window, e.g. Claude's 5-hour limit at 42 % that resets at 3 pm.</summary>
public sealed record PlanLimit(string Name, double Percent, DateTimeOffset? ResetsAt);

/// <summary>
/// Plan usage as the agents themselves report it. Claude: the same endpoint Claude Code's /usage reads, signed in
/// with Claude Code's own token, which is sent only to api.anthropic.com. Codex: the rate_limits that Codex writes
/// into its session logs.
/// </summary>
public static class PlanLimits
{
    public static string ClaudeCredentialsPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public static string CodexSessionsRoot { get; } = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "sessions");

    public static async Task<IReadOnlyList<PlanLimit>> ReadClaudeAsync(HttpClient http, string credentialsPath, CancellationToken ct)
    {
        if (!File.Exists(credentialsPath)) return [];
        string? token;
        using (var credentials = JsonDocument.Parse(await File.ReadAllBytesAsync(credentialsPath, ct)))
        {
            if (!credentials.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return [];
            token = oauth.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            // Claude Code refreshes an expired token itself; asking with one would only fail.
            if (oauth.TryGetProperty("expiresAt", out var e) && e.TryGetInt64(out var ms) && DateTimeOffset.FromUnixTimeMilliseconds(ms) < DateTimeOffset.UtcNow) return [];
        }
        if (string.IsNullOrEmpty(token)) return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return [];
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return ParseClaude(doc.RootElement);
    }

    internal static IReadOnlyList<PlanLimit> ParseClaude(JsonElement root)
    {
        var limits = new List<PlanLimit>();
        foreach (var (key, name) in new[] { ("five_hour", "Claude · 5-hour"), ("seven_day", "Claude · weekly") })
        {
            if (!root.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            if (!window.TryGetProperty("utilization", out var u) || !u.TryGetDouble(out var percent)) continue;
            DateTimeOffset? resets = window.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String && r.TryGetDateTimeOffset(out var at) ? at : null;
            limits.Add(new PlanLimit(name, percent, resets));
        }
        return limits;
    }

    /// <summary>The latest limits in the newest Codex session log.</summary>
    public static IReadOnlyList<PlanLimit> ReadCodex(string sessionsRoot)
    {
        if (!Directory.Exists(sessionsRoot)) return [];
        var newest = new DirectoryInfo(sessionsRoot).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
            .MaxBy(f => f.LastWriteTimeUtc);
        if (newest is null) return [];
        try
        {
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - (1 << 20)), SeekOrigin.Begin); // the tail is enough
            using var reader = new StreamReader(stream);
            string? last = null;
            while (reader.ReadLine() is { } line) if (line.Contains("\"rate_limits\"", StringComparison.Ordinal)) last = line;
            return last is null ? [] : ParseCodex(last);
        }
        catch (IOException) { return []; }
    }

    internal static IReadOnlyList<PlanLimit> ParseCodex(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var payload = root.TryGetProperty("payload", out var p) ? p : root;
            if (!payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return [];
            var written = root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var t) ? t : DateTimeOffset.UtcNow;
            var result = new List<PlanLimit>();
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (!limits.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!w.TryGetProperty("used_percent", out var u) || !u.TryGetDouble(out var percent)) continue;
                var minutes = w.TryGetProperty("window_minutes", out var m) && m.TryGetInt32(out var wm) ? wm : 0;
                DateTimeOffset? resets =
                    w.TryGetProperty("resets_at", out var r) && r.TryGetInt64(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : w.TryGetProperty("resets_in_seconds", out var s) && s.TryGetInt64(out var secs) ? written.AddSeconds(secs)
                    : null;
                result.Add(new PlanLimit("Codex · " + minutes switch { 300 => "5-hour", 10080 => "weekly", > 0 and < 1440 => $"{minutes / 60}-hour", > 0 => $"{minutes / 1440}-day", _ => key }, percent, resets));
            }
            return result;
        }
        catch (JsonException) { return []; }
    }
}
