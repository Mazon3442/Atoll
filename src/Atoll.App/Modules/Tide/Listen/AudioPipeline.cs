using Atoll.Modules.Media;

namespace Atoll.Modules.Tide.Listen;

/// <summary>
/// Streaming linear resampler from a device rate to 16 kHz. The phase survives block boundaries, so a
/// capture callback split into odd-sized packets does not produce a click every packet.
/// </summary>
internal sealed class StreamResampler
{
    private readonly double _step;
    private readonly bool _identity;
    private float _previous;
    private double _position;
    private bool _started;

    public StreamResampler(int inputRate, int outputRate = PcmAudio.TargetRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 1);
        _step = (double)inputRate / outputRate;
        _identity = inputRate == outputRate;
    }

    /// <summary>Appends the resampled output to <paramref name="output"/>; the caller clears it per block.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        if (input.IsEmpty) return;
        if (_identity)
        {
            output.AddRange(input);
            return;
        }
        if (!_started)
        {
            _started = true;
            _previous = input[0];
        }
        foreach (var current in input)
        {
            while (_position < 1)
            {
                output.Add(_previous + (current - _previous) * (float)_position);
                _position += _step;
            }
            _position -= 1;
            _previous = current;
        }
    }
}

/// <summary>
/// Bounded mono ring between the WASAPI callback and the wall-clock assembler. Overflow drops the oldest
/// audio: a slow consumer must never make the capture thread queue without bound.
/// </summary>
internal sealed class AudioRing(int capacity)
{
    private readonly float[] _buffer = new float[Math.Max(1, capacity)];
    private readonly object _gate = new();
    private int _read;
    private int _count;

    public int Capacity => _buffer.Length;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        lock (_gate)
        {
            if (samples.Length >= _buffer.Length)
            {
                samples[^_buffer.Length..].CopyTo(_buffer);
                _read = 0;
                _count = _buffer.Length;
                return;
            }
            var overflow = Math.Max(0, _count + samples.Length - _buffer.Length);
            if (overflow > 0)
            {
                _read = (_read + overflow) % _buffer.Length;
                _count -= overflow;
            }
            var write = (_read + _count) % _buffer.Length;
            var first = Math.Min(samples.Length, _buffer.Length - write);
            samples[..first].CopyTo(_buffer.AsSpan(write));
            if (first < samples.Length) samples[first..].CopyTo(_buffer);
            _count += samples.Length;
        }
    }

    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            var count = Math.Min(destination.Length, _count);
            var first = Math.Min(count, _buffer.Length - _read);
            _buffer.AsSpan(_read, first).CopyTo(destination);
            if (first < count) _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);
            _read = (_read + count) % _buffer.Length;
            _count -= count;
            return count;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _read = 0;
            _count = 0;
        }
    }
}

/// <summary>One assembled 100 ms frame plus the per-source level, which is what speaker labels come from.</summary>
internal sealed record AudioFrame(float[] Samples, float MicLevel, float SystemLevel);

/// <summary>Sums mic and loopback with headroom and ducks the mic while the call is loud.</summary>
internal static class FrameMixer
{
    public const int FrameSamples = PcmAudio.TargetRate / 10; // 100 ms
    public const float DuckThreshold = 0.04f; // about -28 dBFS: the other side is clearly talking
    public const float DuckGain = 0.35f;

    // Two full-scale sources added together would clip; leave room before the soft clamp does the rest.
    private const float Headroom = 0.7f;

    public static AudioFrame Mix(ReadOnlySpan<float> mic, ReadOnlySpan<float> system)
    {
        var micLevel = PcmAudio.Rms(mic);
        var systemLevel = PcmAudio.Rms(system);
        var duck = systemLevel > DuckThreshold ? DuckGain : 1f;
        var samples = new float[FrameSamples];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = SoftClamp((mic[i] * duck + system[i]) * Headroom);
        return new AudioFrame(samples, micLevel, systemLevel);
    }

    /// <summary>Linear below the knee and asymptotic above it, so loud speech rounds instead of clipping flat.</summary>
    public static float SoftClamp(float sample)
    {
        const float knee = 0.9f;
        var magnitude = MathF.Abs(sample);
        if (magnitude <= knee) return sample;
        var over = (magnitude - knee) / (1 - knee);
        return MathF.CopySign(knee + (1 - knee) * MathF.Tanh(over), sample);
    }
}

