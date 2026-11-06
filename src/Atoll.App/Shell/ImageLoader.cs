using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace Atoll.Shell;

public static class ImageLoader
{
    /// <summary>Loads a WinRT stream reference (album art, app logo) into a frozen bitmap usable on any thread.</summary>
    public static async Task<ImageSource?> LoadAsync(IRandomAccessStreamReference? reference, int decodeWidth)
    {
        if (reference is null) return null;
        using var winrtStream = await reference.OpenReadAsync();
        using var stream = winrtStream.AsStreamForRead();
        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.StreamSource = buffer;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
