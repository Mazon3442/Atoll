using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace Atoll.Modules.Screen;

/// <summary>
/// Records an area of the screen to an MP4 (H.264) file with Windows' own encoder. A capture thread grabs frames on a
/// steady clock into a small queue, and the encoder pulls them as it needs them, so memory stays flat however long it runs.
/// Video only; there is no sound.
/// </summary>
internal sealed class ScreenRecorder : IDisposable
{
    private readonly PixelRect _area;
    private readonly string _path;
    private readonly int _fps;
    private readonly VideoEncodingQuality _quality;
    private readonly bool _high;
    private readonly BlockingCollection<(byte[] Pixels, TimeSpan At)> _queue = new(boundedCapacity: 6);
    private readonly ConcurrentBag<byte[]> _spare = [];
    private readonly Stopwatch _clock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _go = new();
    private Task? _capture;
    private Task? _encode;
    private MediaStreamSource? _source;
    private IRandomAccessStream? _output;
    private int _grabbed, _refused, _requested, _lastError;

    public ScreenRecorder(PixelRect area, string path, int fps, VideoEncodingQuality quality, bool highQuality = false)
    {
        // H.264 wants even dimensions.
        _area = area with { Width = area.Width & ~1, Height = area.Height & ~1 };
        if (_area.IsEmpty) throw new ArgumentException("The area is too small to record.", nameof(area));
        _path = path;
        _fps = Math.Clamp(fps, 5, 60);
        _quality = quality;
        _high = highQuality;
    }

    public TimeSpan Elapsed => _clock.Elapsed;
    public string Path => _path;

    /// <summary>Frames grabbed, frames the screen refused, samples the encoder asked for, and the last error. For diagnosing a silent file.</summary>
    public (int Grabbed, int Refused, int Requested, int LastError) Counts => (_grabbed, _refused, _requested, _lastError);

    /// <summary>Starts capturing and encoding. Throws if the screen cannot be read or Windows has no H.264 encoder.</summary>
    public async Task StartAsync()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

        // The capture thread makes and owns its device contexts: GDI handles belong to the thread that uses them.
        _capture = Task.Factory.StartNew(CaptureLoop, TaskCreationOptions.LongRunning);
        await _ready.Task;

        var video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_area.Width, (uint)_area.Height);
        video.FrameRate.Numerator = (uint)_fps;
        video.FrameRate.Denominator = 1;
        video.PixelAspectRatio.Numerator = 1;
        video.PixelAspectRatio.Denominator = 1;
        _source = new MediaStreamSource(new VideoStreamDescriptor(video)) { BufferTime = TimeSpan.Zero, CanSeek = false };
        _source.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(_quality);
        profile.Video.Width = (uint)_area.Width;
        profile.Video.Height = (uint)_area.Height;
        profile.Video.FrameRate.Numerator = (uint)_fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = Bitrate(_area.Width, _area.Height, _fps, _high);
        profile.Audio = null;

        var file = File.Create(_path);
        _output = file.AsRandomAccessStream();
        var prepared = await new MediaTranscoder { HardwareAccelerationEnabled = true }.PrepareMediaStreamSourceTranscodeAsync(_source, _output, profile);
        if (!prepared.CanTranscode)
        {
            Dispose();
            throw new InvalidOperationException("Windows has no video encoder available (" + prepared.FailureReason + ").");
        }
        _clock.Start();
        _go.Set();
        _encode = prepared.TranscodeAsync().AsTask();
    }

    /// <summary>About 0.1 bits per pixel per frame (0.2 for high quality): sharp text without a huge file.</summary>
    internal static uint Bitrate(int width, int height, int fps, bool high = false) =>
        (uint)Math.Clamp(width * (long)height * fps / (high ? 5 : 10), 1_000_000, 60_000_000);

    private void CaptureLoop()
    {
        FrameGrabber grabber;
        try
        {
            grabber = new FrameGrabber(_area);
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            _queue.CompleteAdding();
            return;
        }

        using (grabber)
        {
            try { _go.Wait(_stop.Token); } // frames start when the encoder is ready for them
            catch (OperationCanceledException) { _queue.CompleteAdding(); return; }
            var interval = TimeSpan.FromSeconds(1.0 / _fps);
            var next = TimeSpan.Zero;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var wait = next - _clock.Elapsed;
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                    if (_stop.IsCancellationRequested) break;
                    if (!_spare.TryTake(out var pixels)) pixels = new byte[grabber.FrameBytes];
                    if (!grabber.Grab(pixels))
                    {
                        _refused++;
                        _lastError = grabber.LastError;
                    }
                    else
                    {
                        _grabbed++;
                        _queue.Add((pixels, _clock.Elapsed), _stop.Token);
                    }
                    next += interval;
                    if (next < _clock.Elapsed - TimeSpan.FromSeconds(1)) next = _clock.Elapsed; // fell far behind: do not burst
                }
            }
            catch (OperationCanceledException) { /* stopped */ }
            finally { _queue.CompleteAdding(); }
        }
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        _requested++;
        // Waits for the next frame; once recording stops and the queue drains, a null sample ends the file.
        if (!_queue.TryTake(out var frame, Timeout.Infinite))
        {
            args.Request.Sample = null;
            return;
        }
        var sample = MediaStreamSample.CreateFromBuffer(frame.Pixels.AsBuffer(0, _area.Width * _area.Height * 4), frame.At);
        sample.Duration = TimeSpan.FromSeconds(1.0 / _fps);
        sample.Processed += (_, _) => _spare.Add(frame.Pixels); // the encoder is done with it: reuse the buffer
        args.Request.Sample = sample;
    }

    /// <summary>Stops capturing, lets the encoder finish what is queued, and closes the file.</summary>
    public async Task StopAsync()
    {
        _stop.Cancel();
        _go.Set();
        if (_capture is not null) await _capture.ConfigureAwait(false);
        if (_encode is not null)
        {
            try { await _encode.ConfigureAwait(false); }
            finally { Dispose(); }
        }
        else Dispose();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _go.Set();
        if (_source is not null) _source.SampleRequested -= OnSampleRequested;
        _output?.Dispose();
        _output = null;
    }
}
