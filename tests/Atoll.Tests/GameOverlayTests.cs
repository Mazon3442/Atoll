using Atoll.Activities;
using Atoll.Modules.Claude;
using Atoll.Modules.Games;
using Atoll.Modules.Media;
using Atoll.Settings;
using Atoll.Shell;

namespace Atoll.Tests;

public class GameOverlayTests
{
    private static MediaViewModel Song(bool playing = true) =>
        new(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask)
        { Title = "Midnight City", Artist = "M83", IsPlaying = playing };

    private static ClaudeViewModel Working(ClaudeState state)
    {
        var claude = new ClaudeViewModel(new ClaudeSessionTracker(TimeProvider.System));
        claude.Current = new ClaudeSessionViewModel("s") { Project = "atoll", State = state };
        return claude;
    }

    [Fact]
    public void The_overlay_pill_names_the_song_and_the_claude_session()
    {
        Assert.Equal("Midnight City · M83", OverlayExtras.Music([new Activity("media.now", "media", Song(), ActivityPriority.Normal)]));
        Assert.Equal("", OverlayExtras.Music([new Activity("media.now", "media", Song(playing: false), ActivityPriority.Ambient)]));
        Assert.Equal("Claude working · atoll", OverlayExtras.Claude([new Activity("claude.work", "claude", Working(ClaudeState.Working), ActivityPriority.Normal)]));
        Assert.Equal("atoll needs you", OverlayExtras.Claude([new Activity("claude.work", "claude", Working(ClaudeState.NeedsYou), ActivityPriority.Normal)]));
        Assert.Equal("", OverlayExtras.Claude([]));
    }

    [Theory]
    [InlineData(ForegroundKind.Game, GameIslandMode.Overlay, true)]
    [InlineData(ForegroundKind.Game, GameIslandMode.Hidden, false)]
    [InlineData(ForegroundKind.Fullscreen, GameIslandMode.Overlay, false)]
    [InlineData(ForegroundKind.Normal, GameIslandMode.Overlay, false)]
    public void The_overlay_filter_applies_only_to_a_game_in_front(ForegroundKind foreground, GameIslandMode mode, bool expected)
    {
        var settings = new AppSettings();
        settings.Games.Mode = mode;
        Assert.Equal(expected, OverlayExtras.Applies(settings, foreground));
    }

    [Fact]
    public void Overlay_keeps_pop_ups_when_on_and_drops_everything_of_that_kind_when_off()
    {
        var game = new Activity("games.session", "games", new GameViewModel(), ActivityPriority.High);
        var song = new Activity("media.now", "media", Song(), ActivityPriority.Normal);
        var change = new Activity("media.track", "media", Song(), ActivityPriority.High, TimeSpan.FromSeconds(4), alert: true);
        var work = new Activity("claude.work", "claude", Working(ClaudeState.Working), ActivityPriority.Normal);
        var done = new Activity("claude.done", "claude", new object(), ActivityPriority.High, TimeSpan.FromSeconds(6), alert: true);
        var settings = new AppSettings();

        // On: the chips for the song and the working session go (they are in the pill); the pop-ups stay.
        var (primary, sides) = OverlayExtras.Filter(settings, change, [game, song, work, done]);
        Assert.Same(change, primary);
        Assert.Equal([game, done], sides);

        // Off: nothing about music or Claude, and the game steps up as the primary.
        settings.Games.OverlayShowMusic = false;
        settings.Games.OverlayShowClaude = false;
        (primary, sides) = OverlayExtras.Filter(settings, change, [game, song, work, done]);
        Assert.Same(game, primary);
        Assert.Empty(sides);
    }
}
