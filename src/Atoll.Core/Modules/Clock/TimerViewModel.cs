using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Clock;

public enum TimerKind
{
    None,
    Countdown,
    Stopwatch,
    Pomodoro,
}

/// <summary>
/// Countdown, stopwatch and Pomodoro in one state machine. Time comes from <see cref="TimeProvider"/>
/// so elapsed time stays exact however often <see cref="Tick"/> runs. UI thread only.
/// </summary>
public sealed partial class TimerViewModel : ObservableObject
{
    private const int MinFocus = 5, MaxFocus = 120;
    private const int MinShortBreak = 1, MaxShortBreak = 60;
    private const int MinLongBreak = 5, MaxLongBreak = 120;
    private const int MinSessions = 1, MaxSessions = 12;

    private readonly TimeProvider _time;
    private PomodoroSettings _pomodoro;
    private Action? _save;
    private TimeSpan _accumulated;
    private DateTimeOffset? _runningSince;
    private TimeSpan _target;
    private bool _onBreak;
    private int _focusDone;

    public TimerViewModel(TimeProvider time, PomodoroSettings? pomodoro = null)
    {
        _time = time;
        _pomodoro = pomodoro ?? new PomodoroSettings();
    }

    /// <summary>Uses the saved Pomodoro lengths; edits are written back through <paramref name="save"/>.</summary>
    public void UsePomodoroSettings(PomodoroSettings settings, Action? save = null)
    {
        _pomodoro = settings;
        _save = save;
        foreach (var name in new[]
                 {
                     nameof(FocusMinutes), nameof(ShortBreakMinutes), nameof(LongBreakMinutes),
                     nameof(SessionsBeforeLongBreak), nameof(PomodoroStartLabel), nameof(PomodoroShortcut),
                     nameof(TodayFocus), nameof(FocusStreak), nameof(TotalFocus),
                 })
            OnPropertyChanged(name);
    }

