using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Tide.Listen;

/// <summary>
/// The Listen section of Tide: local microphone + system-audio capture, Whisper transcription, a notes pane
/// and the optional notes model pass. Nothing is uploaded; the only outbound call is the notes model.
/// </summary>
public sealed partial class ListenViewModel : ObservableObject, IDisposable
{
    private const string ReadyText = "Ready. Microphone and system audio stay on this PC.";

    private readonly TideSettings _settings;
    private readonly Action _save;
    private readonly Func<AppSettings?>? _appSettings;
    private readonly Action<bool>? _listeningChanged;
    private readonly ListenStore _store = new(ListenStore.DefaultDirectory);
    private readonly ListenNotes _notes = new();
    private readonly DispatcherTimer _notesTimer;
    private readonly DispatcherTimer _saveTimer;
    private NotesGate _notesGate = new(DateTimeOffset.Now);

    private CancellationTokenSource? _run;
    private Task? _transcribeTask;
    private ListenCapture? _capture;
    private ISpeechTranscriber? _transcriber;
    private Channel<AudioFrame>? _frames;
    private PcmWavWriter? _audio;
    private ListenSession? _session;
    private bool _notesRunning;
    private bool _disposed;
    private int _linesSinceSave;

    public ListenViewModel(TideSettings settings, Action save, Func<AppSettings?>? appSettings = null,
        Action<bool>? listeningChanged = null)
    {
        _settings = settings;
        _save = save;
        _appSettings = appSettings;
        _listeningChanged = listeningChanged;
        _captureMic = settings.ListenMic;
        _captureSystem = settings.ListenSystemAudio;
        _notesModel = settings.ListenNotesModel;

        _notesTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _notesTimer.Tick += async (_, _) => await MaybeRunNotesAsync();
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
        _saveTimer.Tick += (_, _) => FlushSession();

        RefreshNotesSources();
        Status = ReadyText;
    }

