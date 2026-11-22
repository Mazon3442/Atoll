using Atoll.Modules.Dictation;
using Atoll.Modules.Tide.Listen;
using Microsoft.Extensions.Time.Testing;

namespace Atoll.Tests;

public class DictationTests
{
    private sealed class FakeRecorder(float[] samples, bool failToStart = false) : IDictationRecorder
    {
        public bool Disposed { get; private set; }
        public event Action<float>? Level;
        public void Speak(float level) => Level?.Invoke(level);

        public Task StartAsync() => failToStart ? throw new ListenCaptureException("No microphone.") : Task.CompletedTask;
        public float[] Finish() => samples;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeTranscriber(string text) : ISpeechTranscriber
    {
        public int Calls { get; private set; }
        public Task<string> TranscribeAsync(float[] samples, CancellationToken ct) { Calls++; return Task.FromResult(text); }
        public void Dispose() { }
    }

    private static readonly float[] Speech = new float[DictationController.MinimumSamples * 4];

    private static (DictationController Controller, List<string> Delivered, FakeRecorder Recorder, FakeTranscriber Transcriber) Make(
        string heard = "Hello there", float[]? samples = null, string? busy = null, bool failToStart = false)
    {
        var delivered = new List<string>();
        var recorder = new FakeRecorder(samples ?? Speech, failToStart);
        var transcriber = new FakeTranscriber(heard);
        var controller = new DictationController(() => recorder, _ => Task.FromResult<ISpeechTranscriber>(transcriber),
            delivered.Add, () => busy, new FakeTimeProvider());
        return (controller, delivered, recorder, transcriber);
    }

    [Fact]
    public async Task Press_speak_press_delivers_the_cleaned_text_once()
    {
        var (controller, delivered, recorder, transcriber) = Make("[BLANK_AUDIO] Hello there (music)");

        await controller.ToggleAsync();
        Assert.Equal(DictationState.Recording, controller.State);
        await controller.ToggleAsync();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal("Hello there", Assert.Single(delivered));
        Assert.Equal("Hello there", controller.LastText);
        Assert.Equal(1, transcriber.Calls);
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task A_stray_tap_is_too_short_to_transcribe()
    {
        var (controller, delivered, _, transcriber) = Make(samples: new float[100]);

        await controller.ToggleAsync();
        await controller.ToggleAsync();

        Assert.Empty(delivered);
        Assert.Equal(0, transcriber.Calls);
        Assert.Contains("short", controller.Message);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    [Fact]
    public async Task Silence_or_noise_delivers_nothing_and_says_so()
    {
        var (controller, delivered, _, _) = Make("[BLANK_AUDIO]");

        await controller.ToggleAsync();
        await controller.ToggleAsync();

        Assert.Empty(delivered);
        Assert.Null(controller.LastText);
        Assert.Contains("No speech", controller.Message);
    }

    [Fact]
    public async Task A_busy_microphone_is_named_and_nothing_starts()
    {
        var (controller, _, _, _) = Make(busy: "Listen");

        await controller.ToggleAsync();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal("Listen is using the microphone.", controller.Message);
    }

    [Fact]
    public async Task A_microphone_that_cannot_start_reports_why()
    {
        var (controller, _, recorder, _) = Make(failToStart: true);

        await controller.ToggleAsync();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal("No microphone.", controller.Message);
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task Cancel_drops_the_recording_without_delivering()
    {
        var (controller, delivered, recorder, transcriber) = Make();

        await controller.ToggleAsync();
        controller.Cancel();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Empty(delivered);
        Assert.Equal(0, transcriber.Calls);
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task The_level_follows_the_voice_and_is_clamped()
    {
        var (controller, _, recorder, _) = Make();

        await controller.ToggleAsync();
        recorder.Speak(0.05f);
        Assert.InRange(controller.Level, 0.25, 0.35);
        recorder.Speak(0.9f);
        Assert.Equal(1, controller.Level);
    }
}
