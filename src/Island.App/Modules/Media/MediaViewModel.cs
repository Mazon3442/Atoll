using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Island.Modules.Media;

/// <summary>
/// Separate object so 20 Hz bar updates do not raise change events on <see cref="MediaViewModel"/>
/// (the island re-measures its pill when the shown view model changes).
/// </summary>
public sealed partial class VisualizerViewModel : ObservableObject
{
    private const double MinBar = 0.18;

    [ObservableProperty] private double _bar0 = MinBar;
    [ObservableProperty] private double _bar1 = MinBar;
    [ObservableProperty] private double _bar2 = MinBar;
    [ObservableProperty] private double _bar3 = MinBar;
    [ObservableProperty] private double _bar4 = MinBar;

    public void Set(float[] levels)
    {
        double Scale(int i) => i < levels.Length ? MinBar + (1 - MinBar) * levels[i] : MinBar;
        Bar0 = Scale(0);
        Bar1 = Scale(1);
        Bar2 = Scale(2);
        Bar3 = Scale(3);
        Bar4 = Scale(4);
    }
}

public sealed partial class MediaViewModel(Func<Task> playPause, Func<Task> next, Func<Task> previous) : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private ImageSource? _art;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private bool _hasTimeline;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private string _durationText = "";

    public VisualizerViewModel Visualizer { get; } = new();

    [RelayCommand]
    private Task PlayPause() => playPause();

    [RelayCommand]
    private Task Next() => next();

    [RelayCommand]
    private Task Previous() => previous();
}
