using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Atoll.Modules.Providers;

namespace Atoll.Modules.Claude;

/// <summary>Your own saved API key, spoken to directly. No gateway is in the path for an Ask question.</summary>
public sealed class OpenAiChatProvider(ProviderEndpoint endpoint, string model, HttpClient client) : IAgentChatProvider
{
    public string Name => endpoint.Name;

    public AgentChatKind Kind => AgentChatKind.Api;

    public IAsyncEnumerable<AgentChatDelta> StreamAsync(string prompt, CancellationToken ct, IReadOnlyList<AgentChatImage>? images = null) =>
        HttpChat.StreamAsync(client, endpoint, new Uri(endpoint.BaseUri, "chat/completions"), OpenAiBody(prompt, model, images), Name, OpenAiText, ct);

    internal static string OpenAiBody(string prompt, string model, IReadOnlyList<AgentChatImage>? images)
    {
        object content = prompt;
        if (images is { Count: > 0 })
        {
            var parts = new List<object> { new { type = "text", text = prompt } };
            foreach (var image in images)
                parts.Add(new { type = "image_url", image_url = new { url = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" } });
            content = parts;
        }
        return JsonSerializer.Serialize(new { model, stream = true, messages = new[] { new { role = "user", content } } });
    }

    /// <summary>Streaming chunks carry delta.content; a provider that ignores stream sends message.content.</summary>
    internal static string? OpenAiText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0) return null;
        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta) && CliAgent.String(delta, "content") is { Length: > 0 } streamed)
            return streamed;
        if (choice.TryGetProperty("message", out var message) && CliAgent.String(message, "content") is { Length: > 0 } whole)
            return whole;
        return null;
    }
}

/// <summary>An Anthropic API key. Same messages/streaming shape the gateway forwards, without the gateway.</summary>
public sealed class AnthropicChatProvider(ProviderEndpoint endpoint, string model, HttpClient client) : IAgentChatProvider
{
    public string Name => endpoint.Name;

    public AgentChatKind Kind => AgentChatKind.Api;

    public IAsyncEnumerable<AgentChatDelta> StreamAsync(string prompt, CancellationToken ct, IReadOnlyList<AgentChatImage>? images = null) =>
        HttpChat.StreamAsync(client, endpoint, new Uri(endpoint.BaseUri, "messages"), Body(prompt, model, images), Name, Text, ct);

    internal static string Body(string prompt, string model, IReadOnlyList<AgentChatImage>? images)
    {
        object content = prompt;
        if (images is { Count: > 0 })
        {
            var parts = new List<object>();
            foreach (var image in images)
                parts.Add(new { type = "image", source = new { type = "base64", media_type = image.MediaType, data = Convert.ToBase64String(image.Data) } });
            parts.Add(new { type = "text", text = prompt });
            content = parts;
        }
        return JsonSerializer.Serialize(new { model, max_tokens = 2048, stream = true, messages = new[] { new { role = "user", content } } });
    }

    internal static string? Text(JsonElement root)
    {
        if (CliAgent.String(root, "type") == "content_block_delta"
            && root.TryGetProperty("delta", out var delta) && CliAgent.String(delta, "text") is { Length: > 0 } streamed)
            return streamed;
        // A non-streaming fallback response carries a content block array.
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            return string.Concat(content.EnumerateArray()
                .Where(block => CliAgent.String(block, "type") == "text")
                .Select(block => CliAgent.String(block, "text")));
        return null;
    }
}

/// <summary>Shared request, error and server-sent-event plumbing for the two API shapes.</summary>
internal static class HttpChat
{
    public static async IAsyncEnumerable<AgentChatDelta> StreamAsync(HttpClient client, ProviderEndpoint endpoint,
        Uri address, string body, string name, Func<JsonElement, string?> read, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        endpoint.Authorize(request);
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new AgentChatException(await ErrorAsync(response, name, ct));

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // Some OpenAI-compatible servers answer the whole thing even when stream was requested.
            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error)) throw new AgentChatException(ErrorText(error, name));
            if (read(document.RootElement) is { Length: > 0 } text) yield return new AgentChatDelta(text);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var text in ReadAsync(stream, read, name, ct)) yield return new AgentChatDelta(text);
    }

    private static async IAsyncEnumerable<string> ReadAsync(Stream stream, Func<JsonElement, string?> read, string name,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // comments and event: names are ignored
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error)) throw new AgentChatException(ErrorText(error, name));
                if (CliAgent.String(root, "type") == "error" && root.TryGetProperty("error", out var streamed))
                    throw new AgentChatException(ErrorText(streamed, name));
                if (read(root) is { Length: > 0 } text) yield return text;
            }
        }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, string name, CancellationToken ct)
    {
        var body = "";
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new char[2_048];
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            body = new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException) { }

        var detail = TryMessage(body);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{name} rejected the API key. Check it in Settings › Agents.",
            HttpStatusCode.TooManyRequests => $"{name} is rate limiting requests. Try again in a moment.",
            HttpStatusCode.NotFound => $"{name} could not find the model or endpoint. Check the model in Settings › Agents.",
            _ => $"{name} returned HTTP {(int)response.StatusCode}." + (detail is null ? "" : " " + detail),
        };
    }

    private static string? TryMessage(string body)
    {
        if (body.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)) return ErrorText(error, "");
        }
        catch (JsonException) { }
        return null;
    }

    private static string ErrorText(JsonElement error, string name)
    {
        var message = error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : CliAgent.String(error, "message") ?? CliAgent.String(error, "detail");
        message = string.Join(" ", (message ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (message.Length > 200) message = message[..200] + "…";
        return message.Length == 0 ? (name.Length == 0 ? "The provider reported an error." : name + " reported an error.") : message;
    }
}
