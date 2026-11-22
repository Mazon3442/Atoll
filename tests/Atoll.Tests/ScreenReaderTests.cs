using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Atoll.Modules.Screen;
using Atoll.Modules.TextTools;

namespace Atoll.Tests;

public class ScreenReaderTests
{
    [Fact]
    public void A_qr_code_made_by_the_text_tab_is_read_back_exactly()
    {
        var picture = QrImage.Make("https://example.com/a?b=1&c=é");
        Assert.Equal("https://example.com/a?b=1&c=é", ScreenReaders.ReadQr(picture));
    }

    [Fact]
    public void A_picture_without_a_qr_code_reads_as_nothing()
    {
        var blank = BitmapSource.Create(200, 200, 96, 96, PixelFormats.Bgr32, null, new byte[200 * 200 * 4], 200 * 4);
        Assert.Null(ScreenReaders.ReadQr(blank));
    }

    [Fact]
    public void The_screen_can_be_captured_and_a_pixel_read()
    {
        var area = new PixelRect(ScreenCapture.VirtualScreen().X, ScreenCapture.VirtualScreen().Y, 32, 32);
        var picture = ScreenCapture.Capture(area);
        if (picture is null) return; // a locked or service session has no desktop to read
        Assert.Equal((32, 32), (picture.PixelWidth, picture.PixelHeight));
        Assert.NotNull(ScreenCapture.PixelAt(picture, 5, 5));
        Assert.Null(ScreenCapture.PixelAt(picture, 32, 0));
        Assert.Equal(new PixelRect(4, 4, 8, 8), ScreenCapture.Crop(picture, new PixelRect(4, 4, 8, 8)) is { } c ? new PixelRect(4, 4, c.PixelWidth, c.PixelHeight) : default);
    }

    [Fact]
    public async Task Rendered_text_is_recognised_when_a_language_pack_is_installed()
    {
        if (!Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Any()) return;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 420, 90));
            context.DrawText(new FormattedText("Hello world", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 36, Brushes.Black, 1.0), new Point(14, 18));
        }
        var target = new RenderTargetBitmap(420, 90, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();

        var text = await ScreenReaders.ReadTextAsync(target);

        Assert.Contains("Hello", text, StringComparison.OrdinalIgnoreCase);
    }
}
