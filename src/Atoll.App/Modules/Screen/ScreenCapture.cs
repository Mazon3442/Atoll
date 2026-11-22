using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Atoll.Modules.Screen;

/// <summary>A rectangle in physical screen pixels, which is what Windows reports and what pictures are made of.</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public static PixelRect FromCorners(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>The part of this rectangle inside <paramref name="bounds"/>.</summary>
    public PixelRect Clamp(PixelRect bounds)
    {
        var left = Math.Max(X, bounds.X);
        var top = Math.Max(Y, bounds.Y);
        var right = Math.Min(Right, bounds.Right);
        var bottom = Math.Min(Bottom, bounds.Bottom);
        return right <= left || bottom <= top ? default : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>The same rectangle relative to <paramref name="origin"/>'s top-left corner.</summary>
    public PixelRect RelativeTo(PixelRect origin) => this with { X = X - origin.X, Y = Y - origin.Y };
}

/// <summary>Takes pictures of the screen with plain GDI, at full resolution and in physical pixels.</summary>
internal static class ScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    /// <summary>All monitors together, in pixels. The top-left can be negative when a monitor sits left of or above the main one.</summary>
    public static PixelRect VirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>The monitor under the cursor, in pixels.</summary>
    public static PixelRect MonitorUnderCursor()
    {
        GetCursorPos(out var point);
        var monitor = MonitorFromPoint(point, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top) : VirtualScreen();
    }

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    /// <summary>A frozen picture of <paramref name="area"/>. Null when Windows refuses (for example a locked desktop).</summary>
    public static BitmapSource? Capture(PixelRect area)
    {
        if (area.IsEmpty) return null;
        var screen = GetDC(0);
        if (screen == 0) return null;
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, area.Width, area.Height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, SRCCOPY | CAPTUREBLT)) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var opaque = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr32, null, 0); // GDI leaves alpha at 0
            opaque.Freeze();
            return opaque;
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>The part of <paramref name="picture"/> inside <paramref name="area"/> (pixels of the picture).</summary>
    public static BitmapSource Crop(BitmapSource picture, PixelRect area)
    {
        var cropped = new CroppedBitmap(picture, new Int32Rect(area.X, area.Y, area.Width, area.Height));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>The colour of one pixel of <paramref name="picture"/>, or null outside it.</summary>
    public static (byte R, byte G, byte B)? PixelAt(BitmapSource picture, int x, int y)
    {
        if (x < 0 || y < 0 || x >= picture.PixelWidth || y >= picture.PixelHeight) return null;
        var pixel = new byte[4];
        new CroppedBitmap(picture, new Int32Rect(x, y, 1, 1)).CopyPixels(pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]); // Bgr32
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);
}

/// <summary>
/// Grabs raw BGRA frames of one area, again and again, reusing one device context and one bitmap so a 30 fps
/// recording does not allocate a picture per frame. The mouse pointer is drawn into each frame.
/// </summary>
internal sealed class FrameGrabber : IDisposable
{
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, DI_NORMAL = 0x3;

    private readonly PixelRect _area;
    private readonly nint _screen;
    private readonly nint _memory;
    private readonly nint _bitmap;
    private readonly nint _previous;
    private readonly nint _bits;

    public FrameGrabber(PixelRect area)
    {
        // H.264 wants even dimensions.
        _area = area with { Width = area.Width & ~1, Height = area.Height & ~1 };
        if (_area.IsEmpty) throw new ArgumentException("The area is too small to record.", nameof(area));
        _screen = GetDC(0);
        _memory = CreateCompatibleDC(_screen);
        var info = new BITMAPINFO { Size = 40, Width = _area.Width, Height = -_area.Height, Planes = 1, BitCount = 32 }; // negative height: top-down
        _bitmap = CreateDIBSection(_memory, ref info, 0, out _bits, 0, 0);
        if (_bitmap == 0) { Dispose(); throw new InvalidOperationException("Windows could not allocate a frame."); }
        _previous = SelectObject(_memory, _bitmap);
    }

    public int Width => _area.Width;
    public int Height => _area.Height;
    public int FrameBytes => Width * Height * 4;

    /// <summary>The Windows error behind the last refused frame, for diagnosing a recording that stays empty.</summary>
    public int LastError { get; private set; }

    /// <summary>Copies the current screen contents into <paramref name="frame"/>.</summary>
    public bool Grab(byte[] frame)
    {
        if (!BitBlt(_memory, 0, 0, _area.Width, _area.Height, _screen, _area.X, _area.Y, SRCCOPY | CAPTUREBLT))
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }
        DrawCursor();
        Marshal.Copy(_bits, frame, 0, Math.Min(frame.Length, FrameBytes));
        return true;
    }

    private void DrawCursor()
    {
        var cursor = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0) return; // CURSOR_SHOWING
        DrawIconEx(_memory, cursor.X - _area.X - 0, cursor.Y - _area.Y - 0, cursor.Handle, 0, 0, 0, 0, DI_NORMAL);
    }

    public void Dispose()
    {
        if (_previous != 0) SelectObject(_memory, _previous);
        if (_bitmap != 0) DeleteObject(_bitmap);
        if (_memory != 0) DeleteDC(_memory);
        if (_screen != 0) ReleaseDC(0, _screen);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int Size, Flags;
        public nint Handle;
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BITMAPINFO info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);
}
