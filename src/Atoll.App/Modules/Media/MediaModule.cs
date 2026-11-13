#if AVALONIA
using Avalonia.Threading;
using Atoll.Desktop;
#else
using System.Windows.Threading;
#endif
using System.Net.Http;
using System.Runtime.InteropServices;
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

    private static readonly TimeSpan NaturalEnd = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SkipWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A song that starts on its own is announced only when the setting is on. A skipped song is always announced:
    /// either the island's own next/previous was pressed just now, or the last song was nowhere near its end
    /// (media keys and the player's own buttons). Players that report no timeline count as skipped.
    /// </summary>
    internal static bool ShouldAnnounceTrack(bool announceSetting, DateTimeOffset? skipRequestedAt, DateTimeOffset? nearEndAt, DateTimeOffset now)
    {
        if (announceSetting) return true;
        if (skipRequestedAt is { } skip && now - skip <= SkipWindow) return true;
        return !(nearEndAt is { } end && now - end <= SkipWindow);
    }

    private readonly MediaViewModel _vm;
    private readonly AudioLevels _levels = new();
    private ModuleContext? _context;
    private SessionManager? _manager;
    private Session? _session;
    private DispatcherTimer? _progressTimer;
    private static readonly HttpClient LyricsHttp = CreateLyricsClient();
    private IReadOnlyList<LyricLine> _lyrics = [];
    private int _lyricsVersion;
    private TimeSpan _position;
    private TimeSpan _duration;
    private TimeSpan _startTime;
    private DateTimeOffset _positionAt;
    private bool _seekInProgress;
    private string _postedState = "";
    private string _trackKey = "";
    private bool _disposed;
    private DateTimeOffset? _skipRequestedAt;
    private DateTimeOffset? _nearEndAt;
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, string> _appNames = [];
    private readonly RecentSources _recent = new(TimeSpan.FromSeconds(5));
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
            () => { _skipRequestedAt = DateTimeOffset.UtcNow; return Control(s => s.TrySkipNextAsync().AsTask()); },
            () => { _skipRequestedAt = DateTimeOffset.UtcNow; return Control(s => s.TrySkipPreviousAsync().AsTask()); },
            SeekAsync,
            on => Control(session => session.TryChangeShuffleActiveAsync(on).AsTask()),
            mode => Control(session => session.TryChangeAutoRepeatModeAsync(mode switch
            {
                MediaRepeat.All => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                MediaRepeat.One => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
            }).AsTask()));
        _levels.LevelsAvailable += levels =>
            _context?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => _vm.Visualizer.Set(levels));
        _levels.Unavailable += () => OnUi(() => _vm.VisualizerNote = "Audio visualization is unavailable for this source on this Windows version.");
        _vm.SourceChanged += _ => BindCurrentSession();
        _vm.OutputChanged += id => _ = SwitchOutputAsync(id);
        _vm.ShowLyricsToggled += on => { if (_context is { } c) { c.Settings.Current.ShowLyrics = on; c.Settings.NotifyChanged(); } _ = LoadLyricsAsync(_vm.Artist, _vm.Title); };
        _vm.VolumeScopeChanged += app => { if (_context is { } c) { c.Settings.Current.VolumeControlsApp = app; c.Settings.NotifyChanged(); } };
        _vm.AppVolumeChanged += level =>
        {
            if (_captureProcess is not { } pid) return;
            try { AudioOutputs.SetAppVolume(pid, (float)level); }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { Log.Info($"App volume unavailable: {ex.Message}"); }
        };
    }

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _vm.VolumeControlsApp = context.Settings.Current.VolumeControlsApp;
        _vm.ShowLyrics = context.Settings.Current.ShowLyrics;
        _vm.BongoCat = context.Settings.Current.BongoCat;
        _vm.ApplyTheme(context.Settings.Current.Theme);
        context.Settings.Changed += OnThemeChanged;
        _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) }; // lyrics change on time
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _sourceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _sourceTimer.Tick += (_, _) => { RefreshSources(); RefreshOutputs(); RefreshAppVolume(); }; _sourceTimer.Start();
        RefreshOutputs();
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
    private void OnThemeChanged()
    {
        var settings = _context!.Settings.Current;
        _vm.ApplyTheme(settings.Theme);
        _vm.BongoCat = settings.BongoCat;
        if (_vm.ShowLyrics == settings.ShowLyrics) return;
        _vm.ShowLyrics = settings.ShowLyrics; // switched in the panel or in Settings
        _ = LoadLyricsAsync(_vm.Artist, _vm.Title);
    }

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
            _vm.SetSources(_recent.Update(choices, DateTimeOffset.UtcNow));
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
                    _vm.CanShuffle = false; _vm.CanRepeat = false; _vm.IsShuffling = false; _vm.Repeat = MediaRepeat.Off;
                    _vm.VisualizerNote = "Audio from " + source.Name; _trackKey = selected;
                    _vm.Status = "This app exposes audio without track or playback controls.";
                }
                _vm.IsPlaying = source.Active;
                if (source.Active) _levels.Start(pid); else _levels.Stop();
                UpdateActivity(); return;
            }
        }
        var next = selected is null ? _manager.GetCurrentSession() : _sessions.GetValueOrDefault(selected);
        // The selected player closed its session for a moment (a track change): keep showing it until it returns
        // or its grace period ends, instead of flashing "Nothing playing" or another app.
        if (next is null && selected is not null && _session is not null && _recent.Contains(selected)) return;
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
        RefreshPlayback(); _ = RefreshPropertiesAsync(next);
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

    private async Task RefreshPropertiesAsync(Session session, int attempt = 0)
    {
        var version = ++_propertiesVersion;
        // Players often announce a new track before its title or cover is ready, or throw while switching,
        // and send no second event: ask again a few times (about 25 s in all, for slow covers).
        void Retry()
        {
            if (attempt >= 6) return;
            _ = Task.Delay(TimeSpan.FromSeconds(1 + attempt * 1.5)).ContinueWith(_ => OnUi(() =>
            {
                if (version == _propertiesVersion && ReferenceEquals(session, _session)) _ = RefreshPropertiesAsync(session, attempt + 1);
            }), TaskScheduler.Default);
        }
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var art = await ImageLoader.LoadAsync(props.Thumbnail, 240).ContinueWith(t =>
            {
                if (t.IsFaulted) Log.Info($"Album art unreadable ({t.Exception!.InnerException?.GetType().Name})."); // keep the title
                return t.IsCompletedSuccessfully ? t.Result : null;
            }, TaskScheduler.Default);
            if (_disposed || !ReferenceEquals(session, _session) || version != _propertiesVersion) { if (art is IDisposable disposable) disposable.Dispose(); return; }

            if (art is null || string.IsNullOrEmpty(props.Title)) Retry();
            // An unfinished reading must not blank what is already shown.
            if (string.IsNullOrEmpty(props.Title) && attempt < 6) { if (art is IDisposable unused) unused.Dispose(); return; }

            var artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            var sameTrack = props.Title == _vm.Title && artist == _vm.Artist;
            _vm.Title = props.Title ?? "";
            _vm.Artist = artist;
            if (art is not null || !sameTrack) _vm.Art = art; // a missed cover read keeps the cover of the same track

            var key = $"{_vm.Title}\n{_vm.Artist}";
            if (key != _trackKey)
            {
                var hadTrack = _trackKey.Length > 0;
                _trackKey = key; _acceptedSeek = null; _seekVersion++;
                _ = LoadLyricsAsync(_vm.Artist, _vm.Title);
                var now = DateTimeOffset.UtcNow;
                if (hadTrack && _vm.IsPlaying && _vm.Title.Length > 0 && !FocusService.IsQuietNow
                    && ShouldAnnounceTrack(_context?.Settings.Current.AnnounceTrackChanges ?? true, _skipRequestedAt, _nearEndAt, now))
                    _context?.Post("track", _vm, ActivityPriority.High, TrackPeek, alert: true);
                _skipRequestedAt = null; _nearEndAt = null; // each change is judged on its own
            }
            UpdateActivity();
        }
        catch (Exception ex)
        {
            Log.Error("Reading media properties failed", ex);
            if (!_disposed && version == _propertiesVersion) Retry();
        }
    }

    private void RefreshPlayback()
    {
        var playback = _session?.GetPlaybackInfo();
        _vm.CanSeek = _vm.HasTimeline && playback?.Controls.IsPlaybackPositionEnabled == true;
        _vm.IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        _vm.CanShuffle = playback?.Controls.IsShuffleEnabled == true;
        _vm.IsShuffling = playback?.IsShuffleActive == true;
        _vm.CanRepeat = playback?.Controls.IsRepeatEnabled == true;
        _vm.Repeat = playback?.AutoRepeatMode switch
        {
            Windows.Media.MediaPlaybackAutoRepeatMode.Track => MediaRepeat.One,
            Windows.Media.MediaPlaybackAutoRepeatMode.List => MediaRepeat.All,
            _ => MediaRepeat.Off,
        };
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
        // Re-read the position at the play/pause boundary: extrapolating across it would make the
        // lyrics jump by however long the player stayed paused.
        RefreshTimeline();
        UpdateActivity();
    }

    private void RefreshOutputs()
    {
        try
        {
            var (outputs, current) = AudioOutputs.List();
            _vm.SetOutputs(outputs, current);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { Log.Info($"Audio outputs unavailable: {ex.Message}"); }
    }

    /// <summary>Follows the playing app's volume when it changes elsewhere (the app's own slider, the mixer).</summary>
    private void RefreshAppVolume()
    {
        try { _vm.ShowAppVolume(_captureProcess is { } pid ? AudioOutputs.GetAppVolume(pid) : null); }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { _vm.ShowAppVolume(null); }
    }

    private async Task SwitchOutputAsync(string id)
    {
        try { await Task.Run(() => AudioOutputs.SetDefault(id)); }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { Log.Error("Switching audio output failed", ex); }
        if (_disposed) return;
        RefreshOutputs();
        _levels.Stop(); // loopback capture stays on the old device until restarted
        if (_vm.IsPlaying) _levels.Start(_captureProcess);
    }

    private static HttpClient CreateLyricsClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ATOLL (https://github.com/Mazon3442/Atoll)"); // lrclib asks clients to name themselves
        return http;
    }

    private async Task LoadLyricsAsync(string artist, string title)
    {
        var version = ++_lyricsVersion;
        _lyrics = []; _vm.Lyric = ""; _vm.SetLyrics([]);
        if (_context?.Settings.Current.ShowLyrics != true || artist.Length == 0 || title.Length == 0) return;
        await Task.Delay(TimeSpan.FromSeconds(1)); // skipping through tracks should not look each one up; the timeline also arrives by then
        if (version != _lyricsVersion || _disposed) return;
        try
        {
            var lines = await Lyrics.FindAsync(LyricsHttp, artist, title, _duration, CancellationToken.None);
            if (version != _lyricsVersion || _disposed) return;
            _lyrics = lines;
            _vm.SetLyrics(lines.Select(Lyrics.Display));
            UpdateProgress();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Log.Info($"Lyrics unavailable: {ex.Message}");
        }
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
        // Anchor to when we received the reading instead of the player's LastUpdatedTime. Some players
        // report an old or otherwise unreliable timestamp, which used to push the lyrics far ahead.
        // A plausible age still compensates for a player that reports its position in one-second steps.
        var received = DateTimeOffset.Now;
        _position = TimelinePosition(t.Position, t.StartTime, t.LastUpdatedTime, received);
        _positionAt = received;
        _vm.HasTimeline = _duration > TimeSpan.Zero;
        _vm.CanSeek = _vm.HasTimeline && _session?.GetPlaybackInfo()?.Controls.IsPlaybackPositionEnabled == true;
        UpdateProgress();
    }

    /// <summary>
    /// Position at <paramref name="received"/>. A plausible reading age keeps rare-reporting players in
    /// step; a wildly old or future timestamp is ignored instead of throwing the lyrics off by minutes.
    /// </summary>
    internal static TimeSpan TimelinePosition(TimeSpan position, TimeSpan startTime, DateTimeOffset lastUpdated, DateTimeOffset received)
    {
        var age = received - lastUpdated;
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(5)) age = TimeSpan.Zero;
        return position - startTime + age;
    }

    private void UpdateProgress()
    {
        if (!_vm.HasTimeline || _vm.IsScrubbing || _seekInProgress) return;
        var pos = _position;
        if (_vm.IsPlaying)
        {
            // Players only report position occasionally; advance from the moment we received it.
            var elapsed = DateTimeOffset.Now - _positionAt;
            if (elapsed > TimeSpan.Zero) pos += elapsed;
        }
        if (pos > _duration) pos = _duration;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (_vm.IsPlaying && _duration - pos <= NaturalEnd) _nearEndAt = DateTimeOffset.UtcNow;
        _vm.Progress = pos / _duration;
        _vm.PositionText = TimerViewModel.Format(pos);
        if (_lyrics.Count > 0)
        {
            _vm.LyricIndex = Lyrics.IndexAt(_lyrics, pos);
            _vm.Lyric = _vm.LyricIndex >= 0 ? Lyrics.Display(_lyrics[_vm.LyricIndex]) : "";
        }
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
