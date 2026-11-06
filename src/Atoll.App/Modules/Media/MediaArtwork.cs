using System.Runtime.InteropServices;
#if AVALONIA
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageSource = Avalonia.Media.IImage;
#else
using System.Windows.Media;
using System.Windows.Media.Imaging;
#endif

namespace Atoll.Modules.Media;

internal static class MediaArtwork
{
    public static string? Color(ImageSource? image)
    {
#if AVALONIA
        if (image is not Bitmap bitmap || bitmap.Format is not { } format || format != PixelFormat.Bgra8888 && format != PixelFormat.Rgba8888) return null;
        var width = bitmap.PixelSize.Width; var height = bitmap.PixelSize.Height;
        if ((long)width * height > 16_000_000) return null;
        var stride = checked(width * 4); var pixels = new byte[checked(stride * height)];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, stride); }
        finally { handle.Free(); }
        return ArtworkPalette.FromPixels(pixels, width, height, stride, format == PixelFormat.Rgba8888, bitmap.AlphaFormat == AlphaFormat.Premul);
#else
        if (image is not BitmapSource source || (long)source.PixelWidth * source.PixelHeight > 16_000_000) return null;
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4); var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return ArtworkPalette.FromPixels(pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride);
#endif
    }
}
