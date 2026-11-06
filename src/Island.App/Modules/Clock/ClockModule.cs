using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Island.Activities;

namespace Island.Modules.Clock;

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

/// <summary>Idle view: shows the time when nothing else is happening.</summary>
public sealed class ClockModule : IIslandModule
{
    private readonly ClockViewModel _clock = new();
    private DispatcherTimer? _timer;

    public object? Panel => _clock;

    public void Start(ModuleContext context)
    {
        _clock.Update(DateTime.Now);
        context.Post("time", _clock, ActivityPriority.Idle);
        _timer = new DispatcherTimer(DispatcherPriority.Background, context.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _clock.Update(DateTime.Now);
        _timer.Start();
    }

    public void Dispose() => _timer?.Stop();
}
