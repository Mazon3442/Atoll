using Atoll.Modules.Screen;

namespace Atoll.Tests;

public class ScreenLogicTests
{
    [Fact]
    public void A_selection_is_built_from_any_two_corners_and_clamped_to_the_screen()
    {
        var rect = PixelRect.FromCorners(300, 200, 100, 50);
        Assert.Equal(new PixelRect(100, 50, 200, 150), rect);

        var screen = new PixelRect(0, 0, 1920, 1080);
        Assert.Equal(new PixelRect(1800, 1000, 120, 80), new PixelRect(1800, 1000, 500, 500).Clamp(screen));
        Assert.True(new PixelRect(2000, 0, 10, 10).Clamp(screen).IsEmpty);
        Assert.Equal(new PixelRect(10, 20, 30, 40), new PixelRect(110, 220, 30, 40).RelativeTo(new PixelRect(100, 200, 500, 500)));
    }

    [Fact]
    public void A_monitor_left_of_the_main_one_gives_negative_pixel_coordinates()
    {
        var virtualScreen = new PixelRect(-1920, 0, 3840, 1080);
        var onLeft = new PixelRect(-1800, 100, 200, 100);
        Assert.Equal(new PixelRect(120, 100, 200, 100), onLeft.RelativeTo(virtualScreen));
    }

    [Fact]
    public void Ocr_readings_merge_line_by_line_keeping_the_one_that_looks_like_words()
    {
        var english = new List<OcrLine> { new("Hello world", 10, 20), new("\u041F\u0440\u0438\u0432\u0435\u0442 \u00A7\u00A7\u00A7", 40, 20) };
        var russian = new List<OcrLine> { new("Hcllo wor1d", 11, 20), new("\u041F\u0440\u0438\u0432\u0435\u0442 \u043C\u0438\u0440", 41, 20) };

        var merged = OcrMerge.Merge([english, russian]);

        Assert.Equal("Hello world" + Environment.NewLine + "\u041F\u0440\u0438\u0432\u0435\u0442 \u043C\u0438\u0440", merged);
    }

    [Fact]
    public void Ocr_lines_keep_page_order_and_empty_readings_vanish()
    {
        var reading = new List<OcrLine> { new("second", 60, 20), new("first", 10, 20), new("  ", 35, 20) };
        Assert.Equal("first" + Environment.NewLine + "second", OcrMerge.Merge([reading]));
        Assert.Equal("", OcrMerge.Merge([]));
    }

    [Theory]
    [InlineData("https://example.com/a?b=1", QrKind.Link)]
    [InlineData("mailto:me@example.com", QrKind.Email)]
    [InlineData("tel:+123456", QrKind.Phone)]
    [InlineData("WIFI:T:WPA;S:Home;P:secret;;", QrKind.Wifi)]
    [InlineData("just some words", QrKind.Text)]
    [InlineData("ftp://example.com", QrKind.Text)]
    public void Scanned_codes_are_classified(string text, QrKind kind) => Assert.Equal(kind, QrPayload.Classify(text));

    [Fact]
    public void A_wifi_code_names_its_network() =>
        Assert.Equal("Home; 5G", QrPayload.WifiName(@"WIFI:T:WPA;S:Home\; 5G;P:secret;;"));

    [Fact]
    public void Saved_files_get_a_dated_name_and_never_overwrite()
    {
        var now = new DateTime(2026, 11, 23, 10, 0, 5);
        var taken = new HashSet<string>();
        var first = ScreenFiles.NextPath(@"C:\Pics", now, ".png", taken.Contains);
        Assert.Equal(@"C:\Pics\Atoll 2026-11-23 10.00.05.png", first);
        taken.Add(first);
        Assert.Equal(@"C:\Pics\Atoll 2026-11-23 10.00.05 (2).png", ScreenFiles.NextPath(@"C:\Pics", now, ".png", taken.Contains));
    }

    [Fact]
    public void The_save_folder_defaults_to_pictures_atoll()
    {
        Assert.EndsWith(Path.Combine("Pictures", "Atoll"), ScreenFiles.Folder(""));
        Assert.Equal(@"D:\Shots", ScreenFiles.Folder(@" D:\Shots "));
    }
}
