using System.Text.Json;
using System.Text.Json.Nodes;

namespace Island.Modules.Claude;

/// <summary>
/// Adds or removes the island's hooks in ~/.claude/settings.json. Other hooks and settings are kept;
/// the file is backed up before every write and never written if it cannot be parsed.
/// </summary>
public static class ClaudeHookInstaller
{
    public static readonly string[] Events =
        ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"];

    private const string Marker = "Island.ClaudeHook";
    private static readonly string[] ToolEvents = ["PreToolUse", "PostToolUse"];

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static string HookExePath => Path.Combine(AppContext.BaseDirectory, "Island.ClaudeHook.exe");

    /// <summary>Forward slashes and quotes so the same string works in Git Bash and cmd.</summary>
    public static string HookCommand => $"\"{HookExePath.Replace('\\', '/')}\"";

    public enum Status
    {
        NotInstalled,
        Installed,
        InstalledElsewhere,
        Unreadable,
    }

    public static Status GetStatus()
    {
        if (!File.Exists(SettingsPath)) return Status.NotInstalled;
        if (TryLoad(out var root) is not null) return Status.Unreadable;
        var commands = OurCommands(root!).ToList();
        if (commands.Count == 0) return Status.NotInstalled;
        return commands.All(c => c == HookCommand) && commands.Count >= Events.Length ? Status.Installed : Status.InstalledElsewhere;
    }

    /// <returns>Null on success, else an error message for the user.</returns>
    public static string? Install() => Update(root =>
    {
        RemoveHooks(root);
        AddHooks(root, HookCommand);
    });

    /// <returns>Null on success, else an error message for the user.</returns>
    public static string? Uninstall() => Update(RemoveHooks);

    internal static void AddHooks(JsonObject root, string command)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }
        foreach (var name in Events)
        {
            if (hooks[name] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[name] = groups;
            }
            var group = new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 5,
                }),
            };
            if (ToolEvents.Contains(name)) group.Insert(0, "matcher", "*");
            groups.Add(group);
        }
    }

    internal static void RemoveHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var (name, node) in hooks.ToList())
        {
            if (node is not JsonArray groups) continue;
            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray list) continue;
                foreach (var hook in list.Where(IsOurs).ToList()) list.Remove(hook);
                if (list.Count == 0) groups.Remove(group);
            }
            if (groups.Count == 0) hooks.Remove(name);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    internal static IEnumerable<string> OurCommands(JsonObject root) =>
        root["hooks"] is JsonObject hooks
            ? hooks.Select(kv => kv.Value).OfType<JsonArray>()
                .SelectMany(groups => groups.OfType<JsonObject>())
                .SelectMany(g => g["hooks"] as JsonArray ?? new JsonArray())
                .Where(IsOurs)
                .Select(h => h!["command"]!.GetValue<string>())
            : [];

    private static bool IsOurs(JsonNode? hook) =>
        hook?["command"] is JsonValue v && v.TryGetValue<string>(out var c) && c.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    private static string? Update(Action<JsonObject> change)
    {
        var error = TryLoad(out var root);
        if (error is not null) return error;
        change(root!);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".island-backup", overwrite: true);
            var temp = SettingsPath + ".island-tmp";
            File.WriteAllText(temp, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, SettingsPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Writing Claude Code settings failed", ex);
            return $"Could not write {SettingsPath}: {ex.Message}";
        }
    }

    private static string? TryLoad(out JsonObject? root)
    {
        root = null;
        try
        {
            if (!File.Exists(SettingsPath))
            {
                root = new JsonObject();
                return null;
            }
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (node is JsonObject obj)
            {
                root = obj;
                return null;
            }
            return $"{SettingsPath} is not a JSON object; left unchanged.";
        }
        catch (JsonException ex)
        {
            return $"{SettingsPath} could not be parsed ({ex.Message}); left unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not read {SettingsPath}: {ex.Message}";
        }
    }
}
