using System.Runtime.InteropServices;
using Atoll.Modules.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Atoll.Modules.Tide.Listen;

/// <summary>A capture source could not start; the message is what the Listen pane shows.</summary>
internal sealed class ListenCaptureException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Captures the microphone and the default output device's loopback at the same time, resamples both to
/// 16 kHz mono and assembles 100 ms frames on wall clock. Audio never leaves the machine.
/// </summary>
internal sealed class ListenCapture : IDisposable
{
    private const int RingMs = 200;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly AudioRing _mic = new(PcmAudio.TargetRate * RingMs / 1000);
    private readonly AudioRing _system = new(PcmAudio.TargetRate * RingMs / 1000);
    private WasapiRecorder? _micRecorder;
    private WasapiRecorder? _systemRecorder;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public bool MicAvailable { get; private set; }
    public bool SystemAvailable { get; private set; }

    /// <summary>Raised on the assembler thread about ten times a second.</summary>
    public event Action<AudioFrame>? FrameReady;

    /// <summary>
    /// Starts the requested sources. A missing microphone is fatal (the caller asked for it); a missing
    /// loopback endpoint is not, because a meeting can still be transcribed from the mic alone.
    /// </summary>
    public async Task StartAsync(bool captureMic, bool captureSystem)
    {
        if (!captureMic && !captureSystem)
            throw new ListenCaptureException("Choose the microphone, system audio, or both.");
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            if (captureMic) await StartMicrophoneAsync();
            if (captureSystem) await StartSystemAudioAsync();
            if (!MicAvailable && !SystemAvailable)
                throw new ListenCaptureException("No audio device is available to listen to.");

            _pump = Task.Run(() => PumpAsync(cts.Token));
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private async Task StartMicrophoneAsync()
    {
        WasapiRecorder? recorder = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            // Communications role is the endpoint Windows uses for calls, which is the one the user expects.
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            recorder = await new WasapiRecorderBuilder().WithDevice(device).BuildAsync();
            if (PcmAudio.SampleBits(recorder.WaveFormat) == 0)
                throw new ListenCaptureException($"The microphone format ({recorder.WaveFormat}) is not supported.");
            var pipe = new SourcePipe(_mic, recorder.WaveFormat);
            recorder.DataAvailable += (buffer, _, _, _) => pipe.Write(buffer);
            recorder.StartRecording();
            _micRecorder = recorder;
            MicAvailable = true;
        }
        catch (ListenCaptureException)
        {
            recorder?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            recorder?.Dispose();
            throw new ListenCaptureException("No microphone is available.", ex);
        }
        catch (Exception ex)
        {
            recorder?.Dispose();
            throw new ListenCaptureException("The microphone could not be opened.", ex);
        }
    }

    private async Task StartSystemAudioAsync()
    {
        WasapiRecorder? recorder = null;
        try
        {
            // Loopback follows the default output device: that is where the other call participants are.
            recorder = await new WasapiRecorderBuilder().WithLoopbackCapture().BuildAsync();
            if (PcmAudio.SampleBits(recorder.WaveFormat) == 0)
                throw new InvalidOperationException($"Unsupported loopback format {recorder.WaveFormat}.");
            var pipe = new SourcePipe(_system, recorder.WaveFormat);
            recorder.DataAvailable += (buffer, _, _, _) => pipe.Write(buffer);
            recorder.StartRecording();
            _systemRecorder = recorder;
            SystemAvailable = true;
        }
        catch (Exception ex)
        {
            recorder?.Dispose();
            // The mic keeps working; the pane says system audio is unavailable rather than refusing to listen.
            Log.Info($"Listen: system audio capture could not start ({ex.GetType().Name}); continuing without it");
            SystemAvailable = false;
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var assembler = new FrameAssembler(_mic, _system);
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (assembler.Pull() is { } frame) FrameReady?.Invoke(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (ObjectDisposedException)
        {
            // Racing teardown; nothing left to assemble.
        }
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        Stop(_micRecorder);
        _micRecorder = null;
        Stop(_systemRecorder);
        _systemRecorder = null;

        try { _pump?.Wait(TimeSpan.FromMilliseconds(500)); }
        catch (AggregateException) { }
        _pump = null;

        cts?.Dispose();
        _mic.Clear();
        _system.Clear();
        MicAvailable = false;
        SystemAvailable = false;
    }

    private static void Stop(WasapiRecorder? recorder)
    {
        if (recorder is null) return;
        try { recorder.StopRecording(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        recorder.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>Per-source decode, resample and ring write. WASAPI serialises a recorder's callbacks.</summary>
    private sealed class SourcePipe
    {
        private readonly AudioRing _ring;
        private readonly WaveFormat _format;
        private readonly StreamResampler _resampler;
        private readonly float[] _mono = new float[16384];
        private readonly List<float> _resampled = new(PcmAudio.TargetRate / 4);

        public SourcePipe(AudioRing ring, WaveFormat format)
        {
            _ring = ring;
            _format = format;
            _resampler = new StreamResampler(format.SampleRate);
        }

        public void Write(ReadOnlySpan<byte> buffer)
        {
            if (PcmAudio.SampleBits(_format) == 0) return;
            var frameBytes = PcmAudio.FrameBytes(_format);
            if (frameBytes <= 0) return;
            while (buffer.Length >= frameBytes)
            {
                var frames = Math.Min(buffer.Length / frameBytes, _mono.Length);
                if (frames <= 0) return;
                var decoded = PcmAudio.MonoFromDevice(buffer, _format, _mono.AsSpan(0, frames));
                if (decoded == 0) return;
                _resampled.Clear();
                _resampler.Process(_mono.AsSpan(0, decoded), _resampled);
                _ring.Write(CollectionsMarshal.AsSpan(_resampled));
                buffer = buffer[(frames * frameBytes)..];
            }
        }
    }
}
