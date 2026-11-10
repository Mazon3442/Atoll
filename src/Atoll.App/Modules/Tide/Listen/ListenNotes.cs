using System.Net.Http;
using System.Text;
using System.Text.Json;
using Atoll.Modules.Providers;
using Atoll.Settings;

namespace Atoll.Modules.Tide.Listen;

/// <summary>One choice in the notes-source picker: automatic, Ollama, or a configured provider account.</summary>
public sealed record ListenNotesSource(string Id, string Name);

/// <summary>What a notes pass produced, or why it produced nothing. The previous summary stays on failure.</summary>
internal sealed record ListenNotesAnswer(ListenNotesResult? Result, string Error, bool NeedsModel);

/// <summary>Summary, commitments and suggested next lines, parsed from the model's JSON answer.</summary>
internal sealed record ListenNotesResult(
    string Summary,
    IReadOnlyList<ListenActionItem> ActionItems,
    IReadOnlyList<ListenReply> Replies)
{
    /// <summary>
    /// Tolerates fenced JSON and a model that wraps the object in prose. Returns null when the answer is
    /// not a usable object, so the caller can keep what it already had.
    /// </summary>
    public static ListenNotesResult? Parse(string content)
    {
        var json = ExtractObject(content);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var summary = Text(root, "summary");
            var actions = new List<ListenActionItem>();
            foreach (var item in Array(root, "action_items"))
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString()?.Trim() ?? "";
                    if (text.Length > 0) actions.Add(new ListenActionItem { Text = text });
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object) continue;
                var action = new ListenActionItem { Text = Text(item, "text"), Owner = Text(item, "owner") };
                if (action.Text.Length > 0) actions.Add(action);
            }

            var replies = new List<ListenReply>();
            foreach (var item in Array(root, "replies"))
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString()?.Trim() ?? "";
                    if (text.Length > 0) replies.Add(new ListenReply { Text = text });
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object) continue;
                var reply = new ListenReply { Text = Text(item, "text"), Tone = Text(item, "tone") };
                if (reply.Text.Length > 0) replies.Add(reply);
            }

            if (summary.Length == 0 && actions.Count == 0 && replies.Count == 0) return null;
            return new ListenNotesResult(summary, actions, replies);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractObject(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) text = text[..fence];
        }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : "";
    }

    private static string Text(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()?.Trim() ?? ""
                : property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    ? property.Value.ToString()
                    : "";
        }
        return "";
    }

    private static JsonElement[] Array(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                return property.Value.EnumerateArray().ToArray();
        }
        return [];
    }
}

/// <summary>
/// Decides when a notes pass is due: 300 new characters, or 45 quiet seconds, whichever comes first. A pass
/// that fails still marks the gate so a broken model is not retried on every timer tick.
/// </summary>
internal sealed class NotesGate(DateTimeOffset now)
{
    public static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(45);
    public const int MinimumNewChars = 300;

    private int _lastChars;
    private DateTimeOffset _lastAt = now;

    public bool Due(int chars, DateTimeOffset at)
    {
        if (chars <= _lastChars) return false;
        return chars - _lastChars >= MinimumNewChars || at - _lastAt >= MinimumWait;
    }

    public void Mark(int chars, DateTimeOffset at)
    {
        _lastChars = chars;
        _lastAt = at;
    }
}

/// <summary>
/// The only outbound call in Listen. Prefers a local Ollama on 127.0.0.1, then falls back to the user's
/// configured provider accounts. Nothing is sent until the user has a model and a transcript.
/// </summary>
internal sealed class ListenNotes : IDisposable
{
    internal const string Ollama = "http://127.0.0.1:11434";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(60);
    private const int TranscriptChars = 6000;

    private const string Prompt =
        "You help someone during a live meeting. Reply with JSON only, no markdown, using exactly: " +
        "{\"summary\":\"...\",\"action_items\":[{\"text\":\"...\",\"owner\":\"...\"}],\"replies\":[{\"text\":\"...\",\"tone\":\"...\"}]}. " +
        "summary: at most three sentences about what was actually said. action_items: only commitments that were made; " +
        "use an empty owner when nobody was named. replies: up to three short lines the user could say next, each with a " +
        "one-word tone. Keep every string under 200 characters. Do not invent facts.";

    private readonly HttpClient _http;

    public ListenNotes(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Keeps the tail of the transcript: the newest context matters most and bounds the prompt size.</summary>
    public static string BuildPrompt(IEnumerable<ListenLine> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(line.Speaker).Append(" (").Append(line.At.LocalDateTime.ToString("h:mm tt")).Append("): ").AppendLine(line.Text);
        }
        var all = text.ToString();
        if (all.Length <= TranscriptChars) return all;
        var slice = all[^TranscriptChars..];
        var newline = slice.IndexOf('\n');
        return newline >= 0 ? slice[(newline + 1)..] : slice;
    }

