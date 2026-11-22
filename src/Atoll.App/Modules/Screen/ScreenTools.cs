using Activity = Atoll.Activities.Activity;
using BitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Settings;
using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Atoll.Modules.Screen;

/// <summary>A button on a result card: what it says, its icon, and what it does.</summary>
public sealed class CardAction(string label, string glyph, Action run)
{
    public string Label { get; } = label;
    public string Glyph { get; } = glyph;
    public ICommand Command { get; } = new RelayCommand(run);
}

/// <summary>The pill card that reports a finished screen tool: a thumbnail or colour swatch, what happened, and buttons.</summary>
public sealed class ScreenCardViewModel(string title, string detail, string glyph, ImageSource? thumbnail = null, Brush? swatch = null, params CardAction[] actions)
{
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string Glyph { get; } = glyph;
    public ImageSource? Thumbnail { get; } = thumbnail;
    public Brush? Swatch { get; } = swatch;
    public IReadOnlyList<CardAction> Actions { get; } = actions;
    public bool HasThumbnail => Thumbnail is not null;
    public bool HasSwatch => Swatch is not null;
    public bool HasDetail => Detail.Length > 0;
    public bool HasActions => Actions.Count > 0;
}

/// <summary>Reads text and QR codes from a picture. Windows' own OCR runs on this PC; nothing is uploaded.</summary>
internal static class ScreenReaders
{
    private const double MinOcrHeight = 48;

    /// <summary>Text in the picture, recognised in every language Windows has an OCR pack for and merged line by line.</summary>
    public static async Task<string> ReadTextAsync(BitmapSource picture)
    {
        var enlarged = picture.PixelHeight < MinOcrHeight ? Scale(picture, MinOcrHeight / picture.PixelHeight) : picture;
        using var bitmap = ToSoftwareBitmap(enlarged);
        var readings = new List<IReadOnlyList<OcrLine>>();
        foreach (var language in OcrEngine.AvailableRecognizerLanguages)
        {
            var engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine is null) continue;
            var result = await engine.RecognizeAsync(bitmap);
            readings.Add(result.Lines.Select(line =>
            {
                var top = line.Words.Min(word => word.BoundingRect.Top);
                var bottom = line.Words.Max(word => word.BoundingRect.Bottom);
                return new OcrLine(line.Text, top, Math.Max(1, bottom - top));
            }).ToList());
        }
        return OcrMerge.Merge(readings);
    }

    /// <summary>The text of the first QR code found in the picture, or null.</summary>
    public static string? ReadQr(BitmapSource picture)
    {
        var bgra = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var bytes = new byte[width * height * 4];
        bgra.CopyPixels(bytes, width * 4, 0);
        var reader = new ZXing.BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = [ZXing.BarcodeFormat.QR_CODE] },
        };
        return reader.Decode(new ZXing.RGBLuminanceSource(bytes, width, height, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32))?.Text;
    }

    private static BitmapSource Scale(BitmapSource picture, double factor)
    {
        var scaled = new TransformedBitmap(picture, new ScaleTransform(factor, factor));
        scaled.Freeze();
        return scaled;
    }

    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource picture)
    {
        var bgra = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(bytes, bgra.PixelWidth * 4, 0);
        return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bgra.PixelWidth, bgra.PixelHeight, BitmapAlphaMode.Premultiplied);
    }
}

/// <summary>
/// Screenshots, text and QR codes from the screen, and a colour picker. Each is a hotkey and a palette action; the result
/// is copied and a card appears on the island. The island hides from the picture unless Settings says to keep it.
/// </summary>
internal sealed class ScreenTools : IDisposable
{
    private const string Key = "screen";

    private readonly ActivityHost _activities;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private bool _busy;

    public ScreenTools(ActivityHost activities, SettingsService settings, Dispatcher dispatcher, HotkeyService? hotkeys)
    {
        _activities = activities;
        _settings = settings;
        _dispatcher = dispatcher;
        if (hotkeys is null) return;
        hotkeys.AddAction(ShortcutCatalog.ScreenshotArea, () => _ = Run(ScreenshotAreaAsync));
        hotkeys.AddAction(ShortcutCatalog.ScreenshotScreen, () => _ = Run(ScreenshotScreenAsync));
        hotkeys.AddAction(ShortcutCatalog.Ocr, () => _ = Run(ReadTextAsync));
        hotkeys.AddAction(ShortcutCatalog.Qr, () => _ = Run(ScanQrAsync));
        hotkeys.AddAction(ShortcutCatalog.ColorPicker, () => _ = Run(PickColorAsync));
    }

    public Task ScreenshotArea() => Run(ScreenshotAreaAsync);
    public Task ScreenshotScreen() => Run(ScreenshotScreenAsync);
    public Task ReadText() => Run(ReadTextAsync);
    public Task ScanQr() => Run(ScanQrAsync);
    public Task PickColor() => Run(PickColorAsync);

    /// <summary>One screen tool at a time: a second hotkey while the overlay is open does nothing.</summary>
    private async Task Run(Func<Task> tool)
    {
        if (_busy) return;
        _busy = true;
        try { await tool(); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("A screen tool failed", ex);
            Card(new ScreenCardViewModel("That did not work", "Windows would not let ATOLL read the screen.", ""));
        }
        finally { _busy = false; }
    }

