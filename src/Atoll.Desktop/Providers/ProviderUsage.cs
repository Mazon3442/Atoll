using System.Text.Json;

namespace Atoll.Desktop.Providers;

public sealed record ProviderTotals(long Requests = 0, long Failed = 0, long InputTokens = 0, long OutputTokens = 0, long UnknownUsage = 0);
public sealed record RequestUsage(string? Model, long? InputTokens, long? OutputTokens);

// Inspect only bounded JSON/SSE events for accounting; request and response content is never retained.
internal sealed class UsageCapture(bool streaming)
{
    private const int Limit = 4 * 1024 * 1024;
    private readonly MemoryStream _buffer = new();
    private bool _overflow;
    public RequestUsage Usage { get; private set; } = new(null, null, null);
    public void Add(ReadOnlySpan<byte> bytes)
    {
        if (!streaming)
        {
            if (_buffer.Length + bytes.Length > Limit) { _overflow = true; _buffer.SetLength(0); }
            if (!_overflow) _buffer.Write(bytes);
            return;
        }
        foreach (var value in bytes)
        {
            if (value == '\n')
            {
                if (!_overflow) ParseLine(_buffer.GetBuffer().AsSpan(0, (int)_buffer.Length));
                _buffer.SetLength(0); _overflow = false;
            }
            else if (!_overflow)
            {
                if (_buffer.Length == Limit) { _overflow = true; _buffer.SetLength(0); }
                else _buffer.WriteByte(value);
            }
        }
    }
    public void Complete()
    {
        if (!_overflow)
        {
            var bytes = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
            if (streaming) ParseLine(bytes); else Parse(bytes);
        }
        _buffer.Dispose();
    }
    private void ParseLine(ReadOnlySpan<byte> line)
    {
        if (line.StartsWith("data:"u8)) Parse(line[5..].Trim((byte)' ').Trim((byte)'\r'));
    }
    private void Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object) root = response;
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object) root = message;
            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : Usage.Model;
            if (model?.Length > 200) model = null;
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) { Usage = Usage with { Model = model }; return; }
            static long? Number(JsonElement e, string a, string b) =>
                (e.TryGetProperty(a, out var v) || e.TryGetProperty(b, out v)) && v.TryGetInt64(out var n) && n >= 0 ? n : null;
            var input = Number(usage, "input_tokens", "prompt_tokens");
            // Anthropic reports cache reads/writes separately from uncached input.
            if (input is not null)
                input += Number(usage, "cache_read_input_tokens", "cache_read_input_tokens") ?? 0;
            if (input is not null)
                input += Number(usage, "cache_creation_input_tokens", "cache_creation_input_tokens") ?? 0;
            Usage = new(model, input ?? Usage.InputTokens, Number(usage, "output_tokens", "completion_tokens") ?? Usage.OutputTokens);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException) { }
    }
}

public sealed class ProviderLedger
{
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, ProviderTotals> _totals = [];
    public string? Error { get; private set; }
    public ProviderLedger(string path)
    {
        _path = path;
        try { if (File.Exists(path)) _totals = JsonSerializer.Deserialize<Dictionary<string, ProviderTotals>>(File.ReadAllText(path)) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Error = "Saved gateway totals could not be read."; }
    }
    public ProviderTotals Get(string id) { lock (_gate) return _totals.GetValueOrDefault(id) ?? new(); }
    public void Record(string id, bool failed, RequestUsage usage)
    {
        lock (_gate)
        {
            var old = _totals.GetValueOrDefault(id) ?? new();
            _totals[id] = new(old.Requests + 1, old.Failed + (failed ? 1 : 0), old.InputTokens + (usage.InputTokens ?? 0), old.OutputTokens + (usage.OutputTokens ?? 0), old.UnknownUsage + (usage.InputTokens is null || usage.OutputTokens is null ? 1 : 0));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_totals));
                File.Move(temp, _path, true); Error = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = "Gateway totals could not be saved."; }
        }
    }
}
