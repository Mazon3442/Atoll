using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules.Clock;
using Atoll.Shell;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace Atoll.Modules.Media;

/// <summary>
/// Now playing from any app that reports to Windows media controls (Spotify, browsers, ...).
/// Playing → Normal activity; paused → Ambient for a short while; track change → brief alert peek.
/// </summary>
public sealed class MediaModule : IIslandModule
{
    private static readonly TimeSpan PausedLinger = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TrackPeek = TimeSpan.FromSeconds(4);

    private readonly MediaViewModel _vm;
    private readonly AudioLevels _levels = new();
    private ModuleContext? _context;
    private SessionManager? _manager;
    private Session? _session;
    private DispatcherTimer? _progressTimer;
    private TimeSpan _position;
    private TimeSpan _duration;
    private TimeSpan _startTime;
    private DateTimeOffset _positionAt;
    private bool _seekInProgress;
    private string _postedState = "";
    private string _trackKey = "";
    private bool _disposed;

    public MediaModule()
    {
        _vm = new MediaViewModel(
            () => Control(s => s.TryTogglePlayPauseAsync().AsTask()),
            () => Control(s => s.TrySkipNextAsync().AsTask()),
            () => Control(s => s.TrySkipPreviousAsync().AsTask()),
            SeekAsync);
        _levels.LevelsAvailable += levels =>
            _context?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => _vm.Visualizer.Set(levels));
    }

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _progressTimer = new DispatcherTimer(DispatcherPriority.Background, context.Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            _manager = await SessionManager.RequestAsync();
            if (_disposed) return;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            BindCurrentSession();
        }
        catch (Exception ex)
        {
            Log.Error("Media controls unavailable", ex);
        }
    }

    private void OnCurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args) => OnUi(BindCurrentSession);

    private void BindCurrentSession()
    {
        if (_disposed || _manager is null) return;
        var next = _manager.GetCurrentSession();
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = next;
        _vm.IsScrubbing = false;
        _vm.CanSeek = false;
        _vm.HasTimeline = false;
        if (next is null)
        {
            _vm.HasSession = false;
            RefreshPlayback();
            return;
        }
        next.MediaPropertiesChanged += OnMediaPropertiesChanged;
        next.PlaybackInfoChanged += OnPlaybackInfoChanged;
        next.TimelinePropertiesChanged += OnTimelineChanged;
        _vm.HasSession = true;
        _vm.AppName = FriendlyAppName(next.SourceAppUserModelId);
        RefreshPlayback();
        RefreshTimeline();
        _ = RefreshPropertiesAsync(next);
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args) =>
        OnUi(() => _ = RefreshPropertiesAsync(sender));

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args) => OnUi(() =>
    {
        if (ReferenceEquals(sender, _session)) RefreshPlayback();
    });

    private void OnTimelineChanged(Session sender, TimelinePropertiesChangedEventArgs args) => OnUi(() =>
    {
        if (ReferenceEquals(sender, _session)) RefreshTimeline();
    });

    private async Task RefreshPropertiesAsync(Session session)
    {
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var art = await ImageLoader.LoadAsync(props.Thumbnail, 240);
            if (_disposed || !ReferenceEquals(session, _session)) return; // a newer session took over meanwhile

            _vm.Title = props.Title ?? "";
            _vm.Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            _vm.Art = art;

            var key = $"{_vm.Title}\n{_vm.Artist}";
            if (key != _trackKey)
            {
                var hadTrack = _trackKey.Length > 0;
                _trackKey = key;
                if (hadTrack && _vm.IsPlaying && _vm.Title.Length > 0)
                    _context?.Post("track", _vm, ActivityPriority.High, TrackPeek, alert: true);
            }
            UpdateActivity();
        }
        catch (Exception ex)
        {
            // Some players throw while switching tracks; the next change event refreshes again.
            Log.Error("Reading media properties failed", ex);
        }
    }

    private void RefreshPlayback()
    {
        var playback = _session?.GetPlaybackInfo();
        _vm.CanSeek = _vm.HasTimeline && playback?.Controls.IsPlaybackPositionEnabled == true;
        _vm.IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (_vm.IsPlaying)
        {
            _levels.Start();
            _progressTimer?.Start();
        }
        else
        {
            _levels.Stop();
            _progressTimer?.Stop();
        }
        UpdateActivity();
    }

    private void RefreshTimeline()
    {
        var t = _session?.GetTimelineProperties();
        if (t is null) return;
        _duration = t.EndTime - t.StartTime;
        _startTime = t.StartTime;
        _position = t.Position - t.StartTime;
        _positionAt = t.LastUpdatedTime;
        _vm.HasTimeline = _duration > TimeSpan.Zero;
        _vm.CanSeek = _vm.HasTimeline && _session?.GetPlaybackInfo()?.Controls.IsPlaybackPositionEnabled == true;
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        if (!_vm.HasTimeline || _vm.IsScrubbing || _seekInProgress) return;
        var pos = _position;
        if (_vm.IsPlaying && _positionAt > DateTimeOffset.MinValue)
            pos += DateTimeOffset.Now - _positionAt; // players only report position occasionally
        if (pos > _duration) pos = _duration;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        _vm.Progress = pos / _duration;
        _vm.PositionText = TimerViewModel.Format(pos);
        _vm.DurationText = TimerViewModel.Format(_duration);
    }

    private void UpdateActivity()
    {
        if (_context is null) return;
        var state = !_vm.HasSession || _vm.Title.Length == 0 ? "none" : _vm.IsPlaying ? "playing" : "paused";
        if (state == _postedState) return;
        _postedState = state;
        switch (state)
        {
            case "playing":
                _context.Post("now", _vm, ActivityPriority.Normal);
                break;
            case "paused":
                _context.Post("now", _vm, ActivityPriority.Ambient, PausedLinger);
                break;
            default:
                _context.Remove("now");
                _context.Remove("track");
                break;
        }
    }

    private async Task Control(Func<Session, Task<bool>> action)
    {
        if (_session is null) return;
        try { await action(_session); }
        catch (Exception ex) { Log.Error("Media control command failed", ex); }
    }

    private async Task SeekAsync(double progress)
    {
        var session = _session;
        if (session is null || !_vm.CanSeek || !double.IsFinite(progress)) return;
        var track = _trackKey;
        _seekInProgress = true;
        try
        {
            var timeline = session.GetTimelineProperties();
            var target = SeekPosition(progress, timeline.StartTime, timeline.EndTime,
                timeline.MinSeekTime, timeline.MaxSeekTime);
            if (target is null) return;
            var accepted = await session.TryChangePlaybackPositionAsync(target.Value.Ticks);
            if (_disposed || !ReferenceEquals(session, _session) || track != _trackKey) return;
            if (accepted)
            {
                _position = target.Value - _startTime;
                _positionAt = DateTimeOffset.Now;
            }
            else
            {
                Log.Info("The media player rejected seeking");
                RefreshTimeline();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Seeking media failed", ex);
        }
        finally
        {
            _seekInProgress = false;
            if (!_disposed) UpdateProgress();
        }
    }

    internal static TimeSpan? SeekPosition(double progress, TimeSpan start, TimeSpan end,
        TimeSpan minSeek, TimeSpan maxSeek)
    {
        var min = start > minSeek ? start : minSeek;
        var max = end < maxSeek ? end : maxSeek;
        if (!double.IsFinite(progress) || end <= start || max < min) return null;
        var target = start + (end - start) * Math.Clamp(progress, 0, 1);
        return target < min ? min : target > max ? max : target;
    }

    /// <summary>"SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify", "chrome.exe" → "chrome".</summary>
    internal static string FriendlyAppName(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var name = aumid.Contains('!') ? aumid[(aumid.LastIndexOf('!') + 1)..] : aumid;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    private void OnUi(Action action) => _context?.Dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        _disposed = true;
        _progressTimer?.Stop();
        _levels.Dispose();
        if (_manager is not null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
    }
}
