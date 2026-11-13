using System.Collections.ObjectModel;
using System.Globalization;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Clock;

public enum RingState
{
    Idle,
    Ringing,
    Snoozed,
}

/// <summary>One weekday button in the alarm editor.</summary>
public sealed partial class DayToggle(DayOfWeek day, string name, Action changed) : ObservableObject
{
    public DayOfWeek Day { get; } = day;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isOn;
    partial void OnIsOnChanged(bool value) => changed();
}

/// <summary>One saved alarm, as a row in the list.</summary>
public sealed partial class AlarmItem : ObservableObject
{
    private readonly AlarmViewModel _owner;

    internal AlarmItem(AlarmViewModel owner, AlarmEntry entry)
    {
        _owner = owner;
        Entry = entry;
    }

    internal AlarmEntry Entry { get; }

    public string TimeText => AlarmViewModel.FormatTime(new TimeOnly(Entry.Hour, Entry.Minute));
    public string Title => Entry.Label.Length > 0 ? Entry.Label : "Alarm";
    public string RepeatText => AlarmViewModel.RepeatName(Entry.Days);

    public bool IsEnabled
    {
        get => Entry.Enabled;
        set
        {
            if (Entry.Enabled == value) return;
            Entry.Enabled = value;
            OnPropertyChanged();
            _owner.Changed();
        }
    }

    internal void Refresh()
    {
        foreach (var name in new[] { nameof(TimeText), nameof(Title), nameof(RepeatText), nameof(IsEnabled) }) OnPropertyChanged(name);
    }

    [RelayCommand] private void Edit() => _owner.BeginEdit(this);
    [RelayCommand] private void Delete() => _owner.Remove(this);
}

/// <summary>
/// The Alarm section: type a time or pick a preset, choose repeat days, save to a list of alarms. Rings and snoozes
/// from <see cref="Tick"/>. Time comes from <see cref="TimeProvider"/>. UI thread only.
/// </summary>
public sealed partial class AlarmViewModel : ObservableObject
{
    public const int SnoozeMinutes = 9;

    private readonly TimeProvider _time;
    private List<AlarmEntry> _entries = [];
    private Action? _save;
    private TimeOnly _editTime;
    private AlarmItem? _editing;
    private DateTimeOffset _lastCheck;
    private DateTimeOffset _snoozeUntil;
    private AlarmItem? _ringing;

    public AlarmViewModel(TimeProvider time)
    {
        _time = time;
        _lastCheck = time.GetLocalNow();
        // Start the editor at the next five minutes, on weekdays-off (rings once).
        var now = _lastCheck;
        _editTime = new TimeOnly(now.Hour, now.Minute).AddMinutes(5 - now.Minute % 5);
        var names = new[] { ("M", DayOfWeek.Monday), ("T", DayOfWeek.Tuesday), ("W", DayOfWeek.Wednesday), ("T", DayOfWeek.Thursday),
            ("F", DayOfWeek.Friday), ("S", DayOfWeek.Saturday), ("S", DayOfWeek.Sunday) };
        Days = new ObservableCollection<DayToggle>(names.Select(n => new DayToggle(n.Item2, n.Item1, OnDaysChanged)));
    }

    /// <summary>Loads the saved alarms; every change is written back through <paramref name="save"/>.</summary>
    public void Use(List<AlarmEntry> entries, Action? save = null)
    {
        _entries = entries;
        _save = save;
        Items.Clear();
        foreach (var entry in entries) Items.Add(new AlarmItem(this, entry));
        _lastCheck = _time.GetLocalNow();
        OnPropertyChanged(nameof(HasAlarms));
    }

    public ObservableCollection<AlarmItem> Items { get; } = [];
    public ObservableCollection<DayToggle> Days { get; }
    public bool HasAlarms => Items.Count > 0;

    /// <summary>Quick picks under the time. The last one is eight hours from now, for sleep.</summary>
    public IReadOnlyList<string> Presets { get; } = ["6:00", "7:30", "8:00", "In 8h"];

    [ObservableProperty] private string _label = "";

    // ---------- the editor ----------

    /// <summary>The time as typed or shown, "7:30". Typing "730", "7 30", "19:45" or "7:30pm" all work.</summary>
    public string TimeInput
    {
        get => $"{(_editTime.Hour % 12 == 0 ? 12 : _editTime.Hour % 12)}:{_editTime.Minute:00}";
        set
        {
            if (TryParse(value, _editTime.Hour >= 12, out var parsed)) SetTime(parsed);
            else OnPropertyChanged(); // put the old text back
        }
    }

