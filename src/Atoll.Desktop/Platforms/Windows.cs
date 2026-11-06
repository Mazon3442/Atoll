using Avalonia.Media;
using Avalonia.Media.Imaging;
using Windows.Storage.Streams;

namespace Atoll.Shell;

public static class ImageLoader
{
    public static async Task<IImage?> LoadAsync(IRandomAccessStreamReference? reference, int decodeWidth)
    {
        if (reference is null) return null;
        using var source = await reference.OpenReadAsync();
        using var stream = source.AsStreamForRead();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Position = 0;
        return Bitmap.DecodeToWidth(buffer, decodeWidth);
    }
}
