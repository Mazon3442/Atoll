using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Atoll.Shell;

/// <summary>
/// Frosted-glass layer for the island. The island window itself is a layered (per-pixel alpha)
/// window, which Windows cannot blur behind, so this separate window sits directly under it,
/// blurs the desktop with the acrylic accent and is clipped to the pill shape with a window region.
/// It cannot be made mouse-transparent (WPF drops WS_EX_LAYERED on non-layered windows), so the
/// island hides it while the pointer passes over the compact pill, letting clicks reach the app behind.
/// </summary>
internal sealed class BackdropWindow : Window
{
    private nint _hwnd;
    private Color _tint;
    private (int X, int Y, int W, int H, string Shape) _last;

    public BackdropWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        Width = Height = 1;
        Left = Top = -32000;
        Title = "ATOLL backdrop";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (nint)ex);

        if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        ApplyAccent();
    }

    public void SetTint(Color tint)
    {
        if (tint == _tint) return;
        _tint = tint;
        ApplyAccent();
    }

    /// <summary>Places the blur under <paramref name="above"/> covering the given rounded rectangles (physical pixels).</summary>
    public void Follow(nint above, IReadOnlyList<(Int32Rect Rect, int Radius)> shapes)
    {
        if (_hwnd == 0 || shapes.Count == 0) return;
        var left = shapes.Min(s => s.Rect.X);
        var top = shapes.Min(s => s.Rect.Y);
        var right = shapes.Max(s => s.Rect.X + s.Rect.Width);
        var bottom = shapes.Max(s => s.Rect.Y + s.Rect.Height);
        var shapeKey = string.Join(";", shapes.Select(s => $"{s.Rect.X - left},{s.Rect.Y - top},{s.Rect.Width},{s.Rect.Height},{s.Radius}"));
        var key = (left, top, right - left, bottom - top, shapeKey);

        // Directly below the island in z-order, every time (other topmost windows can slip between).
        Native.SetWindowPos(_hwnd, above, left, top, right - left, bottom - top, Native.SWP_NOACTIVATE);
        if (key == _last) return;
        _last = key;

        nint region = 0;
        foreach (var (r, radius) in shapes)
        {
            var part = Native.CreateRoundRectRgn(r.X - left, r.Y - top, r.X - left + r.Width + 1, r.Y - top + r.Height + 1, radius * 2, radius * 2);
            if (region == 0)
            {
                region = part;
                continue;
            }
            Native.CombineRgn(region, region, part, Native.RGN_OR);
            Native.DeleteObject(part);
        }
        if (Native.SetWindowRgn(_hwnd, region, true) == 0) Native.DeleteObject(region);
    }

    private void ApplyAccent()
    {
        if (_hwnd == 0) return;
        var accent = new Native.AccentPolicy
        {
            AccentState = Native.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = (uint)(_tint.A << 24 | _tint.B << 16 | _tint.G << 8 | _tint.R),
        };
        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new Native.WindowCompositionAttributeData { Attribute = Native.WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            Native.SetWindowCompositionAttribute(_hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
