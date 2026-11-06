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
public sealed partial class TimerViewModel(TimeProvider time) : ObservableObject
{
    public static readonly TimeSpan FocusLength = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan ShortBreak = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongBreak = TimeSpan.FromMinutes(15);
    private const int FocusSessionsPerLongBreak = 4;

    private TimeSpan _accumulated;
    private DateTimeOffset? _runningSince;
    private TimeSpan _target;
    private bool _onBreak;
    private int _focusDone;

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

    public TimeSpan Elapsed => _accumulated + (_runningSince is { } since ? time.GetUtcNow() - since : TimeSpan.Zero);
    public TimeSpan Remaining
    {
        get
        {
            var left = _target - Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    [RelayCommand]
    private void StartCountdown(object? minutes)
    {
        var m = Convert.ToDouble(minutes, System.Globalization.CultureInfo.InvariantCulture);
        Begin(TimerKind.Countdown, TimeSpan.FromMinutes(m), "Timer");
    }

    [RelayCommand]
    private void StartStopwatch() => Begin(TimerKind.Stopwatch, TimeSpan.Zero, "Stopwatch");

    [RelayCommand]
    private void StartPomodoro()
    {
        _focusDone = 0;
        _onBreak = false;
        Begin(TimerKind.Pomodoro, FocusLength, "Focus");
    }

    [RelayCommand(CanExecute = nameof(IsActive))]
    private void PauseResume()
    {
        if (!IsActive) return;
        if (_runningSince is { } since)
        {
            _accumulated += time.GetUtcNow() - since;
            _runningSince = null;
            IsRunning = false;
        }
        else
        {
            _runningSince = time.GetUtcNow();
            IsRunning = true;
        }
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(IsCountingDown))]
    private void AddMinute()
    {
        if (!IsCountingDown) return;
        _target += TimeSpan.FromMinutes(1);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(IsActive))]
    private void Reset()
    {
        var wasActive = IsActive;
        _accumulated = TimeSpan.Zero;
        _runningSince = null;
        _target = TimeSpan.Zero;
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
                Reset();
                Completed?.Invoke("Timer done", $"{Format(length)} timer finished");
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
            Restart(FocusLength, "Focus");
            Completed?.Invoke("Break over", "Time to focus");
            return;
        }
        _focusDone++;
        _onBreak = true;
        var isLong = _focusDone % FocusSessionsPerLongBreak == 0;
        Restart(isLong ? LongBreak : ShortBreak, isLong ? "Long break" : "Break");
        Completed?.Invoke("Focus session done", isLong ? "Take a 15 minute break" : "Take a 5 minute break");
    }

    private void Begin(TimerKind kind, TimeSpan target, string label)
    {
        var wasActive = IsActive;
        Kind = kind;
        Restart(target, label);
        if (!wasActive) ActiveChanged?.Invoke();
    }

    private void Restart(TimeSpan target, string label)
    {
        _target = target;
        _accumulated = TimeSpan.Zero;
        _runningSince = time.GetUtcNow();
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
