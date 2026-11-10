using System.Runtime.InteropServices;

namespace Atoll.Modules.Downloads;

/// <summary>
/// Finds the folder Windows actually calls Downloads, through the shell's known folder, so a folder moved
/// to OneDrive or another drive resolves to where the browser writes instead of %USERPROFILE%\Downloads.
/// </summary>
public static class DownloadFolder
{
    internal static readonly Guid KnownFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out string path);

    /// <summary>The folder as configured in Windows, or null when it cannot be found or read.</summary>
    public static string? Resolve() => Resolve(_ => FromShell());

    /// <summary>Resolves through <paramref name="lookup"/>, so the mapping can be tested without the shell.</summary>
    internal static string? Resolve(Func<Guid, string?> lookup)
    {
        try
        {
            var path = lookup(KnownFolder);
            return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? null : path;
        }
        catch (Exception ex)
        {
            // A missing shell entry means there is nothing to watch, not a reason to take the island down.
            Log.Info($"Downloads folder could not be resolved: {ex.Message}");
            return null;
        }
    }

    internal static string? FromShell()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return SHGetKnownFolderPath(KnownFolder, 0, 0, out var path) == 0 ? path : null;
    }
}
