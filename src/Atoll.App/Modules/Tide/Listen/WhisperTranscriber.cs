using System.Text;
using Whisper.net;

namespace Atoll.Modules.Tide.Listen;

/// <summary>Transcribes one 16 kHz mono float window. Implemented by Whisper.net; tests do not load a model.</summary>
internal interface ISpeechTranscriber : IDisposable
{
    Task<string> TranscribeAsync(float[] samples, CancellationToken ct);
}

/// <summary>
/// Whisper.net on the CPU. The model is loaded once per Listen run and freed on stop, because keeping a
/// base.en context alive costs a few hundred MB for a feature that is not always on.
/// </summary>
internal sealed class WhisperTranscriber : ISpeechTranscriber
{
    private readonly WhisperFactory _factory;
    private readonly string _language;
    private readonly int _threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    private WhisperTranscriber(WhisperFactory factory, string language)
    {
        _factory = factory;
        _language = language;
    }

    /// <summary>English-only ggml models reject any hint other than "en", so the caller resolves that first.</summary>
    public static WhisperTranscriber Load(string modelPath, string language) =>
        new(WhisperFactory.FromPath(modelPath), language);

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        var builder = _factory.CreateBuilder().WithThreads(_threads).WithNoContext();
        builder = string.IsNullOrWhiteSpace(_language) ? builder.WithLanguageDetection() : builder.WithLanguage(_language);
        using var processor = builder.Build();

        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
        {
            var part = segment.Text?.Trim();
            if (string.IsNullOrEmpty(part)) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(part);
        }
        return text.ToString();
    }

    public void Dispose() => _factory.Dispose();
}
