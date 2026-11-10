using System.Runtime.InteropServices;
using System.Windows;

namespace Atoll.Shell;

/// <summary>
/// Real Windows blur behind the island's rounded shapes. The blur region follows the pill and the visible
/// side bubbles, so the shape and click-through stay exactly as they are. Used when the system accepts it;
/// otherwise the island falls back to <see cref="GlassSampler"/>.
/// </summary>
internal sealed class SystemBackdrop : IDisposable
{
    private const int DWM_BB_ENABLE = 0x1;
    private const int DWM_BB_BLURREGION = 0x2;
    private const int RGN_OR = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        public int Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public nint Region;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }

    internal readonly record struct Shape(Rect Bounds, double Radius, bool Notch);

    /// <summary>
    /// Whether DWM will actually draw the blur. Windows 11 still answers success and then draws nothing,
    /// which is why the island looked like a flat tint; those builds use <see cref="GlassSampler"/> instead.
    /// </summary>
    public static bool Supported { get; } = Environment.OSVersion.Version.Build < 22000;

    private nint _region;

    /// <summary>Applies the blur to the rounded shapes. Returns false when Windows refuses it.</summary>
    public bool Enable(nint hwnd, IReadOnlyList<Shape> shapes)
    {
        if (!Supported || hwnd == 0 || shapes.Count == 0) return false;
        var region = BuildRegion(shapes);
        if (region == 0) return false;
        var data = new BlurBehind
        {
            Flags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
            Enable = true,
            Region = region,
        };
        var result = DwmEnableBlurBehindWindow(hwnd, ref data);
        if (result < 0)
        {
            DeleteObject(region);
            return false;
        }
        if (_region != 0) DeleteObject(_region);
        _region = region;
        return true;
    }

    public void Disable(nint hwnd)
    {
        if (_region != 0)
        {
            DeleteObject(_region);
            _region = 0;
        }
        if (hwnd == 0) return;
        var data = new BlurBehind { Flags = DWM_BB_ENABLE, Enable = false };
        _ = DwmEnableBlurBehindWindow(hwnd, ref data);
    }

    public void Dispose() => Disable(0);

    private static nint BuildRegion(IReadOnlyList<Shape> shapes)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return 0;
        foreach (var shape in shapes)
        {
            var left = (int)Math.Floor(shape.Bounds.Left);
            var top = (int)Math.Floor(shape.Bounds.Top);
            var right = (int)Math.Ceiling(shape.Bounds.Right);
            var bottom = (int)Math.Ceiling(shape.Bounds.Bottom);
            if (right <= left || bottom <= top) continue;
            var diameter = Math.Max(1, (int)Math.Round(shape.Radius * 2));
            var part = CreateRoundRectRgn(left, top, right + 1, bottom + 1, diameter, diameter);
            if (part == 0) continue;
            if (shape.Notch)
            {
                // A notch is flush with the screen edge: square off the top corners only.
                var squareTop = CreateRectRgn(left, top, right + 1, top + Math.Max(1, diameter / 2));
                if (squareTop != 0)
                {
                    CombineRgn(part, part, squareTop, RGN_OR);
                    DeleteObject(squareTop);
                }
            }
            CombineRgn(region, region, part, RGN_OR);
            DeleteObject(part);
        }
        return region;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind data);

    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
}