    /// <summary>The timer tab's two sections: plain timers, then the Pomodoro setup.</summary>
    public IReadOnlyList<string> Sections { get; } = ["Timer", "Pomodoro"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimerSection), nameof(IsPomodoroSection))]
    private int _sectionIndex;

    public bool IsTimerSection => SectionIndex == 0;
    public bool IsPomodoroSection => SectionIndex == 1;

    public int FocusMinutes
    {
        get => Clamp(_pomodoro.FocusMinutes, MinFocus, MaxFocus);
        set => SetPomodoro(Clamp(_pomodoro.FocusMinutes, MinFocus, MaxFocus), Clamp(value, MinFocus, MaxFocus),
            v => _pomodoro.FocusMinutes = v, nameof(FocusMinutes), nameof(PomodoroStartLabel), nameof(PomodoroShortcut));
    }

    public int ShortBreakMinutes
    {
        get => Clamp(_pomodoro.ShortBreakMinutes, MinShortBreak, MaxShortBreak);
        set => SetPomodoro(Clamp(_pomodoro.ShortBreakMinutes, MinShortBreak, MaxShortBreak), Clamp(value, MinShortBreak, MaxShortBreak),
            v => _pomodoro.ShortBreakMinutes = v, nameof(ShortBreakMinutes));
    }

    public int LongBreakMinutes
    {
        get => Clamp(_pomodoro.LongBreakMinutes, MinLongBreak, MaxLongBreak);
        set => SetPomodoro(Clamp(_pomodoro.LongBreakMinutes, MinLongBreak, MaxLongBreak), Clamp(value, MinLongBreak, MaxLongBreak),
            v => _pomodoro.LongBreakMinutes = v, nameof(LongBreakMinutes));
    }

    public int SessionsBeforeLongBreak
    {
        get => Clamp(_pomodoro.SessionsBeforeLongBreak, MinSessions, MaxSessions);
        set => SetPomodoro(Clamp(_pomodoro.SessionsBeforeLongBreak, MinSessions, MaxSessions), Clamp(value, MinSessions, MaxSessions),
            v => _pomodoro.SessionsBeforeLongBreak = v, nameof(SessionsBeforeLongBreak));
    }

    /// <summary>Label for the primary action under the Pomodoro settings.</summary>
    public string PomodoroStartLabel => $"Start {FocusMinutes} min focus";

    private static string DayKey(DateTimeOffset day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Focus sessions finished today.</summary>
    public int TodayFocus => _pomodoro.FocusDays.GetValueOrDefault(DayKey(_time.GetLocalNow()));

    /// <summary>Days in a row with a finished session. Today not having one yet does not break it.</summary>
    public int FocusStreak
    {
        get
        {
            var day = _time.GetLocalNow();
            if (!_pomodoro.FocusDays.ContainsKey(DayKey(day))) day = day.AddDays(-1);
            var days = 0;
            while (_pomodoro.FocusDays.GetValueOrDefault(DayKey(day)) > 0)
            {
                days++;
                day = day.AddDays(-1);
            }
            return days;
        }
    }

    public int TotalFocus => _pomodoro.TotalFocus;

    private void RecordFocus()
    {
        var now = _time.GetLocalNow();
        var key = DayKey(now);
        _pomodoro.FocusDays[key] = _pomodoro.FocusDays.GetValueOrDefault(key) + 1;
        _pomodoro.TotalFocus++;
        var oldest = DayKey(now.AddDays(-366));
        foreach (var old in _pomodoro.FocusDays.Keys.Where(day => string.CompareOrdinal(day, oldest) < 0).ToList())
            _pomodoro.FocusDays.Remove(old);
        OnPropertyChanged(nameof(TodayFocus));
        OnPropertyChanged(nameof(FocusStreak));
        OnPropertyChanged(nameof(TotalFocus));
        _save?.Invoke();
    }

    /// <summary>Home's timer card shows this instead of a hard-coded 25 minutes.</summary>
    public string PomodoroShortcut => $"{FocusMinutes}m";

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private void SetPomodoro(int current, int value, Action<int> assign, params string[] properties)
    {
        if (current == value) return;
        assign(value);
        foreach (var property in properties) OnPropertyChanged(property);
        _save?.Invoke();
    }

    [RelayCommand] private void AddFocus() => FocusMinutes += 5;
    [RelayCommand] private void SubtractFocus() => FocusMinutes -= 5;
    [RelayCommand] private void AddShortBreak() => ShortBreakMinutes += 1;
    [RelayCommand] private void SubtractShortBreak() => ShortBreakMinutes -= 1;
    [RelayCommand] private void AddLongBreak() => LongBreakMinutes += 5;
    [RelayCommand] private void SubtractLongBreak() => LongBreakMinutes -= 5;
    [RelayCommand] private void AddSession() => SessionsBeforeLongBreak += 1;
    [RelayCommand] private void SubtractSession() => SessionsBeforeLongBreak -= 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsCountingDown))]
    [NotifyCanExecuteChangedFor(nameof(PauseResumeCommand), nameof(AddMinuteCommand), nameof(ResetCommand))]
    private TimerKind _kind;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _display = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private double _progress;

    /// <summary>Raised when a countdown or Pomodoro phase ends. Argument: (title, detail).</summary>
    public event Action<string, string>? Completed;

    /// <summary>Raised when the timer starts or stops being active (so the module can show/hide its activity).</summary>
    public event Action? ActiveChanged;

    public bool IsActive => Kind != TimerKind.None;
    public bool IsCountingDown => Kind is TimerKind.Countdown or TimerKind.Pomodoro;

    public TimeSpan Elapsed => _accumulated + (_runningSince is { } since ? _time.GetUtcNow() - since : TimeSpan.Zero);
    public TimeSpan Remaining
    {
        get
        {
            // An alarm counts to a moment on the clock, so a sleeping PC or a clock change cannot make it late.
            var left = _alarmAt is { } at ? at - _time.GetUtcNow() : _target - Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>The running countdown ends at a set time of day instead of after a set length.</summary>
    public bool IsAlarm => _alarmAt is not null;

    private DateTimeOffset? _alarmAt;

    /// <summary>What was typed in the timer box: a length in minutes ("24") or a time of day ("5:36 pm", "17:36").</summary>
    [ObservableProperty] private string _entryText = "";

    /// <summary>Shown under the inputs when what was typed is not a length or a time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    public bool HasMessage => Message.Length > 0;

    [RelayCommand]
    private void StartCountdown(object? minutes)
    {
        var m = Convert.ToDouble(minutes, System.Globalization.CultureInfo.InvariantCulture);
        Begin(TimerKind.Countdown, TimeSpan.FromMinutes(m), "Timer");
    }

    /// <summary>A plain number is a length in minutes; anything with a colon or am/pm is a time of day for an alarm.</summary>
    [RelayCommand]
    private void StartEntry()
    {
        var text = EntryText.Trim();
        if (text.Length > 0 && !IsTimeOfDay(text))
        {
            if (!TryParseLength(text, out var length))
            {
                Message = "Type minutes, like 24 or 17.5, or a time, like 5:36 pm.";
                return;
            }
            Message = "";
            EntryText = "";
            Begin(TimerKind.Countdown, length, "Timer");
            return;
        }
        var now = _time.GetLocalNow();
        if (!TryNextTime(text, now, _time.LocalTimeZone, out var at))
        {
            Message = "Type minutes, like 24 or 17.5, or a time, like 5:36 pm.";
            return;
        }
        Message = "";
        EntryText = "";
        Begin(TimerKind.Countdown, at - now, $"Alarm · {at.ToString("t", System.Globalization.CultureInfo.CurrentCulture)}", at);
    }

    private static bool IsTimeOfDay(string text) =>
        text.Contains(':') || System.Text.RegularExpressions.Regex.IsMatch(text, @"[ap]\.?\s*m?\.?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Minutes ("24", "17.5"), at least a second and at most a day.</summary>
    internal static bool TryParseLength(string? text, out TimeSpan length)
    {
        length = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var minutes)
            && !double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out minutes)) return false;
        if (!double.IsFinite(minutes) || minutes < 0 || minutes > 24 * 60) return false;
        length = TimeSpan.FromMinutes(minutes);
        return length >= TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// The next time the clock shows <paramref name="text"/>: later today, else tomorrow. "5:36" with no am or pm means
    /// whichever of 5:36 am and 5:36 pm comes first; "17:36" and "5:36 pm" are exact.
    /// </summary>
    internal static bool TryNextTime(string? text, DateTimeOffset now, TimeZoneInfo zone, out DateTimeOffset at)
    {
        at = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (!TimeOnly.TryParse(text, System.Globalization.CultureInfo.CurrentCulture, out var time)
            && !TimeOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out time)) return false;
        var exact = System.Text.RegularExpressions.Regex.IsMatch(text, @"[ap]\.?\s*m?\.?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || time.Hour is 0 or > 12;
        var candidates = exact ? new[] { time } : [time, time.AddHours(12)];
        var best = DateTimeOffset.MaxValue;
        foreach (var candidate in candidates)
        {
            var local = now.DateTime.Date + candidate.ToTimeSpan();
            if (new DateTimeOffset(local, zone.GetUtcOffset(local)) <= now.AddSeconds(1)) local = local.AddDays(1);
            var moment = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (moment < best) best = moment;
        }
        at = best;
        return true;
    }

    [RelayCommand]
    private void StartStopwatch() => Begin(TimerKind.Stopwatch, TimeSpan.Zero, "Stopwatch");

    [RelayCommand]
    private void StartPomodoro()
    {
        _focusDone = 0;
        _onBreak = false;
        Begin(TimerKind.Pomodoro, TimeSpan.FromMinutes(FocusMinutes), "Focus");
    }

    /// <summary>A break on its own, without a focus session first; focus follows when it ends, as in a normal cycle.</summary>
    [RelayCommand]
    private void StartBreak()
    {
        _focusDone = 0;
        _onBreak = true;
        Begin(TimerKind.Pomodoro, TimeSpan.FromMinutes(ShortBreakMinutes), "Break");
    }

    [RelayCommand]
    private void StartLongBreak()
    {
        _focusDone = 0;
        _onBreak = true;
        Begin(TimerKind.Pomodoro, TimeSpan.FromMinutes(LongBreakMinutes), "Long break");
    }

    [RelayCommand(CanExecute = nameof(IsActive))]
    private void PauseResume()
    {
        if (!IsActive || IsAlarm) return; // an alarm goes off at its time; it cannot be paused
        if (_runningSince is { } since)
        {
            _accumulated += _time.GetUtcNow() - since;
            _runningSince = null;
            IsRunning = false;
        }
        else
        {
            _runningSince = _time.GetUtcNow();
            IsRunning = true;
        }
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(IsCountingDown))]
    private void AddMinute()
    {
        if (!IsCountingDown) return;
        _target += TimeSpan.FromMinutes(1);
        if (_alarmAt is { } at) _alarmAt = at.AddMinutes(1);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(IsActive))]
    private void Reset()
    {
        var wasActive = IsActive;
        _accumulated = TimeSpan.Zero;
        _runningSince = null;
        _target = TimeSpan.Zero;
        _alarmAt = null;
        OnPropertyChanged(nameof(IsAlarm));
        IsRunning = false;
        Kind = TimerKind.None;
        Label = "";
        Refresh();
        if (wasActive) ActiveChanged?.Invoke();
    }

    /// <summary>Updates the display and fires completion. Call a few times a second.</summary>
    public void Tick()
    {
        if (IsCountingDown && IsRunning && Remaining <= TimeSpan.Zero)
        {
            if (Kind == TimerKind.Countdown)
            {
                var length = _target;
                var alarm = _alarmAt;
                Reset();
                if (alarm is { } at) Completed?.Invoke("Alarm", $"It's {at.ToString("t", System.Globalization.CultureInfo.CurrentCulture)}");
                else Completed?.Invoke("Timer done", $"{Format(length)} timer finished");
                return;
            }
            AdvancePomodoro();
        }
        Refresh();
    }

    private void AdvancePomodoro()
    {
        if (_onBreak)
        {
            _onBreak = false;
            Restart(TimeSpan.FromMinutes(FocusMinutes), "Focus");
            Completed?.Invoke("Break over", "Time to focus");
            return;
        }
        _focusDone++;
        RecordFocus();
        _onBreak = true;
        var isLong = _focusDone % SessionsBeforeLongBreak == 0;
        var minutes = isLong ? LongBreakMinutes : ShortBreakMinutes;
        Restart(TimeSpan.FromMinutes(minutes), isLong ? "Long break" : "Break");
        Completed?.Invoke("Focus session done", $"Take a {minutes} minute break");
    }

    private void Begin(TimerKind kind, TimeSpan target, string label, DateTimeOffset? alarmAt = null)
    {
        var wasActive = IsActive;
        _alarmAt = alarmAt;
        OnPropertyChanged(nameof(IsAlarm));
        Kind = kind;
        Restart(target, label);
        if (!wasActive) ActiveChanged?.Invoke();
    }

    private void Restart(TimeSpan target, string label)
    {
        _target = target;
        _accumulated = TimeSpan.Zero;
        _runningSince = _time.GetUtcNow();
        IsRunning = true;
        Label = label;
        Refresh();
    }

    private void Refresh()
    {
        if (!IsActive)
        {
            Display = "";
            Progress = 0;
            return;
        }
        if (Kind == TimerKind.Stopwatch)
        {
            Display = Format(Elapsed);
            Progress = 0;
            return;
        }
        // Round up so "0:00" only shows once time is really up.
        Display = Format(TimeSpan.FromSeconds(Math.Ceiling(Remaining.TotalSeconds)));
        Progress = _target > TimeSpan.Zero ? Math.Clamp(Elapsed / _target, 0, 1) : 0;
    }

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
