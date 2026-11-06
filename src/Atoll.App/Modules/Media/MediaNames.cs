namespace Atoll.Modules.Media;

/// <summary>
/// Sources seen recently. Some players close and reopen their media session on every track change; keeping a
/// source for a short grace period stops the picker from dropping it (and jumping to another app) in that gap.
/// </summary>
internal sealed class RecentSources(TimeSpan grace)
{
    private readonly Dictionary<string, (string Name, DateTimeOffset Seen)> _items = [];

    /// <summary>Records the live sources and returns them plus any that closed within the grace period.</summary>
    public IReadOnlyList<MediaSourceChoice> Update(IEnumerable<MediaSourceChoice> live, DateTimeOffset now)
    {
        foreach (var source in live) _items[source.Id] = (source.Name, now);
        foreach (var id in _items.Where(i => now - i.Value.Seen > grace).Select(i => i.Key).ToList()) _items.Remove(id);
        return _items.Select(i => new MediaSourceChoice(i.Key, i.Value.Name)).ToList();
    }

    public bool Contains(string id) => _items.ContainsKey(id);
}

public sealed partial class MediaModule
{
    /// <summary>
    /// Whether to switch to the player the system calls current or playing. Only a newly current player that is
    /// playing takes over what is shown; a paused one (a YouTube tab during a track change) does not. A user's pick
    /// holds until another player starts playing.
    /// </summary>
    internal static bool ShouldFollow(string? candidateId, bool candidatePlaying, string? followedId, bool showing) =>
        !showing || (candidateId is not null && candidateId != followedId && candidatePlaying);

    internal static string FriendlyAppName(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var name = aumid.Contains('!') ? aumid[(aumid.LastIndexOf('!') + 1)..] : aumid;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return name[..^4];
        // Reverse-domain IDs such as "com.wimpysworld.sidra" name the app in their last segment.
        var parts = name.Split('.');
        if (parts.Length >= 3 && parts[0].Length is >= 2 and <= 4 && parts[0].All(char.IsAsciiLetterLower) && parts[^1].Length > 0)
        {
            var app = parts[^1];
            return app.All(c => !char.IsUpper(c)) ? char.ToUpperInvariant(app[0]) + app[1..] : app;
        }
        return name;
    }
}
