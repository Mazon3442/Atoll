namespace Atoll.Modules.Media;

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
