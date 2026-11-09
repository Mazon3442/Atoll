using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Atoll.Modules.Media;

/// <summary>
/// Captures what the speakers play (WASAPI loopback) and reduces it to a few 0–1 band levels for
/// the visualizer. <see cref="LevelsAvailable"/> fires about 20 times a second on a capture thread.
/// </summary>
public sealed class AudioLevels : IDisposable
{
    public const int Bands = 5;
    private const int FftExponent = 10;
    private const int FftSize = 1 << FftExponent;
    private static readonly double[] HannWindow = Enumerable.Range(0, FftSize)
        .Select(i => 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)))).ToArray();
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
    private int _generation;
    private uint? _processId;
    private bool _starting;
    private bool _disposed;

    public event Action<float[]>? LevelsAvailable;
    public event Action? Unavailable;

    /// <summary>Starts loopback capture of the current default output device.</summary>
    public void Start(uint? processId = null)
    {
        lock (_gate)
        {
            if (_disposed || (_recorder is not null || _starting) && _processId == processId) return;
        }
        Stop();
        int generation;
        lock (_gate) { if (_disposed) return; generation = ++_generation; _processId = processId; _starting = true; }
        _ = StartAsync(processId, generation);
    }

    private async Task StartAsync(uint? processId, int generation)
    {
        WasapiRecorder? recorder = null;
        try
        {
            var builder = new WasapiRecorderBuilder();
            if (processId is { } pid) builder.WithProcessLoopback(pid, ProcessLoopbackMode.IncludeTargetProcessTree);
            else builder.WithLoopbackCapture();
            recorder = await builder.BuildAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || generation != _generation) { recorder.Dispose(); return; }
                _recorder = recorder; _format = recorder.WaveFormat;
                if (SampleBits(_format) == 0) Log.Info($"Visualizer: unsupported audio format {_format}; bars stay still");
                recorder.DataAvailable += (buffer, _, _, _) => { lock (_gate) { if (generation == _generation) OnData(buffer); } };
                recorder.StartRecording(); _starting = false;
            }
        }
        catch (Exception ex)
        {
            bool current;
            lock (_gate) { current = !_disposed && generation == _generation; if (current) { _recorder = null; _starting = false; } }
            recorder?.Dispose();
            Log.Error("Selected audio visualizer could not start", ex);
            if (current) Unavailable?.Invoke();
        }
    }

    public void Stop()
    {
        WasapiRecorder? recorder;
        lock (_gate)
        {
            _generation++; _starting = false;
            recorder = _recorder;
            _recorder = null;
            _filled = 0;
        }
        if (recorder is null) return;
        try { recorder.StopRecording(); }
        catch (Exception ex) { Log.Error("Audio visualizer stop failed", ex); }
        recorder.Dispose();
        Array.Clear(_levels);
        LevelsAvailable?.Invoke((float[])_levels.Clone());
    }

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>Shared-mode loopback is 32-bit float, usually wrapped as WAVE_FORMAT_EXTENSIBLE.</summary>
    internal static bool IsFloat32(WaveFormat? f) => f is { BitsPerSample: 32 } &&
        (f.Encoding == WaveFormatEncoding.IeeeFloat || (f is WaveFormatExtensible x && x.SubFormat == FloatSubFormat));

    /// <summary>
    /// Bits per sample the visualizer can read: 32 for float, 16/24/32 for integer PCM (exclusive-mode devices and
    /// some USB DACs loop back integer audio). 0 when unsupported.
    /// </summary>
    internal static int SampleBits(WaveFormat? f)
    {
        if (IsFloat32(f)) return 32;
        var pcm = f is not null && (f.Encoding == WaveFormatEncoding.Pcm || f is WaveFormatExtensible x && x.SubFormat == PcmSubFormat);
        return pcm && f!.BitsPerSample is 16 or 24 or 32 ? f.BitsPerSample : 0;
    }

    /// <summary>One sample as -1..1.</summary>
    internal static float ReadSample(ReadOnlySpan<byte> b, bool isFloat, int bits) => bits switch
    {
        32 when isFloat => BinaryPrimitives.ReadSingleLittleEndian(b),
        16 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
        24 => (b[0] | b[1] << 8 | (sbyte)b[2] << 16) / 8388608f,
        32 => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
        _ => 0,
    };

    private void OnData(ReadOnlySpan<byte> buffer)
    {
        var bits = SampleBits(_format);
        if (bits == 0) return;
        var isFloat = IsFloat32(_format);
        var bytes = bits / 8;
        var channels = _format!.Channels;
        var frameBytes = _format.BlockAlign > 0 ? _format.BlockAlign : channels * bytes;
        // Front left/right only: surround layouts (e.g. 7.1) are mostly silent channels that would
        // dilute the level.
        var used = Math.Min(channels, 2);
        for (var i = 0; i + frameBytes <= buffer.Length; i += frameBytes)
        {
            float sum = 0;
            for (var c = 0; c < used; c++) sum += ReadSample(buffer.Slice(i + c * bytes, bytes), isFloat, bits);
            _samples[_filled++] = sum / used;
            if (_filled == FftSize)
            {
                Analyze(_format.SampleRate);
                _filled = 0;
            }
        }
    }

    private void Analyze(int sampleRate)
    {
        for (var i = 0; i < FftSize; i++)
        {
            _fft[i].X = (float)(_samples[i] * HannWindow[i]);
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

    public void Dispose() { lock (_gate) _disposed = true; Stop(); }
}
