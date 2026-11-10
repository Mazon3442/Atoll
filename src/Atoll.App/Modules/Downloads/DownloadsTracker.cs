namespace Atoll.Modules.Downloads;

/// <summary>One temp file a browser is writing.</summary>
public sealed record DownloadSnapshot(string TempPath, string Name, long Bytes);

/// <summary>A download that finished: the browser renamed the temp file to <paramref name="Path"/>.</summary>
public sealed record DownloadCompletion(string Path, long Bytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Decides which files in the Downloads folder are real downloads and when one has finished.
/// Only a browser's temp name starts a download, so a copy or an unzip never announces itself; completion
/// is the browser renaming that temp name to the final one (or, for browsers that delete and create,
/// the same stem appearing within <see cref="MatchWindow"/>).
/// </summary>
public sealed class DownloadsTracker
{
    /// <summary>How long a deleted temp name can wait for its final file before it is given up on.</summary>
    public static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(10);

    /// <summary>A browser that crashed leaves its temp file behind; stop showing it after this long unwritten.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    internal static readonly string[] TempExtensions =
        [".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp"];

    private sealed class Tracked(string tempPath, string name, long bytes, DateTimeOffset changed)
    {
        public string TempPath { get; } = tempPath;
        public string Name { get; } = name;
        public long Bytes { get; set; } = bytes;
        public DateTimeOffset Changed { get; set; } = changed;
    }

    private sealed class Pending(string name, long bytes, DateTimeOffset at)
    {
        public string Name { get; } = name;
        public long Bytes { get; } = bytes;
        public DateTimeOffset At { get; } = at;
    }

    private readonly Dictionary<string, Tracked> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Pending> _pending = [];

    public int Count => _active.Count;

    public IReadOnlyList<DownloadSnapshot> Active => _active.Values
        .Select(item => new DownloadSnapshot(item.TempPath, item.Name, item.Bytes))
        .ToList();

    public bool IsTracking(string path) => _active.ContainsKey(path);

    /// <summary>True only for the names browsers use while a download is still being written.</summary>
    public static bool IsTemporary(string path) =>
        TempExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the temp file will be called: "report.pdf.crdownload" → "report.pdf".</summary>
    public static string NameFor(string tempPath) => System.IO.Path.GetFileNameWithoutExtension(tempPath);

    public bool Start(string path, long bytes, DateTimeOffset now)
    {
        if (!IsTemporary(path)) return false;
        if (_active.ContainsKey(path)) return false;
        _active[path] = new Tracked(path, NameFor(path), bytes, now);
        return true;
    }

    /// <summary>Records a new size. False when the file is not tracked or the size did not move.</summary>
    public bool Update(string path, long bytes, DateTimeOffset now)
    {
        if (!_active.TryGetValue(path, out var item) || item.Bytes == bytes) return false;
        item.Bytes = bytes;
        item.Changed = now;
        return true;
    }

    /// <summary>
    /// The browser's own finish. The temp name loses its extension, so "report.pdf.crdownload" becomes
    /// "report.pdf". A rename to any other name stops tracking quietly, without an alert.
    /// </summary>
    public DownloadCompletion? Rename(string oldPath, string newPath, long bytes, DateTimeOffset now)
    {
        if (!_active.TryGetValue(oldPath, out var item))
        {
            // Some browsers first rename another temp name into the one they write.
            Start(newPath, bytes, now);
            return null;
        }
        _active.Remove(oldPath);
        if (!string.Equals(item.Name, System.IO.Path.GetFileName(newPath), StringComparison.OrdinalIgnoreCase))
            return null;
        return new DownloadCompletion(newPath, bytes > 0 ? bytes : item.Bytes);
    }

    /// <summary>Remembers a temp file that vanished, so its final file can still complete it.</summary>
    public bool Deleted(string path, DateTimeOffset now)
    {
        if (!_active.Remove(path, out var item)) return false;
        _pending.Add(new Pending(item.Name, item.Bytes, now));
        Prune(now);
        return true;
    }

    /// <summary>A file appeared. A temp name starts a download; a final name only completes a pending one.</summary>
    public DownloadCompletion? Created(string path, long bytes, DateTimeOffset now)
    {
        if (IsTemporary(path))
        {
            Start(path, bytes, now);
            return null;
        }
        var name = System.IO.Path.GetFileName(path);
        var match = _pending.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            && now - p.At <= MatchWindow);
        if (match is null) return null;
        _pending.Remove(match);
        return new DownloadCompletion(path, bytes > 0 ? bytes : match.Bytes);
    }

    /// <summary>Drops abandoned temp files and stale Deleted/Created pairs. True when a download left the set.</summary>
    public bool Tick(DateTimeOffset now)
    {
        var stale = _active.Where(pair => now - pair.Value.Changed > IdleTimeout).Select(pair => pair.Key).ToList();
        stale.ForEach(path => _active.Remove(path));
        Prune(now);
        return stale.Count > 0;
    }

    private void Prune(DateTimeOffset now) => _pending.RemoveAll(p => now - p.At > MatchWindow);

    public void Clear()
    {
        _active.Clear();
        _pending.Clear();
    }
}
