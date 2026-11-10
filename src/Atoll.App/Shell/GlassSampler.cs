using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Atoll.Shell;

/// <summary>
/// Fallback frosted glass for the island: captures the screen behind it at 1/<see cref="Downscale"/> size,
/// blurs and tints that tiny image, and the island paints it back scaled up (which smooths it further).
/// Used only when <see cref="SystemBackdrop"/> cannot apply the Windows blur to a per-pixel-alpha window.
/// The island window must be excluded from capture (<see cref="ExcludeFromCapture"/>) or it would sample itself.
/// </summary>
internal static class GlassSampler
{
    public const int Downscale = 8;

    internal static Int32Rect CaptureBounds(Rect bounds)
    {
        var x = (int)Math.Floor(bounds.Left); var y = (int)Math.Floor(bounds.Top);
        return new Int32Rect(x, y, Math.Max(1, (int)Math.Ceiling(bounds.Right) - x), Math.Max(1, (int)Math.Ceiling(bounds.Bottom) - y));
    }

    private const uint WDA_NONE = 0x0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(nint dest, int dx, int dy, int dw, int dh, nint src, int sx, int sy, int sw, int sh, uint rop);

    /// <summary>Hides the window from screen capture (screenshots, recordings, and our own sampling).</summary>
    public static bool ExcludeFromCapture(nint hwnd, bool exclude) =>
        SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);

    /// <summary>Returns a small blurred, tinted copy of the screen area (physical pixels), or null on failure.</summary>
    public static BitmapSource? Capture(Int32Rect area, Color tint)
    {
        var w = Math.Max(2, area.Width / Downscale);
        var h = Math.Max(2, area.Height / Downscale);
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            SetStretchBltMode(mem, HALFTONE); // averages pixels while shrinking: the first, cheap blur
            if (!StretchBlt(mem, 0, 0, w, h, screen, area.X, area.Y, area.Width, area.Height, SRCCOPY)) return null;
            SelectObject(mem, old);

            var source = Imaging.CreateBitmapSourceFromHBitmap(bmp, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels = new int[w * h];
            bgra.CopyPixels(pixels, w * 4, 0);

            BoxBlur(pixels, w, h);
            BoxBlur(pixels, w, h);
            Tint(pixels, tint);

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            result.Freeze();
            return result;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>3×3 box blur, in place.</summary>
    private static void BoxBlur(int[] p, int w, int h)
    {
        var copy = (int[])p.Clone();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var yy = y + dy;
                    if (yy < 0 || yy >= h) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = x + dx;
                        if (xx < 0 || xx >= w) continue;
                        var c = copy[yy * w + xx];
                        r += (c >> 16) & 0xFF;
                        g += (c >> 8) & 0xFF;
                        b += c & 0xFF;
                        n++;
                    }
                }
                p[y * w + x] = unchecked((int)0xFF000000) | (r / n << 16) | (g / n << 8) | (b / n);
            }
        }
    }

    /// <summary>Blends the theme tint over the blur (tint alpha = strength); result is opaque.</summary>
    private static void Tint(int[] p, Color tint)
    {
        var a = tint.A / 255.0;
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            var r = (int)(((c >> 16) & 0xFF) * (1 - a) + tint.R * a);
            var g = (int)(((c >> 8) & 0xFF) * (1 - a) + tint.G * a);
            var b = (int)((c & 0xFF) * (1 - a) + tint.B * a);
            p[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
    }
}
