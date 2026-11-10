using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Atoll.Modules.Claude;

/// <summary>Runs a CLI once, feeds it the prompt on stdin and hands each stdout line to the agent's parser.</summary>
internal static class CliAgent
{
    public static async IAsyncEnumerable<AgentChatDelta> RunAsync(IProcessRunner runner, ProcessCommand command,
        string name, string prompt, Func<string, (string? Text, string? Error)> read, [EnumeratorCancellation] CancellationToken ct,
        string? stdin = null)
    {
        using var session = runner.Start(command);
        var error = ReadErrorAsync(session.StandardError, ct);
        await session.StandardInput.WriteAsync((stdin ?? prompt).AsMemory(), ct);
        await session.StandardInput.FlushAsync(ct);
        session.StandardInput.Close();

        string? line;
        var produced = false;
        while ((line = await session.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) continue;
            var (text, failure) = read(line);
            if (failure is not null) throw new AgentChatException(failure);
            if (!string.IsNullOrEmpty(text))
            {
                produced = true;
                yield return new AgentChatDelta(text);
            }
        }

        var code = await session.Completion.WaitAsync(ct);
        var details = await error;
        if (code != 0) throw new AgentChatException(Failure(name, code, details, prompt));
        // Some CLIs report a missing sign-in on stderr but still exit zero with no output.
        if (!produced && Clean(details, prompt) is { Length: > 0 } reason) throw new AgentChatException(reason);
    }

    private static async Task<string> ReadErrorAsync(TextReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[512];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, ct)) > 0 && text.Length < 4_000)
                text.Append(buffer, 0, Math.Min(read, 4_000 - text.Length));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        return text.ToString();
    }

    /// <summary>A few useful stderr lines, with the prompt removed in case the CLI echoes it.</summary>
    private static string Failure(string name, int code, string details, string prompt)
    {
        var detail = Clean(details, prompt);
        return detail.Length == 0 ? $"{name} exited with code {code}." : $"{name} exited with code {code}. {detail}";
    }

    private static string Clean(string details, string prompt)
    {
        var lines = details.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(3)
            .ToArray();
        var detail = string.Join(" ", lines);
        if (prompt.Length > 0) detail = detail.Replace(prompt, "[question]", StringComparison.Ordinal);
        return detail.Length > 300 ? detail[..300] + "…" : detail;
    }

    internal static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static bool True(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>Claude Code: JSONL stream events, chat-only via disallowed tools and a single turn.</summary>
public sealed class ClaudeCodeChatProvider(IProcessRunner runner, ProcessCommand command, string? model, bool allowTools) : IAgentChatProvider
{
    public string Name => "Claude Code";

    public AgentChatKind Kind => AgentChatKind.ClaudeCode;

    public IAsyncEnumerable<AgentChatDelta> StreamAsync(string prompt, CancellationToken ct, IReadOnlyList<AgentChatImage>? images = null)
    {
        var arguments = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--no-session-persistence",
        };
        // Pictures travel as base64 in one stream-json user message; plain text on stdin cannot carry them.
        if (images is { Count: > 0 }) { arguments.Add("--input-format"); arguments.Add("stream-json"); }
        if (!string.IsNullOrWhiteSpace(model)) { arguments.Add("--model"); arguments.Add(model.Trim()); }
        if (allowTools)
        {
            // -p cannot ask, so without this every edit or command is denied and "tools on" would do nothing.
            arguments.Add("--permission-mode");
            arguments.Add("bypassPermissions");
        }
        else
        {
            // A single turn with every tool blocked is the only honest chat-only mode Claude Code exposes.
            arguments.Add("--disallowedTools");
            arguments.Add("*");
            arguments.Add("--max-turns");
            arguments.Add("1");
        }
        var parser = new ClaudeParser();
        return CliAgent.RunAsync(runner, command with { Arguments = [.. command.Arguments, .. arguments] }, Name, prompt, parser.Read, ct,
            images is { Count: > 0 } ? UserMessage(prompt, images) : null);
    }

    /// <summary>One line of the stream-json input: the pictures first, then the question.</summary>
    internal static string UserMessage(string prompt, IReadOnlyList<AgentChatImage> images)
    {
        var content = new List<object>();
        foreach (var image in images)
            content.Add(new { type = "image", source = new { type = "base64", media_type = image.MediaType, data = Convert.ToBase64String(image.Data) } });
        content.Add(new { type = "text", text = prompt });
        return JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content } }) + "\n";
    }

    private sealed class ClaudeParser
    {
        private bool _emitted;

        public (string? Text, string? Error) Read(string line)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { return (null, null); }
            using (document)
            {
                var root = document.RootElement;
                var type = CliAgent.String(root, "type");
                if (type == "stream_event")
                {
                    if (!root.TryGetProperty("event", out var streamEvent)
                        || CliAgent.String(streamEvent, "type") != "content_block_delta"
                        || !streamEvent.TryGetProperty("delta", out var delta)
                        || CliAgent.String(delta, "type") != "text_delta") return (null, null);
                    var text = CliAgent.String(delta, "text");
                    if (string.IsNullOrEmpty(text)) return (null, null);
                    _emitted = true;
                    return (text, null);
                }
                if (type == "assistant")
                {
                    // Older CLIs without partial messages send the whole assistant message at once.
                    if (_emitted || !root.TryGetProperty("message", out var message)
                        || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                        return (null, null);
                    var text = string.Concat(content.EnumerateArray()
                        .Where(block => CliAgent.String(block, "type") == "text")
                        .Select(block => CliAgent.String(block, "text")));
                    if (text.Length == 0) return (null, null);
                    _emitted = true;
                    return (text, null);
                }
                if (type == "result")
                {
                    var result = CliAgent.String(root, "result");
                    if (CliAgent.True(root, "is_error") || CliAgent.String(root, "subtype") is not ("success" or null))
                        return (null, "Claude Code could not answer." + (string.IsNullOrWhiteSpace(result) ? "" : " " + Trim(result)));
                    if (!_emitted && !string.IsNullOrWhiteSpace(result))
                    {
                        _emitted = true;
                        return (result, null);
                    }
                }
            }
            return (null, null);
        }
    }

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "…";
}

