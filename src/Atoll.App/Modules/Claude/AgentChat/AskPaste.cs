using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Atoll.Modules.Claude;

/// <summary>
/// Lets the Ask box take pictures from the clipboard: a screenshot, a copied image, or image files copied in Explorer.
/// A picture alone is not pasted as text; text that arrives with it still is.
/// </summary>
public static class AskPaste
{
    private const int LongestSide = 1568; // larger pictures are shrunk: models downscale them anyway
    private const int MaxBytes = 5 * 1024 * 1024;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AskPaste), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject box, DependencyPropertyChangedEventArgs e)
    {
        if (box is not TextBox text) return;
        if ((bool)e.NewValue) DataObject.AddPastingHandler(text, OnPaste);
        else DataObject.RemovePastingHandler(text, OnPaste);
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox { DataContext: ClaudeViewModel view }) return;
        var images = Read(e.DataObject);
        if (images.Count == 0) return;
        foreach (var image in images)
            if (!view.Ask.AddImage(image)) break;
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) e.CancelCommand();
    }

    private static List<AgentChatImage> Read(IDataObject data)
    {
        var found = new List<AgentChatImage>();
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var file in files.Where(IsPicture))
                    if (Load(file) is { } fromFile && Normalize(fromFile) is { } image) found.Add(image);
            }
            else if (data.GetDataPresent(DataFormats.Bitmap))
            {
                var bitmap = data.GetData(DataFormats.Bitmap) as BitmapSource ?? System.Windows.Clipboard.GetImage();
                if (bitmap is not null && Normalize(bitmap) is { } image) found.Add(image);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or NotSupportedException
            or UnauthorizedAccessException or InvalidOperationException or FileFormatException)
        {
            Log.Info($"A pasted picture could not be read ({ex.GetType().Name}).");
        }
        return found;
    }

    private static bool IsPicture(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";

    private static BitmapSource? Load(string path)
    {
        if (new FileInfo(path).Length > 25 * 1024 * 1024) return null;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Shrinks to <see cref="LongestSide"/> and encodes as PNG; null when it is still too large to send.</summary>
    internal static AgentChatImage? Normalize(BitmapSource source)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > LongestSide)
        {
            var scale = LongestSide / (double)longest;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.Length > MaxBytes ? null : new AgentChatImage(stream.ToArray(), "image/png");
    }
}

/// <summary>Turns a picture's bytes into a small image for the strip above the Ask box.</summary>
public sealed class AskImageThumbnail : IValueConverter
{
    public static AskImageThumbnail Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes) return null;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 96;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
