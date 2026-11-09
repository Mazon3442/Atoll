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
    private ThemeSettings _theme = new();
    private string? _artworkColor;
    public int PlaybackRevision { get; private set; }
    public void InvalidatePlayback() { PlaybackRevision++; IsScrubbing = false; }
    partial void OnTitleChanged(string value) => PlaybackRevision++;
    partial void OnArtistChanged(string value) => PlaybackRevision++;
    public ObservableCollection<MediaSourceChoice> Sources { get; } = [];
    /// <summary>
    /// The source on screen. The module keeps it on the system's active player through <see cref="Follow"/>;
    /// a pick from the list holds until the active player changes or the picked source closes.
    /// </summary>
    [ObservableProperty] private MediaSourceChoice? _selectedSource;
    /// <summary>Raised only when the user picks a source.</summary>
    public event Action<string>? SourceChanged;
    private bool _following;
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
    /// <summary>The current line of time-synced lyrics; <see cref="HasLyrics"/> keeps its row so the panel does not jump.</summary>
    [ObservableProperty] private string _lyric = "";
    [ObservableProperty] private bool _hasLyrics;

    public VisualizerViewModel Visualizer { get; } = new();

    /// <summary>Speakers and headphones; picking one makes it the Windows default output.</summary>
    public ObservableCollection<MediaSourceChoice> Outputs { get; } = [];
    [ObservableProperty] private MediaSourceChoice? _selectedOutput;
    public bool HasOutputs => Outputs.Count > 1; // a picker with one choice is noise
    /// <summary>Raised only when the user picks an output.</summary>
    public event Action<string>? OutputChanged;
    private bool _syncingOutputs;
    partial void OnSelectedOutputChanged(MediaSourceChoice? value)
    {
        if (value is not null && !_syncingOutputs) OutputChanged?.Invoke(value.Id);
    }
    public void SetOutputs(IEnumerable<MediaSourceChoice> outputs, string? current)
    {
        var wanted = outputs.ToList();
        _syncingOutputs = true;
        try
        {
            foreach (var o in Outputs.Where(o => wanted.All(w => w.Id != o.Id)).ToList()) Outputs.Remove(o);
            foreach (var o in wanted.Where(o => Outputs.All(w => w.Id != o.Id))) Outputs.Add(o);
            SelectedOutput = Outputs.FirstOrDefault(o => o.Id == current);
        }
        finally { _syncingOutputs = false; }
        OnPropertyChanged(nameof(HasOutputs));
    }
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
        IsScrubbing = false;
        PlaybackRevision++;
        if (value is not null && !_following) SourceChanged?.Invoke(value.Id);
    }
    /// <summary>Shows the system's active source without treating it as a user pick. Null clears the selection.</summary>
    public void Follow(string? id)
    {
        _following = true;
        try { SelectedSource = Sources.FirstOrDefault(s => s.Id == id); }
        finally { _following = false; }
    }
    /// <summary>Updates the list in place. A closed selection is cleared so the module follows the system again.</summary>
    public void SetSources(IEnumerable<MediaSourceChoice> choices)
    {
        var wanted = choices.DistinctBy(c => c.Id).ToList();
        foreach (var source in Sources.Where(s => wanted.All(w => w.Id != s.Id)).ToList()) Sources.Remove(source);
        foreach (var source in wanted.Where(s => Sources.All(w => w.Id != s.Id))) Sources.Add(source);
        if (SelectedSource is not null && Sources.All(s => s.Id != SelectedSource.Id)) Follow(null);
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