    /// <summary>0 for am, 1 for pm.</summary>
    public int PmIndex
    {
        get => _editTime.Hour >= 12 ? 1 : 0;
        set => SetTime(new TimeOnly(_editTime.Hour % 12 + (value == 1 ? 12 : 0), _editTime.Minute));
    }

    public string SaveLabel => _editing is null ? "Save alarm" : "Update alarm";

    /// <summary>"Rings tomorrow at 7:30 AM · in 11h 04m".</summary>
    public string Summary
    {
        get
        {
            var now = _time.GetLocalNow();
            var at = Next(_editTime, DaysMask(), now, _time.LocalTimeZone);
            var day = at.Date == now.Date ? "today" : at.Date == now.Date.AddDays(1) ? "tomorrow" : at.ToString("dddd", CultureInfo.CurrentCulture);
            return $"Rings {day} at {FormatTime(_editTime)} · in {FormatSpan(at - now)}";
        }
    }

    private void SetTime(TimeOnly time)
    {
        _editTime = time;
        OnPropertyChanged(nameof(TimeInput));
        OnPropertyChanged(nameof(PmIndex));
        OnPropertyChanged(nameof(Summary));
    }

    private void OnDaysChanged() => OnPropertyChanged(nameof(Summary));

    private int DaysMask() => Days.Where(d => d.IsOn).Aggregate(0, (mask, d) => mask | 1 << (int)d.Day);

    private void SetDays(Func<DayOfWeek, bool> on)
    {
        foreach (var day in Days) day.IsOn = on(day.Day);
    }

    [RelayCommand] private void RepeatOnce() => SetDays(_ => false);
    [RelayCommand] private void RepeatWeekdays() => SetDays(d => d is >= DayOfWeek.Monday and <= DayOfWeek.Friday);
    [RelayCommand] private void RepeatEveryDay() => SetDays(_ => true);

    [RelayCommand] private void AddMinute() => SetTime(_editTime.AddMinutes(1));
    [RelayCommand] private void SubtractMinute() => SetTime(_editTime.AddMinutes(-1));
    [RelayCommand] private void AddHour() => SetTime(_editTime.AddHours(1));
    [RelayCommand] private void SubtractHour() => SetTime(_editTime.AddHours(-1));

    [RelayCommand]
    private void UsePreset(string? preset)
    {
        if (preset is "In 8h")
        {
            var at = _time.GetLocalNow().AddHours(8);
            var time = new TimeOnly(at.Hour, at.Minute);
            SetTime(time.AddMinutes((5 - time.Minute % 5) % 5)); // to the next five minutes
        }
        else if (preset is not null && TryParse(preset, false, out var parsed)) SetTime(parsed);
    }

    [RelayCommand]
    private void Save()
    {
        var entry = _editing?.Entry ?? new AlarmEntry();
        entry.Hour = _editTime.Hour;
        entry.Minute = _editTime.Minute;
        entry.Days = DaysMask();
        entry.Label = Label.Trim();
        entry.Enabled = true;
        if (_editing is { } item) item.Refresh();
        else
        {
            _entries.Add(entry);
            Items.Add(new AlarmItem(this, entry));
            OnPropertyChanged(nameof(HasAlarms));
        }
        _editing = null;
        OnPropertyChanged(nameof(SaveLabel));
        Changed();
    }

    internal void BeginEdit(AlarmItem item)
    {
        _editing = item;
        Label = item.Entry.Label;
        SetDays(d => (item.Entry.Days & 1 << (int)d) != 0);
        SetTime(new TimeOnly(item.Entry.Hour, item.Entry.Minute));
        OnPropertyChanged(nameof(SaveLabel));
    }

    internal void Remove(AlarmItem item)
    {
        if (_editing == item)
        {
            _editing = null;
            OnPropertyChanged(nameof(SaveLabel));
        }
        _entries.Remove(item.Entry);
        Items.Remove(item);
        OnPropertyChanged(nameof(HasAlarms));
        if (_ringing == item) Dismiss();
        Changed();
    }

    internal void Changed() => _save?.Invoke();

    // ---------- ringing ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRinging), nameof(IsSnoozed))]
    private RingState _state;

    public bool IsRinging => State == RingState.Ringing;
    public bool IsSnoozed => State == RingState.Snoozed;

    [ObservableProperty] private string _ringTitle = "";
    [ObservableProperty] private string _ringDetail = "";

    /// <summary>"8:41" while snoozed: the time until it rings again.</summary>
    [ObservableProperty] private string _snoozeText = "";

