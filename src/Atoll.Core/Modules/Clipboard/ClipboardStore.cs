using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atoll.Modules.Clipboard;

public enum ClipboardRecordKind
{
    Text,
    Image,
}

/// <summary>One saved copy. Images travel as PNG bytes so restoring them never depends on a DIB handle.</summary>
public sealed class ClipboardRecord
{
    public ClipboardRecordKind Kind { get; set; }
    public string? Text { get; set; }
    public byte[]? Png { get; set; }
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;

    /// <summary>What the record costs against the history's byte cap.</summary>
    [JsonIgnore]
    public long ByteSize => (Text is null ? 0 : Encoding.UTF8.GetByteCount(Text)) + (Png?.LongLength ?? 0);
}

/// <summary>
/// Saves clipboard history to %LOCALAPPDATA%\Atoll\clipboard.bin, encrypted with DPAPI for the
/// current Windows account. Layout: magic, version, then a DPAPI blob of record JSON. The entropy is
/// unique to this feature, so a ciphertext from settings or another feature cannot be swapped in.
/// Writes are debounced and serialised on one background thread; a corrupt file or one that belongs
/// to another user or PC is deleted rather than read or rewritten in the clear.
/// </summary>
public sealed class ClipboardStore : IDisposable
{
    public const int MaxItems = 100;
    public const long MaxBytes = 25L * 1024 * 1024;

    /// <summary>Captured images larger than this are skipped before they reach the history.</summary>
    public const long MaxImageBytes = 8L * 1024 * 1024;

    /// <summary>How long to wait for more changes before writing, so a burst of copies costs one save.</summary>
    private const int DebounceMs = 300;

    private static readonly byte[] Magic = "ATOLLCLP"u8.ToArray();
    private const int Version = 1;
    private const int HeaderLength = 8 + 4;

    private static readonly byte[] Entropy = "Atoll.Clipboard.v1"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly object _io = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly int _maxItems;
    private readonly long _maxBytes;
    private List<ClipboardRecord>? _pending;
    private Thread? _worker;
    private long _queued;
    private long _written;
    private bool _stopped;

    public ClipboardStore(string? path = null, int maxItems = MaxItems, long maxBytes = MaxBytes)
    {
        FilePath = path ?? DefaultPath;
        _maxItems = Math.Max(1, maxItems);
        _maxBytes = Math.Max(1, maxBytes);
    }

    /// <summary>LocalApplicationData, not Roaming: the ciphertext must not follow the user to another PC.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atoll", "clipboard.bin");

    public string FilePath { get; }

    /// <summary>Newest first. A missing, corrupt or foreign file yields an empty list and is deleted.</summary>
    public List<ClipboardRecord> Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(FilePath)) return [];
        try
        {
            var bytes = File.ReadAllBytes(FilePath);
            if (bytes.Length <= HeaderLength || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                Discard("header");
                return [];
            }
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Magic.Length, 4)) != Version)
            {
                Discard("version");
                return [];
            }

            var json = ProtectedData.Unprotect(bytes[HeaderLength..], Entropy, DataProtectionScope.CurrentUser);
            var records = JsonSerializer.Deserialize<List<ClipboardRecord>>(json, Json) ?? [];
            return Trim(records.Where(IsValid).ToList());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Wrong user/PC (DPAPI), a damaged blob, or a hand-edited file. Drop it rather than guess.
            Log.Error($"Clipboard history could not be read ({ex.GetType().Name}); starting empty");
            DeleteFile();
            return [];
        }
    }

    /// <summary>Queues a snapshot. Only the newest snapshot is written, after a short quiet period.</summary>
    public void Save(IReadOnlyList<ClipboardRecord> records)
    {
        if (!OperatingSystem.IsWindows() || _stopped) return;
        lock (_gate)
        {
            if (_stopped) return;
            _pending = [.. records];
            _queued++;
            if (_worker is null)
            {
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "ATOLL clipboard store" };
                _worker.Start();
            }
        }
        _wake.Set();
    }

    /// <summary>Blocks until every queued save has been written. Used by Clear and by shutdown.</summary>
    public void Flush()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_gate)
        {
            while (!_stopped && (_pending is not null || _written < _queued)) Monitor.Wait(_gate);
        }
    }

    /// <summary>Deletes the file and forgets anything queued. The next save starts from scratch.</summary>
    public void Wipe()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_gate) _pending = null;
        _wake.Set();
        Flush(); // let an in-flight writer finish before its file disappears
        lock (_io) DeleteFile();
    }

    /// <summary>Newest first. Drops the oldest rows once either cap is reached; an oversized row is skipped.</summary>
    internal List<ClipboardRecord> Trim(IReadOnlyList<ClipboardRecord> records)
    {
        var kept = new List<ClipboardRecord>();
        long total = 0;
        foreach (var record in records)
        {
            if (kept.Count >= _maxItems) break;
            var size = record.ByteSize;
            if (size > _maxBytes) continue;
            if (total + size > _maxBytes) break; // the rest are older, and eviction starts at the oldest end
            kept.Add(record);
            total += size;
        }
        return kept;
    }

    private static bool IsValid(ClipboardRecord record) => record.Kind switch
    {
        ClipboardRecordKind.Text => !string.IsNullOrWhiteSpace(record.Text),
        ClipboardRecordKind.Image => record.Png is { Length: > 0 },
        _ => false,
    };

    private void WorkerLoop()
    {
        while (true)
        {
            _wake.WaitOne();
            lock (_gate) if (_stopped) return;

            // Drain the debounce: another copy resets the quiet period. A disposed event means
            // Dispose gave up waiting for an in-flight write, so this thread is done either way.
            try
            {
                while (_wake.WaitOne(DebounceMs))
                {
                    lock (_gate) if (_stopped) return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            List<ClipboardRecord>? snapshot;
            long version;
            lock (_gate)
            {
                if (_stopped) return;
                snapshot = _pending;
                version = _queued;
                _pending = null;
            }

            if (snapshot is not null) Write(snapshot);

            lock (_gate)
            {
                if (version > _written) _written = version;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void Write(List<ClipboardRecord> records)
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only; other builds stay in memory
        var temp = FilePath + ".tmp";
        lock (_io)
        {
            try
            {
                var kept = Trim(records);
                if (kept.Count == 0)
                {
                    DeleteFile();
                    return;
                }

                var json = JsonSerializer.SerializeToUtf8Bytes(kept, Json);
                var blob = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
                var header = new byte[HeaderLength];
                Magic.CopyTo(header, 0);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(Magic.Length), Version);

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(header);
                    stream.Write(blob);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The path appears in the log; record contents never do.
                Log.Error($"Clipboard history could not be saved ({ex.GetType().Name})");
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void Discard(string reason)
    {
        Log.Error($"Clipboard history file was invalid ({reason}); starting empty");
        lock (_io) DeleteFile();
    }

    /// <summary>Deleting is best effort: a locked file still leaves the app working with no history.</summary>
    private void DeleteFile()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Clipboard history could not be deleted ({ex.GetType().Name})");
        }
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        Flush();
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            Monitor.PulseAll(_gate);
        }
        _wake.Set();
        _worker?.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }
}