/// <summary>
/// Pulls one fixed frame per source on every wall-clock tick. On underrun it repeats the last frame rather
/// than shortening the frame, so the transcription window always advances with wall time.
/// </summary>
internal sealed class FrameAssembler(AudioRing mic, AudioRing system)
{
    private readonly float[] _micFrame = new float[FrameMixer.FrameSamples];
    private readonly float[] _systemFrame = new float[FrameMixer.FrameSamples];
    private readonly float[] _lastMicFrame = new float[FrameMixer.FrameSamples];
    private readonly float[] _lastSystemFrame = new float[FrameMixer.FrameSamples];
    private bool _micHeard;
    private bool _systemHeard;

    /// <summary>Null until a source has delivered something, so total silence produces no transcript work.</summary>
    public AudioFrame? Pull()
    {
        Fill(mic, _micFrame, _lastMicFrame, ref _micHeard);
        Fill(system, _systemFrame, _lastSystemFrame, ref _systemHeard);
        if (!_micHeard && !_systemHeard) return null;
        return FrameMixer.Mix(_micFrame, _systemFrame);
    }

    private static void Fill(AudioRing ring, float[] frame, float[] last, ref bool heard)
    {
        var read = ring.Read(frame);
        if (read < frame.Length)
        {
            if (read > 0) heard = true;
            if (heard) Array.Copy(last, 0, frame, read, frame.Length - read);
            else Array.Clear(frame, read, frame.Length - read);
        }
        if (read > 0) heard = true;
        if (heard) Array.Copy(frame, last, frame.Length);
    }
}

/// <summary>
/// Accumulates frames into ~6 s windows for Whisper. Windows below the RMS gate are skipped, and one second
/// of overlap stays behind so a sentence crossing a window boundary is not cut in half.
/// </summary>
internal sealed class VadWindow
{
    public const int WindowSamples = PcmAudio.TargetRate * 6;
    public const int OverlapSamples = PcmAudio.TargetRate; // 1 s
    public const float SilenceRms = 0.008f;

    private readonly List<float> _samples = new(WindowSamples + FrameMixer.FrameSamples);
    private readonly List<(float Mic, float System)> _levels = [];
    private double _micEnergy;
    private double _systemEnergy;

    public int Count => _samples.Count;

    public void Append(AudioFrame frame)
    {
        _samples.AddRange(frame.Samples);
        _levels.Add((frame.MicLevel, frame.SystemLevel));
        _micEnergy += frame.MicLevel * frame.MicLevel * frame.Samples.Length;
        _systemEnergy += frame.SystemLevel * frame.SystemLevel * frame.Samples.Length;
    }

    /// <summary>
    /// True when a full, loud-enough window is ready. <paramref name="you"/> labels the window by which
    /// source carried more energy: the microphone is the user, loopback is everyone else on the call.
    /// </summary>
    public bool TryTake(out float[] window, out bool you)
    {
        window = [];
        you = false;
        if (_samples.Count < WindowSamples) return false;

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_samples);
        if (PcmAudio.Rms(span[..WindowSamples]) < SilenceRms)
        {
            // Quiet stretch: keep only the trailing overlap so an idle call does not grow forever.
            Drop(_samples.Count - OverlapSamples);
            return false;
        }

        window = span[..WindowSamples].ToArray();
        you = _micEnergy >= _systemEnergy && _micEnergy > 0;
        Drop(WindowSamples - OverlapSamples);
        return true;
    }

    public void Clear()
    {
        _samples.Clear();
        _levels.Clear();
        _micEnergy = 0;
        _systemEnergy = 0;
    }

    private void Drop(int samples)
    {
        if (samples <= 0) return;
        var frames = Math.Min(samples / FrameMixer.FrameSamples, _levels.Count);
        var removed = frames * FrameMixer.FrameSamples;
        if (removed == 0) return;
        for (var i = 0; i < frames; i++)
        {
            var (mic, system) = _levels[i];
            _micEnergy -= mic * mic * FrameMixer.FrameSamples;
            _systemEnergy -= system * system * FrameMixer.FrameSamples;
        }
        _micEnergy = Math.Max(0, _micEnergy);
        _systemEnergy = Math.Max(0, _systemEnergy);
        _levels.RemoveRange(0, frames);
        _samples.RemoveRange(0, removed);
    }
}
