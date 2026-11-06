// Shared by Atoll.ClaudeHook (writer) and Atoll (reader). Keep dependency-free.
using System.Text.Json;

namespace Atoll.Claude;

/// <summary>
/// Compact summary of one Claude Code hook call. The hook exe sends this instead of the raw hook
/// JSON, which can carry whole file contents (e.g. a Write tool's input).
/// </summary>
public sealed record HookMessage
{
    public string Agent { get; init; } = "claude";
    public string Event { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string? Cwd { get; init; }
    public string? Tool { get; init; }

    /// <summary>Short human description of the tool call, e.g. "Editing App.xaml.cs".</summary>
    public string? Detail { get; init; }

    /// <summary>Notification text, e.g. "Claude needs your permission to use Bash".</summary>
    public string? Message { get; init; }

    public string? NotificationType { get; init; }

    /// <summary>Top-level window of the terminal/editor hosting the session (0 if unknown).</summary>
    public long Hwnd { get; init; }
}

public static class HookProtocol
{
    public static readonly string[] Agents = ["claude", "codex", "opencode", "hermes"];

    public static string AgentName(string agent) => agent switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "opencode" => "OpenCode",
        "hermes" => "Hermes",
        _ => agent,
    };

    // Keep the pipe and exe names compatible with existing Claude Code hook installations.
    public const string PipeName = "atoll-claude";
    public const int MaxMessageBytes = 16 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HookMessage FromHookInput(JsonElement root, long hwnd, string agent = "claude")
    {
        var tool = Str(root, "tool_name");
        var input = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tool_input", out var ti) ? ti : default;
        return new HookMessage
        {
            Agent = agent,
            Event = Str(root, "hook_event_name") ?? "",
            SessionId = Str(root, "session_id") ?? "",
            Cwd = Str(root, "cwd"),
            Tool = tool,
            Detail = tool is null ? null : DescribeTool(tool, input),
            Message = Truncate(Str(root, "message"), 300),
            NotificationType = Str(root, "notification_type"),
            Hwnd = hwnd,
        };
    }

    public static string DescribeTool(string tool, JsonElement input)
    {
        string? Arg(string name) => Str(input, name);

        return tool switch
        {
            "Edit" or "MultiEdit" or "Write" or "edit" or "write" or "write_file" => $"Editing {FileName(Arg("file_path") ?? Arg("filePath") ?? Arg("path"))}",
            "apply_patch" => "Applying a patch",
            "NotebookEdit" => $"Editing {FileName(Arg("notebook_path"))}",
            "Read" or "read" or "read_file" => $"Reading {FileName(Arg("file_path") ?? Arg("filePath") ?? Arg("path"))}",
            "Bash" or "PowerShell" or "bash" or "terminal" or "exec_command" or "shell_command" => $"Running {Truncate(FirstLine(Arg("description") ?? Arg("command") ?? Arg("cmd")), 60)}",
            "Grep" or "Glob" or "grep" or "glob" or "file_search" => $"Searching {Truncate(Arg("pattern"), 40)}",
            "WebFetch" => $"Fetching {Host(Arg("url"))}",
            "WebSearch" => $"Searching the web for {Truncate(Arg("query"), 40)}",
            "Task" or "Agent" => Arg("description") is { Length: > 0 } d ? $"Agent: {Truncate(d, 50)}" : "Running an agent",
            "TodoWrite" => "Updating tasks",
            _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => $"Using {tool[(tool.LastIndexOf("__", StringComparison.Ordinal) + 2)..]}",
            _ => $"Using {tool}",
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string FileName(string? path) => string.IsNullOrEmpty(path) ? "a file" : Path.GetFileName(path.TrimEnd('/', '\\'));

    private static string Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "a page";

    private static string FirstLine(string? s) => s is null ? "a command" : s.Split('\n', 2)[0].Trim();

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..(max - 1)] + "…";
}