/// <summary>
/// Codex: JSONL events. There is no trustworthy zero-tool flag, so chat-only runs in a read-only sandbox that can
/// still read files; with tools on it may also edit files in the working folder.
/// </summary>
public sealed class CodexChatProvider(IProcessRunner runner, ProcessCommand command, string? model, bool allowTools = false) : IAgentChatProvider
{
    public string Name => "Codex";

    public AgentChatKind Kind => AgentChatKind.Codex;

    public IAsyncEnumerable<AgentChatDelta> StreamAsync(string prompt, CancellationToken ct, IReadOnlyList<AgentChatImage>? images = null)
    {
        var arguments = new List<string> { "exec", "--json", "--skip-git-repo-check", "--sandbox", allowTools ? "workspace-write" : "read-only" };
        if (!string.IsNullOrWhiteSpace(model)) { arguments.Add("--model"); arguments.Add(model.Trim()); }
        if (images is { Count: > 0 }) return WithImages(arguments, prompt, images, ct);
        arguments.Add("-"); // prompt on stdin
        var parser = new CodexParser();
        return CliAgent.RunAsync(runner, command with { Arguments = [.. command.Arguments, .. arguments] }, Name, prompt, parser.Read, ct);
    }

    /// <summary>Codex takes pictures as files. They live in a private temp folder for the one run and are deleted after.</summary>
    private async IAsyncEnumerable<AgentChatDelta> WithImages(List<string> arguments, string prompt,
        IReadOnlyList<AgentChatImage> images, [EnumeratorCancellation] CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "atoll-ask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            for (var i = 0; i < images.Count; i++)
            {
                var path = Path.Combine(folder, $"image{i + 1}{(images[i].MediaType == "image/jpeg" ? ".jpg" : ".png")}");
                await File.WriteAllBytesAsync(path, images[i].Data, ct);
                arguments.Add("--image");
                arguments.Add(path);
            }
            arguments.Add("--"); // --image takes several values; this keeps the stdin marker from being read as one
            arguments.Add("-");
            var parser = new CodexParser();
            await foreach (var delta in CliAgent.RunAsync(runner, command with { Arguments = [.. command.Arguments, .. arguments] }, Name, prompt, parser.Read, ct))
                yield return delta;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class CodexParser
    {
        public (string? Text, string? Error) Read(string line)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { return (null, null); }
            using (document)
            {
                var root = document.RootElement;
                var type = CliAgent.String(root, "type");
                if (type == "error") return (null, Message(root) ?? "Codex reported an error.");
                if (type == "turn.failed" && root.TryGetProperty("error", out var failed))
                    return (null, Message(failed) ?? "Codex could not finish the answer.");
                if (type == "item.completed" && root.TryGetProperty("item", out var item)
                    && CliAgent.String(item, "type") == "agent_message")
                {
                    var text = CliAgent.String(item, "text");
                    return string.IsNullOrEmpty(text) ? (null, null) : (text, null);
                }
                if (type == "message" && CliAgent.String(root, "role") == "assistant"
                    && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var text = string.Concat(content.EnumerateArray()
                        .Where(block => CliAgent.String(block, "type") is "output_text" or "text")
                        .Select(block => CliAgent.String(block, "text")));
                    return text.Length == 0 ? (null, null) : (text, null);
                }
            }
            return (null, null);
        }

        private static string? Message(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object ? CliAgent.String(element, "message") : null;
    }
}

/// <summary>
/// OpenCode: plain stdout from one run. Its one-shot command has no trustworthy way to turn tools off, so chat-only
/// is a request, not a guarantee; the UI admits that.
/// </summary>
public sealed class OpenCodeChatProvider(IProcessRunner runner, ProcessCommand command, string? model) : IAgentChatProvider
{
    public string Name => "OpenCode";

    public AgentChatKind Kind => AgentChatKind.OpenCode;

    public IAsyncEnumerable<AgentChatDelta> StreamAsync(string prompt, CancellationToken ct, IReadOnlyList<AgentChatImage>? images = null)
    {
        if (images is { Count: > 0 })
            throw new AgentChatException("OpenCode can't take pasted images here. Pick Claude Code, Codex or an API key, or remove the image.");
        var arguments = new List<string> { "run" };
        if (!string.IsNullOrWhiteSpace(model)) { arguments.Add("--model"); arguments.Add(model.Trim()); }
        return Lines(runner, command with { Arguments = [.. command.Arguments, .. arguments] }, prompt, ct);
    }

    private async IAsyncEnumerable<AgentChatDelta> Lines(IProcessRunner runner, ProcessCommand command, string prompt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var delta in CliAgent.RunAsync(runner, command, Name, prompt, line => (line + "\n", null), ct))
            yield return delta;
    }
}