    /// <summary>Raised when ringing or snoozing starts or ends, so the module can show or remove the activity.</summary>
    public event Action? StateChanged;

    /// <summary>Call a few times a second. Rings alarms that came due since the last call, and ends a snooze.</summary>
    public void Tick()
    {
        var now = _time.GetLocalNow();
        var since = _lastCheck;
        _lastCheck = now;
        if (State == RingState.Snoozed)
        {
            var left = _snoozeUntil - now;
            if (left <= TimeSpan.Zero) Ring(_ringing!);
            else SnoozeText = FormatSpan(left, seconds: true);
        }
        if (State != RingState.Idle) return;

        foreach (var item in Items)
        {
            var entry = item.Entry;
            if (!entry.Enabled) continue;
            if (Next(new TimeOnly(entry.Hour, entry.Minute), entry.Days, since, _time.LocalTimeZone) > now) continue;
            _ringing = item;
            Ring(item);
            // A one-time alarm is spent once it rings; a repeating one stays on for its next day.
            if (entry.Days == 0)
            {
                entry.Enabled = false;
                item.Refresh();
                Changed();
            }
            return;
        }
    }

    private void Ring(AlarmItem item)
    {
        _ringing = item;
        RingTitle = item.Title;
        RingDetail = $"{item.TimeText} · {item.RepeatText}";
        State = RingState.Ringing;
        StateChanged?.Invoke();
    }

    [RelayCommand]
    private void Snooze()
    {
        if (State != RingState.Ringing) return;
        _snoozeUntil = _time.GetLocalNow().AddMinutes(SnoozeMinutes);
        SnoozeText = FormatSpan(_snoozeUntil - _time.GetLocalNow(), seconds: true);
        State = RingState.Snoozed;
        StateChanged?.Invoke();
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (State == RingState.Idle) return;
        _ringing = null;
        State = RingState.Idle;
        StateChanged?.Invoke();
    }

    // ---------- time maths ----------

    /// <summary>The first moment after <paramref name="after"/> that the clock shows <paramref name="time"/> on an allowed day.</summary>
    internal static DateTimeOffset Next(TimeOnly time, int days, DateTimeOffset after, TimeZoneInfo zone)
    {
        for (var offset = 0; offset <= 7; offset++)
        {
            var local = after.DateTime.Date.AddDays(offset) + time.ToTimeSpan();
            if (days != 0 && (days & 1 << (int)local.DayOfWeek) == 0) continue;
            var at = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (at > after) return at;
        }
        return after.AddDays(1); // not reached: a day is always allowed within a week
    }

    internal static bool TryParse(string? text, bool pmDefault, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().ToLowerInvariant().Replace(".", ":");
        bool? pm = null;
        if (s.EndsWith('p') || s.EndsWith("pm")) pm = true;
        else if (s.EndsWith('a') || s.EndsWith("am")) pm = false;
        s = s.TrimEnd('a', 'p', 'm', ' ');
        int hour, minute;
        var parts = s.Split([':', ' ', 'h'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2 || parts.Any(p => !p.All(char.IsAsciiDigit) || p.Length > 4)) return false;
        if (parts.Length == 2)
        {
            hour = int.Parse(parts[0], CultureInfo.InvariantCulture);
            minute = int.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        else
        {
            var n = int.Parse(parts[0], CultureInfo.InvariantCulture);
            if (parts[0].Length <= 2) (hour, minute) = (n, 0);
            else (hour, minute) = (n / 100, n % 100);
        }
        if (hour > 23 || minute > 59) return false;
        if (pm is null && hour is >= 1 and <= 12) pm = pmDefault; // "7:30" keeps whichever half is chosen
        if (pm == true && hour < 12) hour += 12;
        else if (pm == false && hour == 12) hour = 0;
        time = new TimeOnly(hour, minute);
        return true;
    }

    internal static string FormatTime(TimeOnly time) => time.ToString("h:mm tt", CultureInfo.CurrentCulture);

    internal static string FormatSpan(TimeSpan span, bool seconds = false)
    {
        if (seconds) return $"{(int)span.TotalMinutes}:{Math.Max(0, span.Seconds):00}";
        var minutes = (int)Math.Ceiling(span.TotalMinutes);
        return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";
    }

    internal static string RepeatName(int days) => days switch
    {
        0 => "Once",
        0b1111111 => "Every day",
        0b0111110 => "Weekdays",
        0b1000001 => "Weekends",
        _ => string.Join(", ", Enumerable.Range(1, 7).Select(i => (DayOfWeek)(i % 7))
            .Where(d => (days & 1 << (int)d) != 0).Select(d => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d))),
    };
}
