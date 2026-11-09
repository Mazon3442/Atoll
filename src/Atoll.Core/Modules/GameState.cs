namespace Atoll.Modules;

/// <summary>Whether a game is running, shared so other modules can stay quiet (do not disturb). Set on the UI thread.</summary>
public static class GameState
{
    public static volatile bool Playing;
}
