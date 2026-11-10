using System.Globalization;
#if !AVALONIA
using System.Media;
#endif
#if AVALONIA
using Avalonia.Threading;
using Atoll.Desktop;
#else
using System.Windows.Threading;
#endif
using CommunityToolkit.Mvvm.ComponentModel;
using Atoll.Activities;
using Atoll.Settings;

namespace Atoll.Modules.Clock;

public sealed partial class ClockViewModel : ObservableObject
{
    [ObservableProperty] private string _time = "";
    [ObservableProperty] private string _date = "";

    public void Update(DateTime now)
    {
        Time = now.ToString("t", CultureInfo.CurrentCulture);
        Date = now.ToString("ddd, MMM d", CultureInfo.CurrentCulture);
    }
}

/// <summary>Expanded tab: clock on the left, timer controls on the right.</summary>
public sealed record ClockPanelViewModel(ClockViewModel Clock, TimerViewModel Timer);

/// <summary>Idle clock, plus timer / stopwatch / Pomodoro as live activities.</summary>
public sealed class ClockModule : IIslandModule
{
    private readonly ClockViewModel _clock = new();
    private readonly TimerViewModel _timer = new(TimeProvider.System);
    private ModuleContext? _context;
    private DispatcherTimer? _ticker;

    public ClockModule() => Panel = new ClockPanelViewModel(_clock, _timer);

    public object? Panel { get; }

    public void Start(ModuleContext context)
    {
        _context = context;
        var pomodoro = context.Settings.Current.Pomodoro ??= new PomodoroSettings();
        _timer.UsePomodoroSettings(pomodoro, context.Settings.NotifyChanged);
        _clock.Update(DateTime.Now);
        context.Post("time", _clock, ActivityPriority.Idle);

        _timer.ActiveChanged += OnTimerActiveChanged;
        _timer.Completed += OnTimerCompleted;

        // 4 Hz keeps the timer display smooth; unchanged clock text raises no change events.
        _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _ticker.Tick += (_, _) =>
        {
            _clock.Update(DateTime.Now);
            if (_timer.IsActive) _timer.Tick();
        };
        _ticker.Start();
    }

    private void OnTimerActiveChanged()
    {
        if (_timer.IsActive) _context?.Post("timer", _timer, ActivityPriority.Normal);
        else _context?.Remove("timer");
    }

    private void OnTimerCompleted(string title, string detail)
    {
        _context?.Post("done", new IndicatorViewModel("", title, detail), ActivityPriority.Critical, TimeSpan.FromSeconds(8), alert: true);
        SystemSounds.Exclamation.Play();
    }

    public void Dispose()
    {
        _ticker?.Stop();
        _timer.ActiveChanged -= OnTimerActiveChanged;
        _timer.Completed -= OnTimerCompleted;
    }
}
