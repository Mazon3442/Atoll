using System.Text.Json;

namespace Island.Modules.Claude;

/// <summary>Token counts from one assistant response in a Claude Code transcript.</summary>
public sealed record UsageEntry(
    DateTimeOffset Time,
    string Model,
    long Input,
    long Output,
    long CacheWrite5m,
    long CacheWrite1h,
    long CacheRead)
{
    public long TotalTokens => Input + Output + CacheWrite5m + CacheWrite1h + CacheRead;

    /// <summary>
    /// Parses one JSONL line. Returns null for anything that is not an assistant message with usage.
    /// <paramref name="dedupeKey"/> is message id + request id: streaming writes several lines per response.
    /// </summary>
    public static UsageEntry? TryParse(ReadOnlySpan<byte> line, out string? dedupeKey)
    {
        dedupeKey = null;
        if (line.IndexOf("\"usage\""u8) < 0) return null; // cheap pre-filter; most lines are not responses
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            if (!Is(root, "type", "assistant")) return null;
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("usage", out var usage)) return null;
            var model = msg.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            if (model.StartsWith('<')) return null; // "<synthetic>" placeholder messages
            if (!root.TryGetProperty("timestamp", out var ts) || !ts.TryGetDateTimeOffset(out var time)) return null;

            long N(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            var cacheWrite = N(usage, "cache_creation_input_tokens");
            long write5m = cacheWrite, write1h = 0;
            if (usage.TryGetProperty("cache_creation", out var split))
            {
                write5m = N(split, "ephemeral_5m_input_tokens");
                write1h = N(split, "ephemeral_1h_input_tokens");
            }

            var id = msg.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var req = root.TryGetProperty("requestId", out var reqEl) ? reqEl.GetString() : null;
            dedupeKey = id is null ? null : $"{id}:{req}";

            return new UsageEntry(time, model, N(usage, "input_tokens"), N(usage, "output_tokens"), write5m, write1h, N(usage, "cache_read_input_tokens"));
        }
        catch (JsonException)
        {
            return null; // partial or corrupt line
        }
    }

    private static bool Is(JsonElement e, string name, string value) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == value;
}

/// <summary>
/// API list prices in USD per million tokens, used for an "API-equivalent" estimate. Subscription
/// plans are not billed this way. Source: Anthropic model table (cached 2026-10-06). Cache writes
/// are 1.25× input (5-minute) and 2× input (1-hour) unless noted.
/// </summary>
public static class ClaudePricing
{
    public sealed record Rates(decimal Input, decimal Output, decimal CacheRead)
    {
        public decimal CacheWrite5m => Input * 1.25m;
        public decimal CacheWrite1h => Input * 2m;
    }

    // Longest prefix wins, so "claude-opus-5-5" is listed before "claude-opus-5".
    private static readonly (string Prefix, Rates Rates)[] Table =
    [
        ("claude-fable-5-1", new(10m, 50m, 0.25m)),
        ("claude-fable-5", new(10m, 50m, 1m)),
        ("claude-mythos", new(10m, 50m, 1m)),
        ("claude-opus-5-5", new(4m, 20m, 0.20m)),
        ("claude-opus-5", new(5m, 25m, 0.50m)),
        ("claude-opus-4-8", new(5m, 25m, 0.50m)),
        ("claude-opus-4-7", new(5m, 25m, 0.50m)),
        ("claude-opus-4-6", new(5m, 25m, 0.50m)),
        ("claude-opus-4-5", new(5m, 25m, 0.50m)),
        ("claude-opus-4", new(15m, 75m, 1.50m)),
        ("claude-sonnet-5-5", new(2m, 10m, 0.20m)),
        ("claude-sonnet-5", new(2m, 10m, 0.20m)),
        ("claude-sonnet-4", new(3m, 15m, 0.30m)),
        ("claude-haiku-5-5", new(0.10m, 0.50m, 0.01m)),
        ("claude-haiku-4-5", new(1m, 5m, 0.10m)),
        ("claude-3-5-haiku", new(0.80m, 4m, 0.08m)),
    ];

    public static Rates? For(string model) =>
        Table.Where(t => model.StartsWith(t.Prefix, StringComparison.Ordinal))
             .OrderByDescending(t => t.Prefix.Length)
             .Select(t => t.Rates)
             .FirstOrDefault();

    public static decimal Cost(UsageEntry e)
    {
        if (For(e.Model) is not { } r) return 0;
        return (e.Input * r.Input + e.Output * r.Output + e.CacheWrite5m * r.CacheWrite5m
                + e.CacheWrite1h * r.CacheWrite1h + e.CacheRead * r.CacheRead) / 1_000_000m;
    }
}

public sealed record UsageSummary(
    long TodayTokens,
    decimal TodayCost,
    DateTimeOffset? BlockStart,
    DateTimeOffset? BlockEnd,
    long BlockTokens,
    decimal BlockCost);

/// <summary>
/// Totals for today and for the current 5-hour block (Claude's usage window). A block starts at the
/// hour of the first message after the previous block ended and lasts 5 hours. Thread-safe.
/// </summary>
public sealed class UsageCalculator
{
    public static readonly TimeSpan BlockLength = TimeSpan.FromHours(5);
    private static readonly TimeSpan Keep = TimeSpan.FromHours(30);

