namespace Atoll.Activities;

public enum ActivityPriority
{
    /// <summary>Shown only when nothing else is active (clock). Never shown as the secondary bubble.</summary>
    Idle = 0,
    Ambient = 10,
    Normal = 20,
    High = 30,
    Critical = 40,
}

/// <summary>
/// One thing the island can show. <see cref="Content"/> is a view model; the island picks its
/// look from DataTemplates keyed "{TypeName}.Compact", ".Peek", ".Mini" (see Shell/Templates).
/// </summary>
public sealed class Activity(string id, string moduleId, object content, ActivityPriority priority, TimeSpan? duration = null, bool alert = false)
{
    /// <summary>When it becomes primary, the island opens to its peek view without being hovered.</summary>
    public bool Alert { get; } = alert;

    public string Id { get; } = id;
    public string ModuleId { get; } = moduleId;
    public object Content { get; } = content;
    public ActivityPriority Priority { get; } = priority;

    /// <summary>Null = stays until removed. Otherwise removed automatically after this long.</summary>
    public TimeSpan? Duration { get; } = duration;

    internal DateTimeOffset PostedAt { get; set; }
    internal long Sequence { get; set; }
}

/// <summary>
/// Thread-safe store of live activities. Picks the primary (highest priority, newest first) and an
/// optional secondary from a different module. <see cref="Changed"/> may fire on any thread.
/// </summary>
public sealed class ActivityHost : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Activity> _items = [];
    private readonly TimeProvider _time;
    private readonly ITimer _expiryTimer;
    private long _sequence;

    public ActivityHost(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _expiryTimer = _time.CreateTimer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? Changed;

    /// <summary>Adds the activity, or replaces one with the same id (restarting its duration).</summary>
    public void Post(Activity activity)
    {
        lock (_gate)
        {
            activity.PostedAt = _time.GetUtcNow();
            activity.Sequence = ++_sequence;
            _items[activity.Id] = activity;
            ScheduleExpiryLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.Remove(id);
            ScheduleExpiryLocked();
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveModule(string moduleId)
    {
        int removed;
        lock (_gate)
        {
            var ids = _items.Values.Where(a => a.ModuleId == moduleId).Select(a => a.Id).ToList();
            ids.ForEach(id => _items.Remove(id));
            removed = ids.Count;
            ScheduleExpiryLocked();
        }
        if (removed > 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    public (Activity? Primary, Activity? Secondary) Current
    {
        get
        {
            lock (_gate)
            {
                var ordered = _items.Values
                    .OrderByDescending(a => a.Priority)
                    .ThenByDescending(a => a.Sequence)
                    .ToList();
                var primary = ordered.FirstOrDefault();
                var secondary = primary is null
                    ? null
                    : ordered.FirstOrDefault(a => a.ModuleId != primary.ModuleId && a.Priority > ActivityPriority.Idle);
                return (primary, secondary);
            }
        }
    }

    private void Expire()
    {
        bool removed;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var dead = _items.Values
                .Where(a => a.Duration is { } d && a.PostedAt + d <= now)
                .Select(a => a.Id)
                .ToList();
            dead.ForEach(id => _items.Remove(id));
            removed = dead.Count > 0;
            ScheduleExpiryLocked();
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ScheduleExpiryLocked()
    {
        var next = _items.Values
            .Where(a => a.Duration is not null)
            .Select(a => a.PostedAt + a.Duration!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        if (next == DateTimeOffset.MaxValue)
        {
            _expiryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        var due = next - _time.GetUtcNow();
        _expiryTimer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _expiryTimer.Dispose();
}
