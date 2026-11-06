using System.Text;
using System.Text.Json;
using Atoll.Claude;

namespace Atoll.Modules.Claude;

/// <summary>Reads recent Codex rollout activity when a session has not loaded the hook adapter.</summary>
public sealed class CodexSessionMonitor(string? root = null) : IDisposable
{
    private readonly string _root = root ?? Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "sessions");
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, Cursor> _files = [];
    private sealed class Cursor
    {
        public long Offset;
        public string? Session;
        public string? Cwd;
        public MemoryStream Line { get; } = new();
        public bool SkipLine;
        public bool Ended;
        public bool Reported;
        public DateTimeOffset LastActivity;
    }
    public event Action<HookMessage>? Received;
    public void Start() => _ = Task.Run(Run);
    private async Task Run()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { do { Poll(); } while (await timer.WaitForNextTickAsync(_shutdown.Token)); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { foreach (var cursor in _files.Values) cursor.Line.Dispose(); _files.Clear(); }
    }
    internal void Poll()
    {
        if (!Directory.Exists(_root)) return;
        try
        {
            // Only inspect today's and yesterday's folders; never recursively scan the full chat archive.
            var paths = new[] { DateTime.UtcNow, DateTime.UtcNow.AddDays(-1) }.SelectMany(day =>
            {
                var directory = Path.Combine(_root, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
                return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.jsonl") : [];
            }).Select(p => new FileInfo(p)).OrderByDescending(f => f.CreationTimeUtc).Take(32).ToArray();
            var wanted = paths.Select(p => p.FullName).ToHashSet();
            foreach (var path in _files.Keys.Where(p => !wanted.Contains(p)).ToArray())
            {
                var cursor = _files[path];
                if (cursor.Reported && cursor.Session is { } session) Received?.Invoke(new HookMessage { Agent = "codex", Event = "SessionEnd", SessionId = session });
                cursor.Line.Dispose(); _files.Remove(path);
            }
            foreach (var file in paths)
            {
                try { Read(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (_files.TryGetValue(file.FullName, out var cursor) && cursor.Reported && cursor.LastActivity < DateTimeOffset.UtcNow.AddMinutes(-10))
                {
                    cursor.Reported = false;
                    Received?.Invoke(new HookMessage { Agent = "codex", Event = "SessionEnd", SessionId = cursor.Session! });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void Read(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var initial = !_files.TryGetValue(file.FullName, out var cursor);
        if (initial) { cursor = new Cursor(); _files[file.FullName] = cursor; }
        if (stream.Length < cursor!.Offset) { cursor.Offset = 0; cursor.Line.SetLength(0); cursor.Session = null; initial = true; }
        HookMessage? latest = null;
        if (initial)
        {
            // Session metadata is first; read a bounded prefix before jumping to recent events.
            var prefix = new byte[(int)Math.Min(stream.Length, 16 * 1024)];
            stream.ReadExactly(prefix); ReadMetadata(prefix, cursor);
            cursor.Offset = Math.Max(0, stream.Length - 4 * 1024 * 1024);
            cursor.SkipLine = cursor.Offset > 0;
        }
        stream.Position = cursor.Offset;
        var bytes = new byte[16 * 1024]; int read; var remaining = 4 * 1024 * 1024;
        while (remaining > 0 && (read = stream.Read(bytes, 0, Math.Min(bytes.Length, remaining))) > 0)
        {
            remaining -= read; cursor.Offset += read;
            foreach (var value in bytes.AsSpan(0, read))
            {
                if (value == '\n')
                {
                    if (!cursor.SkipLine)
                    {
                        var message = Parse(Encoding.UTF8.GetString(cursor.Line.GetBuffer(), 0, (int)cursor.Line.Length), cursor);
                        if (message is not null) latest = message;
                    }
                    cursor.Line.SetLength(0); cursor.SkipLine = false;
                }
                else if (!cursor.SkipLine)
                {
                    if (cursor.Line.Length >= 2 * 1024 * 1024) { cursor.SkipLine = true; cursor.Line.SetLength(0); }
                    else cursor.Line.WriteByte(value);
                }
            }
        }
        if (latest is not null)
        {
            if (initial && latest.Event is "Stop" or "Interrupt") latest = latest with { Event = "SessionStart", Detail = null };
            cursor.Reported = true;
            Received?.Invoke(latest);
        }
    }
    private static void ReadMetadata(ReadOnlySpan<byte> bytes, Cursor cursor)
    {
        // Codex metadata can include large instructions after these fields; do not buffer that text.
        try
        {
            var reader = new Utf8JsonReader(bytes, false, default);
            var payload = false;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.CurrentDepth == 1 && reader.ValueTextEquals("payload")) { payload = true; continue; }
                if (!payload || reader.CurrentDepth != 2) continue;
                var field = reader.GetString();
                if (field is not ("id" or "session_id" or "cwd")) continue;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) continue;
                if (field == "cwd") cursor.Cwd = reader.GetString(); else cursor.Session = reader.GetString();
                if (cursor.Session is not null && cursor.Cwd is not null) return;
            }
        }
        catch (JsonException) { }
    }
    private static HookMessage? Parse(string line, Cursor cursor)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
            var type = Str(root, "type");
            if (type == "session_meta") { cursor.Session = Str(payload, "id") ?? Str(payload, "session_id"); cursor.Cwd = Str(payload, "cwd"); return null; }
            if (cursor.Session is null || !DateTimeOffset.TryParse(Str(root, "timestamp"), out var timestamp) || timestamp < DateTimeOffset.UtcNow.AddMinutes(-10)) return null;
            var eventType = Str(payload, "type");
            if (eventType == "task_started") cursor.Ended = false;
            if (eventType is "task_complete" or "turn_aborted") cursor.Ended = true;
            if (cursor.Ended && eventType is "token_count" or "item_completed") return null;
            var ev = (type, eventType) switch
            {
                ("event_msg", "task_started") => "UserPromptSubmit",
                ("event_msg", "task_complete") => "Stop",
                ("event_msg", "turn_aborted") => "Interrupt",
                ("response_item", "function_call" or "custom_tool_call") => "PreToolUse",
                ("response_item", "function_call_output" or "custom_tool_call_output") => "PostToolUse",
                ("event_msg", "token_count" or "item_completed") => "PostToolUse",
                _ => null,
            };
            if (ev is null) return null;
            cursor.LastActivity = timestamp;
            var tool = ev == "PreToolUse" ? Str(payload, "name") : null;
            return new HookMessage { Agent = "codex", Event = ev, SessionId = cursor.Session, Cwd = cursor.Cwd, Tool = tool, Detail = tool is null ? null : "Using " + tool[..Math.Min(tool.Length, 100)] };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }
    public void Dispose() => _shutdown.Cancel();
}
