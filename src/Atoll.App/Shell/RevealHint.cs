using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Atoll.Shell;

/// <summary>
/// A thin line at the top of the screen that fills from the center while the pointer is held there to bring back a
/// hidden island. It is click-through and never activated, so it cannot get in the way of the app underneath.
/// </summary>
internal sealed class RevealHint : Window
{
    private const double LineHeight = 3;
    private readonly ScaleTransform _fill = new(0, 1);
    private nint _hwnd;

    public RevealHint()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Focusable = false;

        var fill = new Border { RenderTransform = _fill, RenderTransformOrigin = new Point(0.5, 0.5), CornerRadius = new CornerRadius(LineHeight / 2) };
        fill.SetResourceReference(Border.BackgroundProperty, "Island.Accent");
        // A dim track shows the whole hold area on light and dark backgrounds.
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), CornerRadius = new CornerRadius(LineHeight / 2), Child = fill };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
            ex = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT) & ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (nint)ex);
        };
    }

    /// <summary>Shows the line across a zone given in physical pixels, filled to <paramref name="progress"/> (0 to 1).</summary>
    public void Update(int left, int top, int width, double scale, double progress)
    {
        _fill.ScaleX = Math.Clamp(progress, 0, 1);
        if (!IsVisible) Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, width, (int)Math.Ceiling(LineHeight * scale), Native.SWP_NOACTIVATE);
    }

    public void Clear()
    {
        _fill.ScaleX = 0;
        if (IsVisible) Hide();
    }
}