    public ObservableCollection<ListenLine> Transcript { get; } = [];
    public ObservableCollection<ListenActionItem> ActionItems { get; } = [];
    public ObservableCollection<ListenReply> Replies { get; } = [];
    public ObservableCollection<ListenNotesSource> NotesSources { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeCapture))]
    private bool _isListening;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeCapture))]
    private bool _isPreparing;

    [ObservableProperty] private bool _isTranscribing;
    [ObservableProperty] private bool _isDownloadingModel;
    [ObservableProperty] private double _modelProgress;
    [ObservableProperty] private string _status = ReadyText;
    [ObservableProperty] private string _statusDetail = "";

    [ObservableProperty] private bool _captureMic = true;
    [ObservableProperty] private bool _captureSystem = true;

    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _notesModel = "";
    [ObservableProperty] private string _notesStatus = "";
    [ObservableProperty] private bool _needsNotesModel;
    [ObservableProperty] private bool _notesBusy;
    [ObservableProperty] private ListenNotesSource? _notesSource;

    /// <summary>Capture switches only take effect on the next start; the button reads that honestly.</summary>
    public bool CanChangeCapture => !IsListening && !IsPreparing;

    public bool HasTranscript => Transcript.Count > 0;

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsListening || IsPreparing) await StopAsync();
        else await StartAsync();
    }

    [RelayCommand]
    private void OpenNotesSettings() => Atoll.Navigation.OpenSettings?.Invoke(4);

    [RelayCommand]
    private void OpenSessions()
    {
        try
        {
            System.IO.Directory.CreateDirectory(ListenStore.DefaultDirectory);
            Process.Start(new ProcessStartInfo { FileName = ListenStore.DefaultDirectory, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            StatusDetail = "The sessions folder could not be opened.";
        }
    }

    private async Task StartAsync()
    {
        if (IsListening || IsPreparing || _disposed) return;
        if (!CaptureMic && !CaptureSystem)
        {
            Status = "Choose the microphone, system audio, or both.";
            StatusDetail = "";
            return;
        }

        _run = new CancellationTokenSource();
        var ct = _run.Token;
        var captureStarted = false;
        var downloading = false;
        var model = _settings.ListenModel;
        IsPreparing = true;
        NeedsNotesModel = false;
        NotesStatus = "";
        StatusDetail = "";
        RefreshNotesSources();
        try
        {
            var path = ListenModel.Resolve(_settings.ListenModelPath, model);
            var exact = ListenModel.IsCoreModel(_settings.ListenModelPath);
            if (!ListenModel.IsComplete(path, model, exact))
            {
                downloading = true;
                IsDownloadingModel = true;
                ModelProgress = 0;
                Status = "Downloading the speech model";
                StatusDetail = "One time, about 142 MB.";
                var progress = new Progress<double>(value =>
                {
                    ModelProgress = value * 100;
                    StatusDetail = $"{value:P0} · one time download";
                });
                await ListenModel.DownloadAsync(path, model, progress, ct);
                IsDownloadingModel = false;
                downloading = false;
            }

            ct.ThrowIfCancellationRequested();
            Status = "Loading the speech model";
            var language = Language(model);
            _transcriber = await Task.Run(() => WhisperTranscriber.Load(path, language), ct);

            ct.ThrowIfCancellationRequested();
            Status = "Starting capture";
            _frames = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(60)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
            _capture = new ListenCapture();
            _capture.FrameReady += OnFrame;
            captureStarted = true;
            await _capture.StartAsync(CaptureMic, CaptureSystem);

            _session = new ListenSession
            {
                Started = DateTimeOffset.Now,
                Title = "Meeting · " + DateTimeOffset.Now.ToString("MMM d, h:mm tt"),
            };
            Transcript.Clear();
            ActionItems.Clear();
            Replies.Clear();
            OnPropertyChanged(nameof(HasTranscript));
            Summary = "";
            NotesText = "";
            NotesStatus = "";
            NeedsNotesModel = false;
            _notesGate = new NotesGate(DateTimeOffset.Now);
            _linesSinceSave = 0;
            if (_settings.ListenKeepAudio)
                _audio = new PcmWavWriter(Path.Combine(ListenStore.DefaultDirectory, "audio", _session.Id + ".wav"));

            IsListening = true;
            IsPreparing = false;
            Status = "Listening";
            StatusDetail = _capture.SystemAvailable ? "" : "System audio is unavailable; only the microphone is transcribed.";
            _listeningChanged?.Invoke(true);
            _notesTimer.Start();
            _transcribeTask = Task.Run(() => TranscribeAsync(_frames.Reader, _transcriber, ct), ct);
            _ = CheckNotesAvailabilityAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await StopAsync("Stopped.");
        }
        catch (ListenCaptureException ex)
        {
            await StopAsync(ex.Message);
            StatusDetail = CaptureMic
                ? "Plug in or choose a microphone in Windows, or turn Microphone off above."
                : ex.Message;
        }
        catch (Exception ex)
        {
            Atoll.Log.Error("Listen could not start", ex);
            await StopAsync("Listen could not start.");
            StatusDetail = downloading
                ? $"Could not download the speech model. Check your connection, or put {ListenModel.FileName(model)} in {ListenModel.Directory}."
                : captureStarted
                    ? "Audio capture could not start."
                    : "The speech model could not be loaded. Check " + ListenModel.Directory + ".";
        }
    }

    private async Task StopAsync(string? finalStatus = null)
    {
        var run = _run;
        _run = null;
        run?.Cancel();
        IsPreparing = false;

        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.FrameReady -= OnFrame;
            capture.Stop();
            capture.Dispose();
        }
        _frames?.Writer.TryComplete();
        _frames = null;

        var task = _transcribeTask;
        _transcribeTask = null;
        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { Atoll.Log.Info("Listen: transcription did not stop in time; freeing it when the window ends"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Atoll.Log.Error("Listen transcription stopped with an error", ex); }
        }

        ReleaseTranscriber(task);
        _audio?.Dispose();
        _audio = null;
        _notesTimer.Stop();
        _saveTimer.Stop();

        var wasListening = IsListening;
        IsListening = false;
        IsTranscribing = false;
        IsDownloadingModel = false;
        if (wasListening) _listeningChanged?.Invoke(false);
        FlushSession();
        run?.Dispose();

        Status = finalStatus ?? (Transcript.Count > 0 ? "Stopped. Session saved." : ReadyText);
        if (finalStatus is null) StatusDetail = "";
    }

    private void OnFrame(AudioFrame frame)
    {
        _frames?.Writer.TryWrite(frame);
        _audio?.Write(frame.Samples);
    }

    private async Task TranscribeAsync(ChannelReader<AudioFrame> reader, ISpeechTranscriber transcriber, CancellationToken ct)
    {
        var window = new VadWindow();
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct))
            {
                window.Append(frame);
                if (!window.TryTake(out var samples, out var you)) continue;
                SetTranscribing(true);
                string text;
                try
                {
                    text = await transcriber.TranscribeAsync(samples, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Atoll.Log.Error("Listen could not transcribe a window", ex);
                    SetTranscribing(false);
                    PostToUi(() => StatusDetail = "A window could not be transcribed.");
                    continue;
                }
                SetTranscribing(false);

                var cleaned = CleanTranscript(text);
                if (cleaned.Length == 0) continue;
                var line = new ListenLine
                {
                    Speaker = you ? "You" : "Others",
                    At = DateTimeOffset.Now,
                    Text = cleaned,
                };
                PostToUi(() => AddLine(line));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    /// <summary>Whisper marks non-speech as bracketed text or music notes; those never become transcript lines.</summary>
    internal static string CleanTranscript(string text)
    {
        var value = Regex.Replace(text.Trim(), @"\[[^\]]*\]|\([^)]*\)", " ");
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value.Any(char.IsLetterOrDigit) ? value : "";
    }

    private void AddLine(ListenLine line)
    {
        Transcript.Add(line);
        _session?.Lines.Add(line);
        OnPropertyChanged(nameof(HasTranscript));
        if (++_linesSinceSave >= 8)
        {
            _linesSinceSave = 0;
            QueueSessionSave();
        }
    }

    /// <summary>
    /// Runs once at start: with no provider accounts and no Ollama, the pane can say so immediately
    /// instead of waiting for the first notes pass to fail.
    /// </summary>
    private async Task CheckNotesAvailabilityAsync(CancellationToken ct)
    {
        if (_appSettings?.Invoke()?.Providers.Accounts.Count > 0) return;
        var provider = _settings.ListenNotesProvider;
        if (provider.Length > 0 && provider != "ollama") return;
        var available = await _notes.OllamaAvailableAsync(ct);
        if (ct.IsCancellationRequested || available) return;
        PostToUi(() =>
        {
            if (_run is null || NotesStatus.Length > 0) return;
            NeedsNotesModel = true;
            NotesStatus = "Notes need a model.";
        });
    }

    private async Task MaybeRunNotesAsync()    {
        if (_notesRunning || _disposed) return;
        var run = _run;
        if (run is null) return;

        var chars = Transcript.Sum(line => line.Text.Length);
        var now = DateTimeOffset.Now;
        if (!_notesGate.Due(chars, now)) return;
        var transcript = ListenNotes.BuildPrompt(Transcript);
        if (transcript.Length == 0) return;

        _notesRunning = true;
        _notesGate.Mark(chars, now);
        NotesBusy = true;
        try
        {
            var accounts = _appSettings?.Invoke()?.Providers.Accounts ?? [];
            var answer = await _notes.AskAsync(transcript, _settings.ListenNotesModel, _settings.ListenNotesProvider, accounts, run.Token);
            if (answer.Result is { } result)
            {
                Summary = result.Summary;
                ActionItems.Clear();
                foreach (var item in result.ActionItems) ActionItems.Add(item);
                Replies.Clear();
                foreach (var reply in result.Replies) Replies.Add(reply);
                NeedsNotesModel = false;
                NotesStatus = "Updated " + DateTimeOffset.Now.ToString("h:mm tt");
                QueueSessionSave();
            }
            else
            {
                // The previous summary and replies stay put; only the status line changes.
                NeedsNotesModel = answer.NeedsModel;
                NotesStatus = answer.Error;
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped or the window was cancelled.
        }
        catch (Exception ex)
        {
            Atoll.Log.Error("Listen notes pass failed", ex);
            NotesStatus = "Notes could not be updated. The previous summary is still here.";
        }
        finally
        {
            _notesRunning = false;
            NotesBusy = false;
        }
    }

    /// <summary>Disposing a Whisper context while it decodes can take the process down; wait for the window.</summary>
    private void ReleaseTranscriber(Task? decode)
    {
        var transcriber = _transcriber;
        _transcriber = null;
        if (transcriber is null) return;
        if (decode is null || decode.IsCompleted)
        {
            transcriber.Dispose();
            return;
        }
        _ = decode.ContinueWith(_ => transcriber.Dispose(), TaskScheduler.Default);
    }

    private void SetTranscribing(bool value)    {
        if (IsTranscribing == value) return;
        PostToUi(() => IsTranscribing = value);
    }

    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private string Language(string model)
    {
        // A ggml model with ".en" only understands English, whatever the language hint says.
        return model.Contains(".en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : _settings.ListenLanguage.Trim();
    }

    private void RefreshNotesSources()
    {
        var previous = _settings.ListenNotesProvider;
        NotesSources.Clear();
        NotesSources.Add(new ListenNotesSource("", "Automatic"));
        NotesSources.Add(new ListenNotesSource("ollama", "Ollama (local)"));
        foreach (var account in _appSettings?.Invoke()?.Providers.Accounts ?? [])
            NotesSources.Add(new ListenNotesSource(account.Id, account.Name));
        NotesSource = NotesSources.FirstOrDefault(source => source.Id == previous) ?? NotesSources[0];
    }

    partial void OnNotesSourceChanged(ListenNotesSource? value)
    {
        if (value is null) return;
        _settings.ListenNotesProvider = value.Id;
        NeedsNotesModel = false;
        NotesStatus = "";
        _save();
    }

    partial void OnNotesModelChanged(string value)
    {
        _settings.ListenNotesModel = value;
        _save();
    }

    partial void OnCaptureMicChanged(bool value)
    {
        _settings.ListenMic = value;
        _save();
    }

    partial void OnCaptureSystemChanged(bool value)
    {
        _settings.ListenSystemAudio = value;
        _save();
    }

    partial void OnNotesTextChanged(string value)
    {
        if (_session is not null) _session.Notes = value;
        QueueSessionSave();
    }

    private void QueueSessionSave()
    {
        if (_session is null || _disposed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSession()
    {
        _saveTimer.Stop();
        var session = _session;
        if (session is null) return;
        session.Notes = NotesText;
        session.Summary = Summary;
        session.ActionItems = ActionItems.ToList();
        session.Replies = Replies.ToList();
        if (session.Lines.Count == 0 && session.Notes.Length == 0) return;
        try { _store.Save(session); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Atoll.Log.Error("Listen session could not be saved", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notesTimer.Stop();
        _saveTimer.Stop();
        var run = _run;
        _run = null;
        run?.Cancel();
        if (_capture is not null)
        {
            _capture.FrameReady -= OnFrame;
            _capture.Dispose();
            _capture = null;
        }
        _frames?.Writer.TryComplete();
        _frames = null;
        // The app is closing: free native and file handles without waiting for a decode to unwind.
        ReleaseTranscriber(_transcribeTask);
        _transcribeTask = null;
        _audio?.Dispose();
        _audio = null;
        FlushSession();
        if (IsListening) _listeningChanged?.Invoke(false);
        IsListening = false;
        run?.Dispose();
        _notes.Dispose();
    }
}
