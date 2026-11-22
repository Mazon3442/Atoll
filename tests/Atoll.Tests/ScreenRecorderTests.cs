using Atoll.Modules.Screen;

namespace Atoll.Tests;

public class ScreenRecorderTests
{
    [Theory]
    [InlineData(1920, 1080, 30, 6_220_800)]
    [InlineData(320, 240, 30, 1_000_000)]   // never below a watchable minimum
    [InlineData(7680, 4320, 60, 60_000_000)] // and capped
    public void Bitrate_scales_with_the_picture_within_limits(int width, int height, int fps, uint expected) =>
        Assert.Equal(expected, ScreenRecorder.Bitrate(width, height, fps));

    [Fact]
    public async Task A_short_recording_becomes_a_real_mp4_file()
    {
        var area = ScreenCapture.VirtualScreen();
        if (ScreenCapture.Capture(new PixelRect(area.X, area.Y, 16, 16)) is null) return; // no desktop to record
        var path = Path.Combine(Path.GetTempPath(), "atoll-rec-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var recorder = new ScreenRecorder(new PixelRect(area.X, area.Y, 321, 241), path, 30, Windows.Media.MediaProperties.VideoEncodingQuality.Vga);
            await recorder.StartAsync();
            await Task.Delay(1500);
            Assert.True(recorder.Elapsed > TimeSpan.FromSeconds(1));
            try { await recorder.StopAsync(); }
            catch (Exception ex) { throw new Exception($"grabbed/refused/requested = {recorder.Counts}", ex); }

            var info = new FileInfo(path);
            Assert.True(info.Exists && info.Length > 2000, $"the file is {(info.Exists ? info.Length : -1)} bytes");
            var head = new byte[12];
            using (var stream = File.OpenRead(path)) stream.ReadExactly(head);
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(head, 4, 4)); // an MP4 container
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Frames_are_even_sized_and_hold_the_whole_area()
    {
        var origin = ScreenCapture.VirtualScreen();
        if (ScreenCapture.Capture(new PixelRect(origin.X, origin.Y, 8, 8)) is null) return;
        using var grabber = new FrameGrabber(new PixelRect(origin.X, origin.Y, 101, 51));
        Assert.Equal((100, 50), (grabber.Width, grabber.Height));
        var frame = new byte[grabber.FrameBytes];
        Assert.True(grabber.Grab(frame));
        Assert.Equal(100 * 50 * 4, frame.Length);
    }
}