    public async Task<ListenNotesAnswer> AskAsync(string transcript, string notesModel, string providerId,
        IReadOnlyList<ProviderAccount> accounts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return new(null, "There is nothing to summarize yet.", false);

        var wantsOllama = providerId.Length == 0 || providerId == "ollama";
        string? ollamaError = null;
        if (wantsOllama)
        {
            try
            {
                var models = await ListOllamaAsync(ct);
                if (models.Count == 0 && providerId == "ollama")
                    return new(null, "Ollama is running but has no models installed.", true);
                if (models.Count > 0)
                {
                    var model = PickModel(notesModel, models) ?? models[0];
                    var json = await ChatOllamaAsync(model, transcript, ct);
                    var parsed = ListenNotesResult.Parse(json);
                    return parsed is null
                        ? new(null, "Ollama answered without usable JSON. Notes stay as they were.", false)
                        : new(parsed, "", false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
            {
                if (ct.IsCancellationRequested) throw;
                ollamaError = ex is OperationCanceledException ? "Ollama did not answer in time." : "Ollama is not reachable.";
                if (providerId == "ollama")
                    return new(null, ollamaError + " Start it, or choose another notes source.", true);
            }
        }

        var candidates = providerId.Length > 0 && providerId != "ollama"
            ? accounts.Where(a => a.Id == providerId).ToArray()
            : accounts.ToArray();
        if (candidates.Length == 0)
            return new(null, "Notes need a model. Start Ollama, or add a provider key in Settings.", true);

        var failure = ollamaError ?? "The notes model could not be reached. Notes stay as they were.";
        foreach (var account in candidates)
        {
            ProviderEndpoint endpoint;
            try { endpoint = ProviderEndpoint.From(account); }
            catch (ArgumentException) { continue; }
            // Anthropic's Messages API is a different shape; the gateway handles chat/completions providers.
            if (endpoint.IsAnthropic) continue;
            try
            {
                var json = await ChatProviderAsync(endpoint, notesModel, transcript, ct);
                var parsed = ListenNotesResult.Parse(json);
                if (parsed is not null) return new(parsed, "", false);
                failure = "The model answered without usable JSON. Notes stay as they were.";
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
            {
                if (ct.IsCancellationRequested) throw;
                failure = ex is OperationCanceledException
                    ? "The notes model did not answer in time."
                    : "The notes model could not be reached. Notes stay as they were.";
            }
        }
        return new(null, failure, false);
    }

    /// <summary>A cheap local probe used to show the "notes need a model" state before the first pass.</summary>
    public async Task<bool> OllamaAvailableAsync(CancellationToken ct)
    {
        try { return (await ListOllamaAsync(ct)).Count > 0; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return false;
        }
    }

    private async Task<List<string>> ListOllamaAsync(CancellationToken ct)
    {        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        using var response = await _http.GetAsync(Ollama + "/api/tags", timeout.Token);
        if (!response.IsSuccessStatusCode) return [];
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
        var names = new List<string>();
        foreach (var model in models.EnumerateArray())
        {
            if (model.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } value) names.Add(value);
        }
        return names;
    }

    private static string? PickModel(string requested, List<string> installed)
    {
        if (string.IsNullOrWhiteSpace(requested)) return installed.FirstOrDefault();
        var name = requested.Trim();
        return installed.FirstOrDefault(m => m.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(m => m.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault();
    }

    private async Task<string> ChatOllamaAsync(string model, string transcript, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ChatTimeout);
        var body = JsonSerializer.Serialize(new
        {
            model,
            stream = false,
            format = "json",
            messages = new[] { new { role = "system", content = Prompt }, new { role = "user", content = transcript } },
            options = new { temperature = 0.2 },
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Ollama + "/api/chat", content, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return doc.RootElement.TryGetProperty("message", out var message) &&
               message.TryGetProperty("content", out var answer)
            ? answer.GetString() ?? ""
            : "";
    }

    private async Task<string> ChatProviderAsync(ProviderEndpoint endpoint, string model, string transcript, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ChatTimeout);
        var body = JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(model) ? DefaultModel(endpoint) : model.Trim(),
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new[] { new { role = "system", content = Prompt }, new { role = "user", content = transcript } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUri, "chat/completions"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.Key);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
            ? content.GetString() ?? ""
            : "";
    }

    private static string DefaultModel(ProviderEndpoint endpoint) =>
        endpoint.IsDeepSeek ? "deepseek-chat" : "gpt-4o-mini";

    public void Dispose() => _http.Dispose();
}
