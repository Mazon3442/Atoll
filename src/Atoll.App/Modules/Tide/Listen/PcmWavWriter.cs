using System.Text;
using Atoll.Modules.Media;

namespace Atoll.Modules.Tide.Listen;

/// <summary>
/// Writes the mixed session audio as 16-bit mono WAV when the user turns Keep audio on. The header is
/// patched on dispose because the length is not known while listening.
/// </summary>
internal sealed class PcmWavWriter : IDisposable
{
    private const int HeaderBytes = 44;
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private int _dataBytes;
    private bool _closed;

    public PcmWavWriter(string path)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        WriteHeader(0);
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (_closed) return;
        foreach (var sample in samples)
        {
            var value = (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);
            _writer.Write(value);
        }
        _dataBytes += samples.Length * 2;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _stream.Seek(0, SeekOrigin.Begin);
            WriteHeader(_dataBytes);
            _stream.Flush();
        }
        finally
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }

    private void WriteHeader(int dataBytes)
    {
        _writer.Write("RIFF"u8);
        _writer.Write(36 + dataBytes);
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16);
        _writer.Write((short)1); // PCM
        _writer.Write((short)1); // mono
        _writer.Write(PcmAudio.TargetRate);
        _writer.Write(PcmAudio.TargetRate * 2);
        _writer.Write((short)2);
        _writer.Write((short)16);
        _writer.Write("data"u8);
        _writer.Write(dataBytes);
    }
}
