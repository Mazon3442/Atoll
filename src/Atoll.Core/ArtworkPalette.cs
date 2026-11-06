using System.Globalization;

namespace Atoll;

public static class ArtworkPalette
{
    public static string? FromPixels(ReadOnlySpan<byte> pixels, int width, int height, int stride, bool rgba = false, bool premultiplied = false)
    {
        if (width <= 0 || height <= 0 || stride < (long)width * 4 || pixels.Length < (long)stride * height) return null;
        var counts = new int[4096]; var red = new int[4096]; var green = new int[4096]; var blue = new int[4096];
        var step = Math.Max(1, (long)width * height / 4096);
        for (long i = 0; i < (long)width * height; i += step)
        {
            var offset = (int)(i / width * stride + i % width * 4);
            var alpha = pixels[offset + 3]; if (alpha < 160) continue;
            var r = (int)pixels[offset + (rgba ? 0 : 2)]; var g = (int)pixels[offset + 1]; var b = (int)pixels[offset + (rgba ? 2 : 0)];
            if (premultiplied && alpha < 255) { r = Math.Min(255, r * 255 / alpha); g = Math.Min(255, g * 255 / alpha); b = Math.Min(255, b * 255 / alpha); }
            var bin = (r >> 4) * 256 + (g >> 4) * 16 + (b >> 4);
            counts[bin]++; red[bin] += r; green[bin] += g; blue[bin] += b;
        }
        var best = -1; var bestScore = 0.0;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            var r = red[i] / counts[i]; var g = green[i] / counts[i]; var b = blue[i] / counts[i];
            var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
            var saturation = max == 0 ? 0 : (max - min) / (double)max;
            var score = counts[i] * (.4 + saturation) * (max < 24 || min > 232 ? .25 : 1);
            if (score > bestScore) { best = i; bestScore = score; }
        }
        return best < 0 ? null : $"#{red[best] / counts[best]:X2}{green[best] / counts[best]:X2}{blue[best] / counts[best]:X2}";
    }

    public static string ForBackground(string? artwork, string background, string fallback)
    {
        var color = Parse(artwork) ?? Parse(fallback) ?? (10, 132, 255);
        var backdrop = Parse(background) ?? (0, 0, 0);
        var target = Luminance(backdrop) < .18 ? (255, 255, 255) : (0, 0, 0);
        for (var i = 0; i <= 20; i++)
        {
            var fraction = i / 20.0;
            var candidate = ((int)Math.Round(color.Item1 + (target.Item1 - color.Item1) * fraction),
                (int)Math.Round(color.Item2 + (target.Item2 - color.Item2) * fraction), (int)Math.Round(color.Item3 + (target.Item3 - color.Item3) * fraction));
            if (Contrast(candidate, backdrop) >= 3 || i == 20) return $"#{candidate.Item1:X2}{candidate.Item2:X2}{candidate.Item3:X2}";
        }
        return fallback;
    }
    private static (int, int, int)? Parse(string? value) => value is { Length: 7 or 9 } && value[0] == '#'
        && int.TryParse(value.AsSpan(value.Length - 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
        ? ((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255) : null;
    private static double Luminance((int R, int G, int B) rgb)
    {
        static double Linear(int value) { var c = value / 255.0; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        return .2126 * Linear(rgb.R) + .7152 * Linear(rgb.G) + .0722 * Linear(rgb.B);
    }
    private static double Contrast((int, int, int) a, (int, int, int) b) =>
        (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);
}
