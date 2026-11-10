namespace Atoll.Modules.Downloads;

/// <summary>
/// Keeps live size text from redrawing on every write: a new size is shown at most every two seconds,
/// and only once it has moved by half a megabyte or one percent, whichever comes first.
/// </summary>
public sealed class SizeUpdateGate(TimeProvider? time = null)
{
    public const long MinBytes = 512 * 1024;
    public const double MinRatio = 0.01;
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long? _lastBytes;
    private DateTimeOffset _lastAt;

    public bool ShouldPush(long bytes) => ShouldPush(bytes, _time.GetUtcNow());

    internal bool ShouldPush(long bytes, DateTimeOffset now)
    {
        if (_lastBytes is { } last)
        {
            var delta = Math.Abs(bytes - last);
            var moved = delta >= MinBytes || (last > 0 && delta / (double)last >= MinRatio);
            if (!moved || now - _lastAt < MinInterval) return false;
        }
        _lastBytes = bytes;
        _lastAt = now;
        return true;
    }

    public void Reset()
    {
        _lastBytes = null;
        _lastAt = default;
    }
}
