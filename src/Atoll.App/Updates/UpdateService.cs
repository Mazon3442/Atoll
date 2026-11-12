using Velopack;
using Velopack.Sources;

namespace Atoll.Updates;

/// <summary>Result of one update check.</summary>
public sealed record UpdateResult(bool Installed, bool Downloaded, string? Version, string? Error);

/// <summary>
/// Checks the GitHub releases for a newer version and downloads it quietly in the background. The
/// update is applied by the caller right after the download (ATOLL closes, installs and reopens), or by
/// <see cref="VelopackApp"/> the next time ATOLL starts if that restart could not be started.
/// </summary>
public static class UpdateService
{
    public const string GitHubUrl = "https://github.com/Mazon3442/Atoll";

    private static UpdateManager Create() => new(new GithubSource(GitHubUrl, accessToken: null, prerelease: false));

    /// <summary>True only when running from a Velopack install (never from a build folder).</summary>
    public static bool IsInstalled => OperatingSystem.IsWindows() && Create().IsInstalled;

    /// <summary>The installed Velopack version, or null when not installed.</summary>
    public static string? InstalledVersion => Create().CurrentVersion?.ToString();

    /// <summary>Version already downloaded and waiting for the next restart, or null.</summary>
    public static string? PendingVersion => Create().UpdatePendingRestart?.Version?.ToString();

    /// <summary>
    /// Installs the downloaded update once this process has exited, then starts the new version. The caller must
    /// shut ATOLL down right after; returns false when there is nothing downloaded to apply.
    /// </summary>
    public static bool ApplyOnExitAndRestart()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var manager = Create();
        if (!manager.IsInstalled || manager.UpdatePendingRestart is not { } pending) return false;
        try
        {
            manager.WaitExitThenApplyUpdates(pending, silent: false, restart: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the update", ex);
            return false;
        }
    }

    public static async Task<UpdateResult> CheckAndDownloadAsync()
    {
        if (!OperatingSystem.IsWindows()) return new UpdateResult(false, false, null, null);
        var manager = Create();
        if (!manager.IsInstalled) return new UpdateResult(false, false, null, null);
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            if (info is null) return new UpdateResult(true, false, null, null);

            await manager.DownloadUpdatesAsync(info);
            Log.Info($"Downloaded update {info.TargetFullRelease?.Version}; installing it now.");
            return new UpdateResult(true, true, info.TargetFullRelease?.Version?.ToString(), null);
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            return new UpdateResult(true, false, null, ex.Message);
        }
    }
}
