using System.Buffers.Binary;
using NAudio.Wave;

namespace Atoll.Modules.Media;

/// <summary>
/// WASAPI PCM decoding shared by the visualizer and Listen. Both read the same shared-mode formats, so the
/// float/integer rules and the front left/right downmix live here rather than in each consumer.
/// </summary>
internal static class PcmAudio
{
    /// <summary>Whisper works at 16 kHz mono; every Listen source is resampled to this rate.</summary>
    public const int TargetRate = 16_000;

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>Shared-mode loopback is 32-bit float, usually wrapped as WAVE_FORMAT_EXTENSIBLE.</summary>
    public static bool IsFloat32(WaveFormat? format) => format is { BitsPerSample: 32 } &&
        (format.Encoding == WaveFormatEncoding.IeeeFloat ||
         (format is WaveFormatExtensible x && x.SubFormat == FloatSubFormat));

    /// <summary>
    /// Bits per sample that can be read: 32 for float, 16/24/32 for integer PCM (exclusive-mode devices and
    /// some USB DACs loop back integer audio). 0 when unsupported.
    /// </summary>
    public static int SampleBits(WaveFormat? format)
    {
        if (IsFloat32(format)) return 32;
        var pcm = format is not null &&
            (format.Encoding == WaveFormatEncoding.Pcm || format is WaveFormatExtensible x && x.SubFormat == PcmSubFormat);
        return pcm && format!.BitsPerSample is 16 or 24 or 32 ? format.BitsPerSample : 0;
    }

    /// <summary>Bytes per device frame, honouring the block alignment when the driver reports one.</summary>
    public static int FrameBytes(WaveFormat format) => format.BlockAlign > 0
        ? format.BlockAlign
        : Math.Max(0, format.Channels) * (SampleBits(format) / 8);

    /// <summary>One sample as -1..1.</summary>
    public static float ReadSample(ReadOnlySpan<byte> bytes, bool isFloat, int bits) => bits switch
    {
        32 when isFloat => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        24 => (bytes[0] | bytes[1] << 8 | (sbyte)bytes[2] << 16) / 8388608f,
        32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
        _ => 0,
    };

    /// <summary>
    /// Decodes one capture buffer to mono. Uses front left/right only: surround layouts (e.g. 7.1) are mostly
    /// silent channels that would dilute speech. Returns the number of mono samples written.
    /// </summary>
    public static int MonoFromDevice(ReadOnlySpan<byte> buffer, WaveFormat format, Span<float> mono)
    {
        var bits = SampleBits(format);
        if (bits == 0) return 0;
        var isFloat = IsFloat32(format);
        var bytes = bits / 8;
        var channels = format.Channels;
        if (channels <= 0) return 0;
        var frameBytes = FrameBytes(format);
        if (frameBytes <= 0) return 0;
        var used = Math.Min(channels, 2);
        var frames = Math.Min(buffer.Length / frameBytes, mono.Length);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * frameBytes;
            float sum = 0;
            for (var channel = 0; channel < used; channel++)
                sum += ReadSample(buffer.Slice(offset + channel * bytes, bytes), isFloat, bits);
            mono[frame] = sum / used;
        }
        return frames;
    }

    /// <summary>Root-mean-square of a block, the quiet/loud test the VAD gate and the mic duck both use.</summary>
    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
    }
}
