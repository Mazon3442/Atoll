using System.Speech.Recognition;

namespace Atoll.Modules.Tide;

/// <summary>
/// Local microphone dictation for Follow My Voice. Uses Windows speech recognition; no audio leaves the PC.
/// </summary>
internal sealed class VoiceFollower : IDisposable
{
    private SpeechRecognitionEngine? _engine;

    public event Action<string>? Recognized;

    public bool Available { get; private set; }

    public string Error { get; private set; } = "";

    public bool Start()
    {
        if (_engine is not null) return true;
        try
        {
            var engine = new SpeechRecognitionEngine();
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechRecognized += OnSpeechRecognized;
            engine.RecognizeAsync(RecognizeMode.Multiple);
            _engine = engine;
            Available = true;
            Error = "";
            return true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            Available = false;
            Error = "Windows speech recognition is not available on this PC.";
            return false;
        }
    }

    public void Stop()
    {
        var engine = _engine;
        _engine = null;
        if (engine is null) return;
        try
        {
            engine.SpeechRecognized -= OnSpeechRecognized;
            engine.RecognizeAsyncCancel();
            engine.Dispose();
        }
        catch (Exception)
        {
            // The recognizer is already shutting down.
        }
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var text = e.Result?.Text;
        if (!string.IsNullOrWhiteSpace(text)) Recognized?.Invoke(text);
    }

    public void Dispose() => Stop();
}
