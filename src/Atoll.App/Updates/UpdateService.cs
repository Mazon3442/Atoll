using Velopack;
using Velopack.Sources;

namespace Atoll.Updates;

/// <summary>Result of one update check.</summary>
public sealed record UpdateResult(bool Installed, bool Downloaded, string? Version, string? Error);

/// <summary>
/// Checks the GitHub releases for a newer version and downloads it quietly in the background. The
/// update is applied by <see cref="VelopackApp"/> the next time ATOLL starts, so the running session
/// is never interrupted.
/// </summary>
public static class UpdateService
{
    public const string GitHubUrl = "https://github.com/Mazon3442/Atoll";

    private static UpdateManager Create() => new(new GithubSource(GitHubUrl, accessToken: null, prerelease: false));

    /// <summary>True only when running from a Velopack install (never from a build folder).</summary>
    public static bool IsInstalled => Create().IsInstalled;

    /// <summary>The installed Velopack version, or null when not installed.</summary>
    public static string? InstalledVersion => Create().CurrentVersion?.ToString();

    /// <summary>Version already downloaded and waiting for the next restart, or null.</summary>
    public static string? PendingVersion => Create().UpdatePendingRestart?.Version?.ToString();

    public static async Task<UpdateResult> CheckAndDownloadAsync()
    {
        var manager = Create();
        if (!manager.IsInstalled) return new UpdateResult(false, false, null, null);
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            if (info is null) return new UpdateResult(true, false, null, null);

            await manager.DownloadUpdatesAsync(info);
            Log.Info($"Downloaded update {info.TargetFullRelease?.Version}; it applies on the next restart.");
            return new UpdateResult(true, true, info.TargetFullRelease?.Version?.ToString(), null);
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            return new UpdateResult(true, false, null, ex.Message);
        }
    }
}
