using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atoll.Modules.Claude;

/// <summary>
/// Adds or removes the island's hooks in ~/.claude/settings.json. Other hooks and settings are kept;
/// the file is backed up before every write and never written if it cannot be parsed.
/// </summary>
public static class ClaudeHookInstaller
{
    public static readonly string[] Events =
        ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"];

    private const string Marker = "Atoll.ClaudeHook";
    private static readonly string[] ToolEvents = ["PreToolUse", "PostToolUse"];

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static string HookExePath => Path.Combine(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "Atoll.ClaudeHook.exe" : "Atoll.ClaudeHook");

    /// <summary>Forward slashes and quotes so the same string works in Git Bash and cmd.</summary>
    public static string HookCommand => QuoteCommand(HookExePath);

    internal static string QuoteCommand(string path) => OperatingSystem.IsWindows()
        ? $"\"{path.Replace('\\', '/')}\""
        : "'" + path.Replace("'", "'\"'\"'") + "'";

    public enum Status
    {
        NotInstalled,
        Installed,
        InstalledElsewhere,
        Unreadable,
    }

    public static Status GetStatus(string? path = null, string? command = null, int? eventCount = null)
    {
        path ??= SettingsPath;
        command ??= HookCommand;
        if (!File.Exists(path)) return Status.NotInstalled;
        if (TryLoad(out var root, path) is not null) return Status.Unreadable;
        var commands = OurCommands(root!).ToList();
        if (commands.Count == 0) return Status.NotInstalled;
        return commands.All(c => c == command) && commands.Count >= (eventCount ?? Events.Length) ? Status.Installed : Status.InstalledElsewhere;
    }

    /// <returns>Null on success, else an error message for the user.</returns>
    public static string? Install() => Update(root =>
    {
        RemoveHooks(root);
        AddHooks(root, HookCommand);
    });

    /// <returns>Null on success, else an error message for the user.</returns>
    public static string? Uninstall() => Update(RemoveHooks);

    internal static void AddHooks(JsonObject root, string command, IReadOnlyList<string>? events = null)
    {
        if (root["hooks"] is not null and not JsonObject)
            throw new InvalidDataException("The hooks section is not an object; left unchanged.");
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }
        foreach (var name in events ?? Events)
        {
            if (hooks[name] is not null and not JsonArray)
                throw new InvalidDataException($"The {name} hooks are not an array; left unchanged.");
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
                    ["timeout"] = name == "Interrupt" ? 1 : 5,
                }),
            };
            if (ToolEvents.Contains(name) || name == "PermissionRequest") group.Insert(0, "matcher", "*");
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

    internal static string? Update(Action<JsonObject> change, string? path = null)
    {
        path ??= SettingsPath;
        try
        {
            var original = File.Exists(path) ? File.ReadAllText(path) : null;
            var error = TryLoad(out var root, path);
            if (error is not null) return error;
            change(root!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if ((File.Exists(path) ? File.ReadAllText(path) : null) != original)
                return $"{path} changed during setup; left unchanged. Try again.";
            if (File.Exists(path)) File.Copy(path, path + ".atoll-backup", overwrite: true);
            var temp = path + ".atoll-tmp";
            File.WriteAllText(temp, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("Writing agent hooks failed", ex);
            return $"Could not write {path}: {ex.Message}";
        }
    }

    private static string? TryLoad(out JsonObject? root, string? path = null)
    {
        path ??= SettingsPath;
        root = null;
        try
        {
            if (!File.Exists(path))
            {
                root = new JsonObject();
                return null;
            }
            var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (node is JsonObject obj)
            {
                root = obj;
                return null;
            }
            return $"{path} is not a JSON object; left unchanged.";
        }
        catch (JsonException ex)
        {
            return $"{path} could not be parsed ({ex.Message}); left unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not read {path}: {ex.Message}";
        }
    }
}
