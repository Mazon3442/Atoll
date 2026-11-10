using System.IO;
using System.Windows.Threading;
using Atoll.Activities;

namespace Atoll.Modules.Downloads;

/// <summary>
/// Shows a dot while the browser downloads, then announces the finished file. Windows gives no download
/// list, so the Downloads folder is watched: a browser's temp name (".crdownload", ".part", ...) starts
/// the download and its rename to the final name finishes it. A file that simply appears is ignored.
/// </summary>
public sealed class DownloadsModule : IIslandModule
{
    private const string ActivityKey = "active";
    // One key for every completion, so two finishing together leave the last one on screen.
    private const string CompleteKey = "complete";

    private readonly DownloadsTracker _tracker = new();
    private readonly SizeUpdateGate _gate = new();
    private readonly DownloadsViewModel _vm = new();
    private readonly object _state = new();
    private ModuleContext? _context;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _sweep;
    private string? _folder;
    private bool _posted;
    private int _shownCount;

    public object? Panel => null;

    public void Start(ModuleContext context)
    {
        _context = context;
        // Folders can be moved to OneDrive while ATOLL runs; re-resolving on any settings change is cheap.
        context.Settings.Changed += OnSettingsChanged;
        Reopen();
    }

    private void OnSettingsChanged() => Post(Reopen);

    private void Reopen()
    {
        if (_context is null) return;
        var folder = DownloadFolder.Resolve();
        if (_watcher is not null && string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase)) return;

        StopWatching();
        _folder = folder;
        if (folder is null) return;

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                Filter = "*",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName,
                InternalBufferSize = 32 * 1024,
            };
            watcher.Created += (_, e) => OnCreated(e.FullPath);
            watcher.Renamed += (_, e) => OnRenamed(e.OldFullPath, e.FullPath);
            watcher.Deleted += (_, e) => OnDeleted(e.FullPath);
            // A full buffer means events were lost; re-check which temp files are still there.
            watcher.Error += (_, _) => Post(Rescan);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Info($"Downloads folder is not being watched: {ex.Message}");
            _watcher = null;
        }
    }

    private void StopWatching()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        lock (_state) _tracker.Clear();
        RemoveActivity();
    }

    private void Rescan()
    {
        lock (_state)
        {
            foreach (var item in _tracker.Active.Where(item => !File.Exists(item.TempPath)).ToList())
                _tracker.Deleted(item.TempPath, Now);
        }
        Refresh(force: true);
    }

    private void OnCreated(string path)
    {
        DownloadCompletion? done;
        bool tracked;
        lock (_state)
        {
            done = _tracker.Created(path, Length(path), Now);
            tracked = _tracker.IsTracking(path);
        }
        // A copy, an unzip or any other new file changes nothing and is never announced.
        if (done is not null) Post(() => Complete(done));
        else if (tracked) Post(() => Refresh(force: true));
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        DownloadCompletion? done;
        lock (_state) done = _tracker.Rename(oldPath, newPath, Length(newPath), Now);
        if (done is not null) Post(() => Complete(done));
        else Post(() => Refresh(force: true));
    }

    private void OnDeleted(string path)
    {
        bool wasActive;
        lock (_state) wasActive = _tracker.Deleted(path, Now);
        if (wasActive) Post(() => Refresh(force: true));
    }

    private void Complete(DownloadCompletion done)
    {
        _context?.Post(CompleteKey, new DownloadCompleteViewModel(done.Path, done.Bytes),
            ActivityPriority.High, TimeSpan.FromSeconds(8), alert: true);
        Refresh(force: true);
    }

    /// <summary>Reads sizes afresh so the text stays live while the browser writes in bursts.</summary>
    private void OnSweep(object? sender, EventArgs e)
    {
        var now = Now;
        bool dropped;
        int count;
        lock (_state)
        {
            dropped = _tracker.Tick(now);
            // Size changes go through the gate in Refresh; only a download leaving the set jumps the queue.
            foreach (var item in _tracker.Active) _tracker.Update(item.TempPath, Length(item.TempPath), now);
            count = _tracker.Count;
        }
        Refresh(force: dropped);
        if (count == 0) _sweep?.Stop();
    }

    private void Refresh(bool force = false)
    {
        if (_context is null) return;
        IReadOnlyList<DownloadSnapshot> active;
        lock (_state) active = _tracker.Active;
        if (active.Count == 0)
        {
            RemoveActivity();
            return;
        }

        var total = active.Sum(item => item.Bytes);
        var count = active.Count;
        // Starting or finishing a download changes the line, so it shows at once; sizes wait for the gate.
        var gateOpen = _gate.ShouldPush(total);
        if (!_posted || force || count != _shownCount || gateOpen)
        {
            _vm.Update(count == 1 ? active[0].Name : "", count, total);
            _shownCount = count;
            if (_posted) return;
            _context.Post(ActivityKey, _vm, ActivityPriority.Ambient);
            _posted = true;
            StartSweep();
        }
    }

    private void RemoveActivity()
    {
        if (_posted)
        {
            _context?.Remove(ActivityKey);
            _posted = false;
        }
        _shownCount = 0;
        _gate.Reset();
        _sweep?.Stop();
    }

    private void StartSweep()
    {
        if (_sweep is null)
        {
            _sweep = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _sweep.Tick += OnSweep;
        }
        if (!_sweep.IsEnabled) _sweep.Start();
    }

    private void Post(Action action) => _context?.Dispatcher.BeginInvoke(action);

    private static long Length(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public void Dispose()
    {
        if (_context is not null) _context.Settings.Changed -= OnSettingsChanged;
        StopWatching();
        _sweep?.Stop();
        _sweep = null;
        _context = null;
    }
}
