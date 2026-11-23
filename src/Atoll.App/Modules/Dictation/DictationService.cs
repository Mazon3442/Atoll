using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules.Tide.Listen;
using Atoll.Settings;
using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;

namespace Atoll.Modules.Dictation;

/// <summary>The pill while dictating: a voice meter and the elapsed time, then "Transcribing".</summary>
public sealed partial class DictationViewModel : ObservableObject
{
    private readonly Action _stop;
    private readonly Action _cancel;

    internal DictationViewModel(Action stop, Action cancel)
    {
        _stop = stop;
        _cancel = cancel;
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRecording), nameof(IsTranscribing))] private bool _recording = true;
    [ObservableProperty] private string _elapsed = "0:00";
    [ObservableProperty] private double _level;
    [ObservableProperty] private string _status = "Listening";

    public bool IsRecording => Recording;
    public bool IsTranscribing => !Recording;

    [RelayCommand] private void Stop() => _stop();
    [RelayCommand] private void Cancel() => _cancel();
}

/// <summary>
/// Voice to text on a hotkey. Press, speak, press again: the text is copied and, if you like, typed into the
/// window you were in. Speech recognition is local Whisper, the same model Tide Listen uses.
/// </summary>
internal sealed class DictationService : IDisposable
{
    private const string Key = "dictation";

    private readonly ActivityHost _activities;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DictationController _controller;
    private readonly DictationViewModel _pill;
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _release = new(DispatcherPriority.Background);
    private ISpeechTranscriber? _warm;
    private string _warmKey = "";

    public DictationService(ActivityHost activities, SettingsService settings, Dispatcher dispatcher, HotkeyService? hotkeys)
    {
        _activities = activities;
        _settings = settings;
        _dispatcher = dispatcher;
        _controller = new DictationController(() => new MicRecorder(), LoadTranscriberAsync, Deliver, MicOwnership.BusyWith, TimeProvider.System);
        _pill = new DictationViewModel(() => _ = ToggleAsync(), _controller.Cancel);
        _controller.Changed += () => _dispatcher.BeginInvoke(Refresh);
        _tick.Tick += (_, _) => Refresh();
        _release.Tick += (_, _) => ReleaseModel();
        hotkeys?.AddAction(ShortcutCatalog.Dictate, () => _ = ToggleAsync());
    }

    /// <summary>The palette's "Dictate" action and the hotkey both land here.</summary>
    public Task ToggleAsync()
    {
        _release.Stop();
        return _controller.ToggleAsync();
    }

    private void Refresh()
    {
        switch (_controller.State)
        {
            case DictationState.Recording:
                _pill.Recording = true;
                _pill.Status = "Listening";
                _pill.Level = _controller.Level;
                _pill.Elapsed = $"{(int)_controller.Elapsed.TotalMinutes}:{_controller.Elapsed.Seconds:00}";
                _activities.Post(new Activity(Key, Key, _pill, ActivityPriority.High));
                _tick.Start();
                break;
            case DictationState.Transcribing:
                _tick.Stop();
                _pill.Recording = false;
                _pill.Status = string.IsNullOrEmpty(_controller.Message) ? "Transcribing" : _controller.Message;
                _activities.Post(new Activity(Key, Key, _pill, ActivityPriority.High));
                break;
            default:
                _tick.Stop();
                _activities.Remove(Key);
                if (_controller.LastText is { } text)
                    _activities.Post(new Activity($"{Key}.done", Key, new IndicatorViewModel("", "Text copied", Preview(text)),
                        ActivityPriority.High, TimeSpan.FromSeconds(4), alert: true));
                else if (_controller.Message.Length > 0)
                    _activities.Post(new Activity($"{Key}.done", Key, new IndicatorViewModel("", _controller.Message),
                        ActivityPriority.High, TimeSpan.FromSeconds(3), alert: true));
                var keep = _settings.Current.Dictation.KeepModelSeconds;
                if (_warm is not null && keep > 0)
                {
                    _release.Interval = TimeSpan.FromSeconds(keep);
                    _release.Stop();
                    _release.Start();
                }
                break;
        }
    }

    private static string Preview(string text) => text.Length <= 80 ? text : text[..79] + "…";

    private async Task<ISpeechTranscriber> LoadTranscriberAsync(CancellationToken ct)
    {
        var tide = _settings.Current.Tide;
        var model = tide.ListenModel;
        var path = ListenModel.Resolve(tide.ListenModelPath, model);
        var language = model.Contains(".en", StringComparison.OrdinalIgnoreCase) ? "en" : tide.ListenLanguage.Trim();
        var key = path + "|" + language;
        if (_warm is not null && _warmKey == key) return _warm;

        _warm?.Dispose();
        _warm = null;
        if (!ListenModel.IsComplete(path, model, ListenModel.IsCoreModel(tide.ListenModelPath)))
            await ListenModel.DownloadAsync(path, model, null, ct);
        var loaded = await Task.Run(() => WhisperTranscriber.Load(path, language), ct);
        _warm = loaded;
        _warmKey = key;
        return loaded;
    }

    /// <summary>Frees the model a while after the last use. Never while a run is in flight.</summary>
    private void ReleaseModel()
    {
        _release.Stop();
        if (_controller.State != DictationState.Idle) return;
        _warm?.Dispose();
        _warm = null;
    }

    private void Deliver(string text)
    {
        _dispatcher.Invoke(() =>
        {
            try { System.Windows.Clipboard.SetText(text); }
            catch (COMException) { Log.Info("The clipboard was busy; the dictated text was not copied."); return; }
        });
        if (_settings.Current.Dictation.TypeIntoWindow)
            _ = KeyboardInjector.PasteAsync();
    }

    public void Dispose()
    {
        _tick.Stop();
        _release.Stop();
        _controller.Dispose();
        _warm?.Dispose();
        _activities.Remove(Key);
    }

    /// <summary>The default communications microphone as 16 kHz mono, collected until <see cref="Finish"/>.</summary>
    private sealed class MicRecorder : IDictationRecorder
    {
        private readonly ListenCapture _capture = new();
        private readonly List<float[]> _chunks = [];
        private readonly object _gate = new();

        public event Action<float>? Level;

        public async Task StartAsync()
        {
            _capture.FrameReady += OnFrame;
            await _capture.StartAsync(captureMic: true, captureSystem: false);
        }

        private void OnFrame(AudioFrame frame)
        {
            lock (_gate) _chunks.Add(frame.Samples);
            Level?.Invoke(frame.MicLevel);
        }

        public float[] Finish()
        {
            _capture.Stop();
            _capture.FrameReady -= OnFrame;
            lock (_gate) return _chunks.SelectMany(chunk => chunk).ToArray();
        }

        public void Dispose() => _capture.Dispose();
    }
}
