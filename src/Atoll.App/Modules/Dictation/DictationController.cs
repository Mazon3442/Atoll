using Atoll.Modules.Tide.Listen;

namespace Atoll.Modules.Dictation;

internal enum DictationState { Idle, Recording, Transcribing }

/// <summary>Records the microphone as 16 kHz mono samples. The real one wraps <see cref="ListenCapture"/>.</summary>
internal interface IDictationRecorder : IDisposable
{
    /// <summary>Loudness of the latest 100 ms, 0 to 1, for the voice meter.</summary>
    event Action<float>? Level;

    Task StartAsync();

    /// <summary>Stops recording and returns everything heard since <see cref="StartAsync"/>.</summary>
    float[] Finish();
}

/// <summary>
/// Push to talk: the first call starts recording, the second stops it, transcribes what was said once and hands
/// the text over. The model loads while the person is still speaking. Nothing here knows about windows or the
/// clipboard, so the whole flow is testable with fakes.
/// </summary>
internal sealed class DictationController(
    Func<IDictationRecorder> createRecorder,
    Func<CancellationToken, Task<ISpeechTranscriber>> loadTranscriber,
    Action<string> deliver,
    Func<string?> micBusy,
    TimeProvider time) : IDisposable
{
    /// <summary>Anything shorter than this is a stray key press, not speech.</summary>
    internal const int MinimumSamples = 16000 * 4 / 10;

    private IDictationRecorder? _recorder;
    private Task<ISpeechTranscriber>? _loading;
    private CancellationTokenSource? _run;
    private DateTimeOffset _started;

    public DictationState State { get; private set; }

    /// <summary>One line for the pill: what is happening, or why nothing was typed.</summary>
    public string Message { get; private set; } = "";

    public float Level { get; private set; }
    public TimeSpan Elapsed => State == DictationState.Recording ? time.GetUtcNow() - _started : TimeSpan.Zero;

    /// <summary>The last text handed to <paramref name="deliver"/>, or null if the last run produced none.</summary>
    public string? LastText { get; private set; }

    public event Action? Changed;

    public async Task ToggleAsync()
    {
        switch (State)
        {
            case DictationState.Transcribing: return;
            case DictationState.Recording: await FinishAsync(); return;
            default: await StartAsync(); return;
        }
    }

    private async Task StartAsync()
    {
        LastText = null;
        if (micBusy() is { } owner)
        {
            Fail($"{owner} is using the microphone.");
            return;
        }

        var recorder = createRecorder();
        recorder.Level += OnLevel;
        try { await recorder.StartAsync(); }
        catch (Exception ex)
        {
            recorder.Level -= OnLevel;
            recorder.Dispose();
            Fail(ex is ListenCaptureException ? ex.Message : "The microphone could not be started.");
            return;
        }

        _recorder = recorder;
        _run = new CancellationTokenSource();
        _started = time.GetUtcNow();
        Level = 0;
        Message = "Listening";
        State = DictationState.Recording;
        // Download and load the model now, so it is ready when the second press comes.
        _loading = loadTranscriber(_run.Token);
        Changed?.Invoke();
    }

    private async Task FinishAsync()
    {
        var recorder = _recorder;
        var run = _run;
        if (recorder is null || run is null) return;
        _recorder = null;
        State = DictationState.Transcribing;
        Message = "Transcribing";
        Level = 0;
        Changed?.Invoke();

        float[] samples;
        try { samples = recorder.Finish(); }
        finally
        {
            recorder.Level -= OnLevel;
            recorder.Dispose();
        }

        try
        {
            if (samples.Length < MinimumSamples)
            {
                Fail("That was too short.");
                return;
            }
            var transcriber = await _loading!;
            var raw = await transcriber.TranscribeAsync(samples, run.Token);
            var text = ListenViewModel.CleanTranscript(raw);
            if (text.Length == 0)
            {
                Fail("No speech heard.");
                return;
            }
            LastText = text;
            deliver(text);
            Message = "Text copied";
            State = DictationState.Idle;
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            State = DictationState.Idle;
            Message = "";
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Dictation failed", ex);
            Fail(ex is System.Net.Http.HttpRequestException ? "The speech model could not be downloaded." : "The speech model could not run.");
        }
        finally
        {
            run.Dispose();
            if (ReferenceEquals(_run, run)) _run = null;
        }
    }

    /// <summary>Drops the recording without transcribing it.</summary>
    public void Cancel()
    {
        if (State == DictationState.Transcribing) { _run?.Cancel(); return; }
        if (State != DictationState.Recording) return;
        _run?.Cancel();
        if (_recorder is { } live) live.Level -= OnLevel;
        _recorder?.Dispose();
        _recorder = null;
        State = DictationState.Idle;
        Message = "";
        Changed?.Invoke();
    }

    private void Fail(string message)
    {
        State = DictationState.Idle;
        Message = message;
        Level = 0;
        Changed?.Invoke();
    }

    private void OnLevel(float level)
    {
        Level = Math.Clamp(level * 6, 0, 1); // speech sits around 0.02 to 0.15 RMS
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _run?.Cancel();
        _recorder?.Dispose();
    }
}
