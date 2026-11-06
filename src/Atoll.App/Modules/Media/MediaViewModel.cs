#if AVALONIA
using ImageSource = Avalonia.Media.IImage;
#else
using System.Windows.Media;
#endif
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Atoll.Settings;

namespace Atoll.Modules.Media;

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
    [ObservableProperty] private string _color = "#0A84FF";

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

public sealed record MediaSourceChoice(string Id, string Name);

public sealed partial class MediaViewModel(Func<Task> playPause, Func<Task> next, Func<Task> previous,
    Func<double, Task> seek) : ObservableObject
{
    public static MediaSourceChoice Automatic { get; } = new("auto", "Automatic");
    private ThemeSettings _theme = new();
    private string? _artworkColor;
    public int PlaybackRevision { get; private set; }
    public void InvalidatePlayback() { PlaybackRevision++; IsScrubbing = false; }
    partial void OnTitleChanged(string value) => PlaybackRevision++;
    partial void OnArtistChanged(string value) => PlaybackRevision++;
    public ObservableCollection<MediaSourceChoice> Sources { get; } = [Automatic];
    [ObservableProperty] private MediaSourceChoice? _selectedSource = Automatic;
    public event Action<string>? SourceChanged;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(NextCommand), nameof(PreviousCommand))]
    private bool _canControl;
    [ObservableProperty] private string _visualizerNote = "System output";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private string _status = "Play something in a media app to see it here.";
    [ObservableProperty] private ImageSource? _art;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private bool _hasTimeline;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SeekCommand))]
    private bool _canSeek;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private string _durationText = "";

    public VisualizerViewModel Visualizer { get; } = new();
    public bool IsScrubbing { get; set; }

    partial void OnArtChanged(ImageSource? value)
    {
        _artworkColor = MediaArtwork.Color(value);
        ApplyTheme(_theme);
    }
    public void ApplyTheme(ThemeSettings theme)
    {
        _theme = theme;
        Visualizer.Color = ArtworkPalette.ForBackground(_artworkColor, theme.Background, theme.Accent);
    }
    partial void OnSelectedSourceChanged(MediaSourceChoice? value)
    {
        if (value is null) { SelectedSource = Automatic; return; }
        IsScrubbing = false;
        PlaybackRevision++;
        SourceChanged?.Invoke(value.Id);
    }
    public void SetSources(IEnumerable<MediaSourceChoice> choices)
    {
        var wanted = new[] { Automatic }.Concat(choices).DistinctBy(c => c.Id).ToList();
        foreach (var source in Sources.Where(s => wanted.All(w => w.Id != s.Id)).ToList()) Sources.Remove(source);
        foreach (var source in wanted.Where(s => Sources.All(w => w.Id != s.Id))) Sources.Add(source);
        if (SelectedSource is null || Sources.All(s => s.Id != SelectedSource.Id)) SelectedSource = Automatic;
    }

    [RelayCommand(CanExecute = nameof(CanSeek), AllowConcurrentExecutions = true)]
    private Task Seek(double progress) => seek(progress);

    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task PlayPause() => playPause();

    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task Next() => next();

    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task Previous() => previous();
}
