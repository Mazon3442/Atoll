using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atoll.Modules.Tide.Listen;

/// <summary>One finished or in-progress Listen session. Sessions are JSON files, not part of settings.json.</summary>
internal sealed class ListenSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Ended { get; set; }
    public string Title { get; set; } = "";

    /// <summary>Speaker-labelled transcript lines, in order.</summary>
    public List<ListenLine> Lines { get; set; } = [];

    /// <summary>What the user typed while listening.</summary>
    public string Notes { get; set; } = "";

    public string Summary { get; set; } = "";
    public List<ListenActionItem> ActionItems { get; set; } = [];
    public List<ListenReply> Replies { get; set; } = [];
}

/// <summary>A transcript line. The speaker is free diarization: mic = You, loopback = Others.</summary>
public sealed class ListenLine
{
    public string Speaker { get; set; } = "You";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string Text { get; set; } = "";

    [JsonIgnore]
    public string TimeText => At.LocalDateTime.ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

    [JsonIgnore]
    public bool IsYou => Speaker == "You";
}

public sealed class ListenActionItem
{
    public string Text { get; set; } = "";
    public string Owner { get; set; } = "";

    [JsonIgnore]
    public string OwnerText => string.IsNullOrWhiteSpace(Owner) ? "" : Owner;
}

public sealed class ListenReply
{
    public string Text { get; set; } = "";
    public string Tone { get; set; } = "";
}

/// <summary>Writes one JSON file per session under %APPDATA%\Atoll\listen.</summary>
internal sealed class ListenStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Atoll", "listen");

    public string Directory { get; } = directory;

    /// <summary>Writes atomically and returns the path so the report can point at the file.</summary>
    public string Save(ListenSession session)
    {
        System.IO.Directory.CreateDirectory(Directory);
        session.Ended = DateTimeOffset.Now;
        var path = Path.Combine(this.Directory, $"session-{session.Started:yyyyMMdd-HHmmss}-{ShortId(session.Id)}.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(session));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Newest first; unreadable files are skipped rather than breaking the list.</summary>
    public IReadOnlyList<ListenSession> Recent(int max = 20)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var sessions = new List<ListenSession>();
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("session-*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(max))
        {
            if (Parse(File.ReadAllText(file.FullName)) is { } session) sessions.Add(session);
        }
        return sessions;
    }

    internal static string Serialize(ListenSession session) => JsonSerializer.Serialize(session, Json);

    internal static ListenSession? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<ListenSession>(json, Json); }
        catch (JsonException ex)
        {
            Atoll.Log.Error("Listen session file was invalid", ex);
            return null;
        }
    }

    private static string ShortId(string id) => id.Length <= 6 ? id : id[..6];
}
