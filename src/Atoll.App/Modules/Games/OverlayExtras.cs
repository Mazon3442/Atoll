using Atoll.Activities;
using Atoll.Modules.Claude;
using Atoll.Modules.Media;
using Atoll.Settings;
using Atoll.Shell;

namespace Atoll.Modules.Games;

/// <summary>What the game overlay adds to the game pill (the song, running Claude sessions) and what it hides.</summary>
internal static class OverlayExtras
{
    /// <summary>"Title · Artist" of the song that is playing, or empty.</summary>
    internal static string Music(IEnumerable<Activity> running) =>
        running.Select(a => a.Content).OfType<MediaViewModel>().FirstOrDefault(m => m.IsPlaying && m.Title.Length > 0) is { } media
            ? media.Artist.Length > 0 ? $"{media.Title} · {media.Artist}" : media.Title
            : "";

    /// <summary>The Claude session that is working or needs you, or empty.</summary>
    internal static string Claude(IEnumerable<Activity> running) =>
        running.Select(a => a.Content).OfType<ClaudeViewModel>().FirstOrDefault() is { Current: { IsActive: true } session }
            ? session.NeedsYou ? $"{session.Project} needs you" : $"Claude working · {session.Project}"
            : "";

    /// <summary>True while a game is in front and the overlay is on.</summary>
    internal static bool Applies(AppSettings settings, ForegroundKind foreground) =>
        foreground == ForegroundKind.Game && settings.Games.Mode == GameIslandMode.Overlay;

    /// <summary>
    /// In the overlay the song and Claude sit inside the game pill, so their chips go. Their pop-ups (a new song,
    /// a session that finished or needs you) stay when the matching setting is on and go when it is off.
    /// The next activity steps up when the primary one is dropped.
    /// </summary>
    internal static (Activity? Primary, IReadOnlyList<Activity> Sides) Filter(AppSettings settings, Activity? primary, IReadOnlyList<Activity> sides)
    {
        var games = settings.Games;
        bool Keep(Activity a) => a.ModuleId switch
        {
            "media" => games.OverlayShowMusic && a.Alert,
            "claude" => games.OverlayShowClaude && a.Content is not ClaudeViewModel, // the "work" chip is the pill text
            _ => true,
        };
        var all = (primary is null ? sides : [primary, .. sides]).Where(Keep).ToList();
        return (all.FirstOrDefault(), all.Skip(1).ToList());
    }
}
