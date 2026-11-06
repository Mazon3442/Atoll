#if AVALONIA
using Avalonia.Threading;
using Atoll.Desktop;
#else
using System.Windows.Threading;
#endif
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
public sealed partial class MediaModule : IIslandModule
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
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, string> _appNames = [];
    private IReadOnlyList<AudioSource> _audioSources = [];
    private DispatcherTimer? _sourceTimer;
    private DispatcherTimer? _followTimer;
    private string? _followedId;
    private uint? _captureProcess;
    private string? _audioOnlyId;
    private readonly SemaphoreSlim _seekGate = new(1, 1);
    private long _seekVersion;
    private long _propertiesVersion;
    private TimeSpan? _acceptedSeek;
    private DateTimeOffset _seekAcceptedAt;

    public MediaModule()
    {
        _vm = new MediaViewModel(
            () => Control(s => s.TryTogglePlayPauseAsync().AsTask()),
            () => Control(s => s.TrySkipNextAsync().AsTask()),
            () => Control(s => s.TrySkipPreviousAsync().AsTask()),
            SeekAsync);
        _levels.LevelsAvailable += levels =>
            _context?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => _vm.Visualizer.Set(levels));
        _levels.Unavailable += () => OnUi(() => _vm.VisualizerNote = "Audio visualization is unavailable for this source on this Windows version.");
        _vm.SourceChanged += _ => BindCurrentSession();
    }

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _vm.ApplyTheme(context.Settings.Current.Theme);
        context.Settings.Changed += OnThemeChanged;
        _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _sourceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _sourceTimer.Tick += (_, _) => RefreshSources(); _sourceTimer.Start();
        _followTimer = new DispatcherTimer { Interval = FollowDelay };
        _followTimer.Tick += (_, _) => { _followTimer.Stop(); FollowSystem(); };
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            _manager = await SessionManager.RequestAsync();
            if (_disposed) return;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            RefreshSources();
        }
        catch (Exception ex)
        {
            Log.Error("Media controls unavailable", ex);
        }
    }

    private void OnCurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args) => OnUi(ScheduleFollow);

    /// <summary>
    /// Players hand Windows' "current session" to another app for a moment between tracks. Switching waits
    /// <see cref="FollowDelay"/> and then re-reads the current session, so a skip never flashes other media.
    /// </summary>
    internal static readonly TimeSpan FollowDelay = TimeSpan.FromSeconds(1.5);

    private void ScheduleFollow()
    {
        if (_followTimer is { IsEnabled: false }) _followTimer.Start();
    }

    private (string? Id, bool Playing) SystemCandidate()
    {
        var current = _manager?.GetCurrentSession();
        if (current is null) return (null, false);
        bool playing;
        try { playing = current.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch (Exception ex) { playing = false; Log.Info($"Playback state unavailable ({ex.GetType().Name})."); }
        return ("media:" + current.SourceAppUserModelId, playing);
    }

    private void FollowSystem()
    {
        if (_disposed || _manager is null) return;
        var (id, playing) = SystemCandidate();
        if (ShouldFollow(id, playing, _followedId, _vm.SelectedSource is not null)) { _followedId = id; _vm.Follow(id); }
        BindCurrentSession();
    }
    private void OnSessionsChanged(SessionManager sender, SessionsChangedEventArgs args) => OnUi(RefreshSources);
    private void OnThemeChanged() => _vm.ApplyTheme(_context!.Settings.Current.Theme);

    private void RefreshSources()
    {
        if (_disposed || _manager is null) return;
        try
        {
            _sessions.Clear();
            foreach (var group in _manager.GetSessions().GroupBy(s => s.SourceAppUserModelId))
                _sessions["media:" + group.Key] = group.FirstOrDefault(s => ReferenceEquals(s, _session)) ?? group.First();
            try { _audioSources = AudioSources.Get(); }
            catch (Exception ex) { _audioSources = []; Log.Info($"Audio source enumeration unavailable ({ex.GetType().Name})."); }
            var choices = _sessions.Select(pair => new MediaSourceChoice(pair.Key, DisplayName(pair.Value.SourceAppUserModelId))).ToList();
            foreach (var source in _audioSources.Where(a => !_sessions.Values.Any(s => Matches(s.SourceAppUserModelId, a.Name))))
                choices.Add(new MediaSourceChoice("audio:" + source.ProcessId, source.Name + " (audio)"));
            _vm.SetSources(choices);
            if (_vm.SelectedSource is null) { FollowSystem(); return; }
            BindCurrentSession();
            // Catches a paused app that starts playing while it is already Windows' current session.
            var (id, playing) = SystemCandidate();
            if (ShouldFollow(id, playing, _followedId, true)) ScheduleFollow();
        }
        catch (Exception ex) { Log.Error("Media sources could not be refreshed", ex); }
    }
    /// <summary>
    /// The name Windows shows for an app in Start (for example "Firefox" for "308046B0AF4A39CB"),
    /// falling back to a tidied app ID. Cached because sources refresh every two seconds.
    /// </summary>
    private string DisplayName(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        if (_appNames.TryGetValue(aumid, out var cached)) return cached;
        var name = FriendlyAppName(aumid);
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is { } type)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                var item = shell.NameSpace("shell:AppsFolder")?.ParseName(aumid);
                if (item?.Name is string shown && !string.IsNullOrWhiteSpace(shown)) name = shown;
            }
        }
        catch (Exception ex) { Log.Info($"App name lookup unavailable ({ex.GetType().Name})."); }
        return _appNames[aumid] = name;
    }

    private static bool Matches(string appId, string processName) => processName.Length > 2 && appId.Contains(processName, StringComparison.OrdinalIgnoreCase);

    private void BindCurrentSession()
    {
        if (_disposed || _manager is null) return;
        var selected = _vm.SelectedSource?.Id;
        if (selected?.StartsWith("audio:", StringComparison.Ordinal) == true)
        {
            if (uint.TryParse(selected[6..], out var pid) && _audioSources.FirstOrDefault(s => s.ProcessId == pid) is { } source)
            {
                if (_audioOnlyId != selected)
                {
                    UnbindSession(); _audioOnlyId = selected; _captureProcess = pid;
                    _vm.Title = source.Name + " audio"; _vm.AppName = source.Name; _vm.Artist = ""; _vm.Art = null;
                    _vm.HasSession = true; _vm.HasTimeline = false; _vm.CanSeek = false; _vm.CanControl = false;
                    _vm.VisualizerNote = "Audio from " + source.Name; _trackKey = selected;
                    _vm.Status = "This app exposes audio without track or playback controls.";
                }
                _vm.IsPlaying = source.Active;
                if (source.Active) _levels.Start(pid); else _levels.Stop();
                UpdateActivity(); return;
            }
        }
        var next = selected is null ? _manager.GetCurrentSession() : _sessions.GetValueOrDefault(selected);
        if (ReferenceEquals(next, _session) && _audioOnlyId is null)
        {
            var pid = next is null ? null : _audioSources.FirstOrDefault(a => Matches(next.SourceAppUserModelId, a.Name))?.ProcessId;
            if (pid != _captureProcess) { _captureProcess = pid; if (_vm.IsPlaying) _levels.Start(pid); }
            return;
        }
        _audioOnlyId = null;
        _captureProcess = next is null ? null : _audioSources.FirstOrDefault(a => Matches(next.SourceAppUserModelId, a.Name))?.ProcessId;
        UnbindSession();
        _session = next;
        _vm.CanControl = next is not null;
        _vm.Status = next is null ? "Play something in a media app or choose an audio source." : "";
        _vm.VisualizerNote = _captureProcess is null ? "System output; this player does not expose its audio process." : "Audio from " + DisplayName(next?.SourceAppUserModelId);
        _vm.IsScrubbing = false;
        _vm.CanSeek = false;
        _vm.HasTimeline = false;
        _acceptedSeek = null;
        if (next is null)
        {
            _vm.HasSession = false; _vm.Title = ""; _vm.Artist = ""; _vm.Art = null;
            RefreshPlayback(); return;
        }
        next.MediaPropertiesChanged += OnMediaPropertiesChanged;
        next.PlaybackInfoChanged += OnPlaybackInfoChanged;
        next.TimelinePropertiesChanged += OnTimelineChanged;
        _vm.HasSession = true;
        _vm.AppName = DisplayName(next.SourceAppUserModelId);
        RefreshPlayback(); RefreshTimeline(); _ = RefreshPropertiesAsync(next);
    }

    private void UnbindSession()
    {
        _vm.InvalidatePlayback();
        _seekVersion++; _propertiesVersion++;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = null; _levels.Stop();
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
        var version = ++_propertiesVersion;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var art = await ImageLoader.LoadAsync(props.Thumbnail, 240);
            if (_disposed || !ReferenceEquals(session, _session) || version != _propertiesVersion) { if (art is IDisposable disposable) disposable.Dispose(); return; }

            _vm.Title = props.Title ?? "";
            _vm.Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            _vm.Art = art;

            var key = $"{_vm.Title}\n{_vm.Artist}";
            if (key != _trackKey)
            {
                var hadTrack = _trackKey.Length > 0;
                _trackKey = key; _acceptedSeek = null; _seekVersion++;
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
            _levels.Start(_captureProcess);
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
        if (_acceptedSeek is { } target && DateTimeOffset.UtcNow - _seekAcceptedAt < TimeSpan.FromSeconds(2)
            && t.LastUpdatedTime < _seekAcceptedAt && Math.Abs((t.Position - target).TotalSeconds) > 1) return;
        _acceptedSeek = null;
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
        var version = ++_seekVersion;
        await _seekGate.WaitAsync();
        if (_disposed || version != _seekVersion || !ReferenceEquals(session, _session) || track != _trackKey) { _seekGate.Release(); return; }
        _seekInProgress = true;
        try
        {
            var timeline = session.GetTimelineProperties();
            var target = SeekPosition(progress, timeline.StartTime, timeline.EndTime,
                timeline.MinSeekTime, timeline.MaxSeekTime);
            if (target is null) return;
            var accepted = await session.TryChangePlaybackPositionAsync(target.Value.Ticks);
            if (_disposed || version != _seekVersion || !ReferenceEquals(session, _session) || track != _trackKey) return;
            if (accepted)
            {
                _position = target.Value - _startTime;
                _positionAt = DateTimeOffset.Now;
                _acceptedSeek = target; _seekAcceptedAt = _positionAt;
                _vm.Status = "";
            }
            else
            {
                Log.Info("The media player rejected seeking");
                _vm.Status = "This player did not accept the seek. Its current position is shown.";
                RefreshTimeline();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Seeking media failed", ex);
            _vm.Status = "Seeking failed. Try again or choose another player.";
        }
        finally
        {
            _seekInProgress = false;
            _seekGate.Release();
            if (!_disposed) UpdateProgress();
        }
    }

    internal static TimeSpan? SeekPosition(double progress, TimeSpan start, TimeSpan end,
        TimeSpan minSeek, TimeSpan maxSeek)
    {
        if (minSeek == TimeSpan.Zero && maxSeek == TimeSpan.Zero) { minSeek = start; maxSeek = end; }
        var min = start > minSeek ? start : minSeek;
        var max = end < maxSeek ? end : maxSeek;
        if (!double.IsFinite(progress) || end <= start || max < min) return null;
        var target = start + (end - start) * Math.Clamp(progress, 0, 1);
        return target < min ? min : target > max ? max : target;
    }

    private void OnUi(Action action) => _context?.Dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        _disposed = true;
        _progressTimer?.Stop();
        _sourceTimer?.Stop();
        _followTimer?.Stop();
        if (_context is not null) _context.Settings.Changed -= OnThemeChanged;
        _levels.Dispose();
        if (_manager is not null) { _manager.CurrentSessionChanged -= OnCurrentSessionChanged; _manager.SessionsChanged -= OnSessionsChanged; }
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
    }
}