    /// <summary>A frozen picture of every screen, taken with the island hidden from it unless Settings keeps it.</summary>
    private async Task<(BitmapSource Picture, PixelRect Bounds)?> Freeze(PixelRect? area = null)
    {
        var bounds = area ?? ScreenCapture.VirtualScreen();
        using (_settings.Current.Screen.ShowIsland ? null : CapturePrivacy.Hold())
        {
            if (!_settings.Current.Screen.ShowIsland) await Task.Delay(90); // let Windows apply the change before the picture
            return ScreenCapture.Capture(bounds) is { } picture ? (picture, bounds) : null;
        }
    }

    private async Task<(BitmapSource Region, PixelRect Area)?> ChooseRegion(OverlayMode mode = OverlayMode.Region)
    {
        if (await Freeze() is not var (picture, bounds)) return null;
        if (ScreenOverlayWindow.Choose(picture, bounds, mode) is not { } chosen || chosen.Area.IsEmpty) return null;
        return (ScreenCapture.Crop(picture, chosen.Area), chosen.Area.Clamp(new PixelRect(0, 0, picture.PixelWidth, picture.PixelHeight)));
    }

    private async Task ScreenshotAreaAsync()
    {
        if (await ChooseRegion() is { } chosen) Deliver(chosen.Region);
    }

    private async Task ScreenshotScreenAsync()
    {
        if (await Freeze(ScreenCapture.MonitorUnderCursor()) is { } shot) Deliver(shot.Picture);
    }

    /// <summary>Saves the picture, copies it, and shows a card with Edit and Show in folder.</summary>
    internal void Deliver(BitmapSource picture)
    {
        var folder = ScreenFiles.Folder(_settings.Current.Screen.Folder);
        Directory.CreateDirectory(folder);
        var path = ScreenFiles.NextPath(folder, DateTime.Now, ".png", File.Exists);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using (var stream = File.Create(path)) encoder.Save(stream);
        CopyImage(picture);

        var thumbnail = Thumbnail(picture);
        Card(new ScreenCardViewModel("Screenshot ready", $"On the clipboard and in the folder · {picture.PixelWidth}×{picture.PixelHeight}", "", thumbnail, null,
            new CardAction("Edit", "", () => ScreenshotEditorWindow.Open(picture, path, this)),
            new CardAction("Show in folder", "", () => PaletteActions.ShowInFolder(path))));
    }

    private async Task ReadTextAsync()
    {
        if (await ChooseRegion() is not { } chosen) return;
        var text = (await ScreenReaders.ReadTextAsync(chosen.Region)).Trim();
        if (text.Length == 0)
        {
            Card(new ScreenCardViewModel("No text found", "Try a larger area, or one with clearer text.", "", Thumbnail(chosen.Region)));
            return;
        }
        CopyText(text);
        Card(new ScreenCardViewModel("Text copied", Preview(text), "", Thumbnail(chosen.Region)));
    }

    private async Task ScanQrAsync()
    {
        if (await ChooseRegion() is not { } chosen) return;
        var text = ScreenReaders.ReadQr(chosen.Region);
        if (text is null)
        {
            Card(new ScreenCardViewModel("No QR code found", "Choose an area that holds the whole code.", "", Thumbnail(chosen.Region)));
            return;
        }
        CopyText(text);
        var actions = QrPayload.Classify(text) switch
        {
            QrKind.Link => new[] { new CardAction("Open", "", () => PaletteActions.Open(text)) },
            QrKind.Email or QrKind.Phone => [new CardAction("Open", "", () => PaletteActions.Open(text))],
            _ => Array.Empty<CardAction>(),
        };
        var detail = QrPayload.Classify(text) == QrKind.Wifi ? $"Wi-Fi network {QrPayload.WifiName(text)}" : Preview(text);
        Card(new ScreenCardViewModel("QR code copied", detail, "", null, null, actions));
    }

    private async Task PickColorAsync()
    {
        if (await Freeze() is not var (picture, bounds)) return;
        if (ScreenOverlayWindow.Choose(picture, bounds, OverlayMode.Pick) is not { } picked) return;
        if (ScreenCapture.PixelAt(picture, picked.X, picked.Y) is not { } colour) return;
        var hex = ScreenOverlayWindow.Hex(colour);
        CopyText(hex);
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        Card(new ScreenCardViewModel(hex, $"copied · rgb({colour.R}, {colour.G}, {colour.B})", "", null, brush));
    }

    private void Card(ScreenCardViewModel card) =>
        _dispatcher.BeginInvoke(() => _activities.Post(new Activity($"{Key}.card", Key, card, ActivityPriority.High, TimeSpan.FromSeconds(8), alert: true)));

    internal static void CopyImage(BitmapSource picture)
    {
        try { System.Windows.Clipboard.SetImage(picture); }
        catch (COMException) { Log.Info("The clipboard was busy; the picture was not copied."); }
    }

    private static void CopyText(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (COMException) { Log.Info("The clipboard was busy; the text was not copied."); }
    }

    private static string Preview(string text)
    {
        var line = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 90 ? line : line[..89] + "…";
    }

    /// <summary>A small picture for the card: at most 120 pixels tall, so the card stays light.</summary>
    private static BitmapSource Thumbnail(BitmapSource picture)
    {
        var scale = Math.Min(1, 120.0 / picture.PixelHeight);
        var thumb = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
        thumb.Freeze();
        return thumb;
    }

    public void Dispose() => _activities.Remove($"{Key}.card");
}