    private readonly object _gate = new();
    private readonly List<UsageEntry> _entries = [];
    private readonly Dictionary<string, DateTimeOffset> _seen = [];

    public void Add(UsageEntry entry, string? dedupeKey)
    {
        lock (_gate)
        {
            if (dedupeKey is not null)
            {
                if (_seen.ContainsKey(dedupeKey)) return;
                _seen[dedupeKey] = entry.Time;
            }
            _entries.Add(entry);
        }
    }

    public UsageSummary Summarize(DateTimeOffset now, TimeZoneInfo zone)
    {
        lock (_gate)
        {
            Prune(now);
            var localNow = TimeZoneInfo.ConvertTime(now, zone);
            var dayStart = new DateTimeOffset(localNow.Date, localNow.Offset);
            var today = _entries.Where(e => e.Time >= dayStart).ToList();

            DateTimeOffset? blockStart = null;
            DateTimeOffset last = default;
            foreach (var e in _entries.OrderBy(e => e.Time))
            {
                if (blockStart is null || e.Time >= blockStart + BlockLength || e.Time - last >= BlockLength)
                    blockStart = FloorToHour(e.Time);
                last = e.Time;
            }

            var active = blockStart is { } start && now < start + BlockLength;
            var block = active ? _entries.Where(e => e.Time >= blockStart!.Value && e.Time < blockStart.Value + BlockLength).ToList() : [];

            return new UsageSummary(
                today.Sum(e => e.TotalTokens),
                today.Sum(ClaudePricing.Cost),
                active ? blockStart : null,
                active ? blockStart + BlockLength : null,
                block.Sum(e => e.TotalTokens),
                block.Sum(ClaudePricing.Cost));
        }
    }

    private void Prune(DateTimeOffset now)
    {
        _entries.RemoveAll(e => now - e.Time > Keep);
        foreach (var key in _seen.Where(kv => now - kv.Value > Keep).Select(kv => kv.Key).ToList())
            _seen.Remove(key);
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset);
}

/// <summary>
/// Tails ~/.claude/projects/**/*.jsonl and keeps a <see cref="UsageCalculator"/> current.
/// Reads each file once, then only appended bytes. <see cref="Updated"/> fires on a background thread.
/// </summary>
public sealed class UsageMonitor : IDisposable
{
    private const int ChunkSize = 1 << 20;

    private readonly string _root;
    private readonly UsageCalculator _calculator = new();
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private int _scanning;

    public UsageMonitor(string root) => _root = root;

    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public event Action<UsageSummary>? Updated;

    public void Start()
    {
        if (!Directory.Exists(_root)) return; // Claude Code not used on this PC
        _watcher = new FileSystemWatcher(_root, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += (_, e) => MarkDirty(e.FullPath);
        _watcher.Created += (_, e) => MarkDirty(e.FullPath);
        _watcher.EnableRaisingEvents = true;

        // Initial scan on a worker thread, then every 2 s: read dirty files and refresh the summary
        // (the block countdown and day rollover change even without new messages).
        _timer = new Timer(_ => Scan(initial: false), null, Timeout.Infinite, Timeout.Infinite);
        Task.Run(() =>
        {
            Scan(initial: true);
            _timer?.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        });
    }

    private void MarkDirty(string path)
    {
        lock (_gate) _dirty.Add(path);
    }

    private void Scan(bool initial)
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            List<string> files;
            if (initial)
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromHours(30);
                files = Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories)
                    .Where(f => File.GetLastWriteTimeUtc(f) >= cutoff)
                    .ToList();
            }
            else
            {
                lock (_gate)
                {
                    files = [.. _dirty];
                    _dirty.Clear();
                }
            }
            foreach (var f in files) ReadAppended(f);
            Updated?.Invoke(_calculator.Summarize(DateTimeOffset.Now, TimeZoneInfo.Local));
        }
        catch (Exception ex)
        {
            Log.Error("Claude usage scan failed", ex);
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
    }

    private void ReadAppended(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = _offsets.GetValueOrDefault(path);
            if (fs.Length < offset) offset = 0; // file was rewritten
            fs.Seek(offset, SeekOrigin.Begin);

            var buffer = new byte[ChunkSize];
            var carry = 0; // bytes of an unfinished line kept at the start of buffer
            while (true)
            {
                if (carry == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2); // one very long line
                var read = fs.Read(buffer, carry, buffer.Length - carry);
                if (read == 0) break;
                ReadOnlySpan<byte> span = buffer.AsSpan(0, carry + read);
                var lastNewline = span.LastIndexOf((byte)'\n');
                if (lastNewline < 0)
                {
                    carry += read;
                    continue;
                }
                var lines = span[..lastNewline];
                foreach (var range in lines.Split((byte)'\n'))
                {
                    if (UsageEntry.TryParse(lines[range], out var key) is { } entry) _calculator.Add(entry, key);
                }
                var rest = span[(lastNewline + 1)..];
                offset = fs.Position - rest.Length;
                rest.CopyTo(buffer);
                carry = rest.Length;
            }
            _offsets[path] = offset; // an unfinished last line is re-read next time
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // File busy or deleted; it will be marked dirty again on the next write.
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer?.Dispose();
    }
}
