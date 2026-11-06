using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;

namespace Island.Modules.Media;

/// <summary>
/// Captures what the speakers play (WASAPI loopback) and reduces it to a few 0–1 band levels for
/// the visualizer. <see cref="LevelsAvailable"/> fires about 20 times a second on a capture thread.
/// </summary>
public sealed class AudioLevels : IDisposable
{
    public const int Bands = 5;
    private const int FftExponent = 10;
    private const int FftSize = 1 << FftExponent;
    private static readonly double[] BandEdgesHz = [60, 150, 400, 1000, 2500, 6000];
    private static readonly long EmitInterval = Stopwatch.Frequency / 20;

    private readonly float[] _samples = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _levels = new float[Bands];
    private readonly object _gate = new();
    private WasapiRecorder? _recorder;
    private WaveFormat? _format;
    private int _filled;
    private long _lastEmit;

    public event Action<float[]>? LevelsAvailable;

    /// <summary>Starts loopback capture of the current default output device.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_recorder is not null) return;
            try
            {
                _recorder = new WasapiRecorderBuilder().WithLoopbackCapture().Build();
                _format = _recorder.WaveFormat;
                _recorder.DataAvailable += (buffer, _, _, _) => OnData(buffer);
                _recorder.StartRecording();
            }
            catch (Exception ex)
            {
                Log.Error("Audio visualizer could not start", ex);
                _recorder?.Dispose();
                _recorder = null;
            }
        }
    }

    public void Stop()
    {
        WasapiRecorder? recorder;
        lock (_gate)
        {
            recorder = _recorder;
            _recorder = null;
        }
        if (recorder is null) return;
        try { recorder.StopRecording(); }
        catch (Exception ex) { Log.Error("Audio visualizer stop failed", ex); }
        recorder.Dispose();
        Array.Clear(_levels);
        LevelsAvailable?.Invoke((float[])_levels.Clone());
    }

    private void OnData(ReadOnlySpan<byte> buffer)
    {
        // Shared-mode loopback delivers float32 on every Windows build this app supports.
        if (_format is not { Encoding: WaveFormatEncoding.IeeeFloat, BitsPerSample: 32 } format) return;

        var channels = format.Channels;
        var frames = MemoryMarshal.Cast<byte, float>(buffer);
        for (var i = 0; i + channels <= frames.Length; i += channels)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += frames[i + c];
            _samples[_filled++] = sum / channels;
            if (_filled == FftSize)
            {
                Analyze(format.SampleRate);
                _filled = 0;
            }
        }
    }

    private void Analyze(int sampleRate)
    {
        for (var i = 0; i < FftSize; i++)
        {
            var hann = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
            _fft[i].X = (float)(_samples[i] * hann);
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftExponent, _fft);

        for (var b = 0; b < Bands; b++)
        {
            var from = Math.Max(1, (int)(BandEdgesHz[b] * FftSize / sampleRate));
            var to = Math.Min(FftSize / 2, Math.Max(from + 1, (int)(BandEdgesHz[b + 1] * FftSize / sampleRate)));
            double peak = 0;
            for (var k = from; k < to; k++)
                peak = Math.Max(peak, Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y));

            // NAudio's forward FFT divides by N; map roughly -70..-15 dB to 0..1.
            var db = 20 * Math.Log10(peak + 1e-9);
            var level = (float)Math.Clamp((db + 70) / 55, 0, 1);
            _levels[b] = Math.Max(level, _levels[b] * 0.75f); // fast attack, smooth fall
        }

        var now = Stopwatch.GetTimestamp();
        if (now - _lastEmit < EmitInterval) return;
        _lastEmit = now;
        LevelsAvailable?.Invoke((float[])_levels.Clone());
    }

    public void Dispose() => Stop();
}
