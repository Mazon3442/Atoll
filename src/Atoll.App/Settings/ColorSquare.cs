using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Atoll.Settings;

public sealed class ColorSquare : FrameworkElement
{
    public double Hue { get; set; }
    public double Saturation { get; set; }
    public double Brightness { get; set; } = 1;
    public event EventHandler? ColorChanged;

    public ColorSquare() { Focusable = true; Cursor = Cursors.Cross; }

    protected override void OnRender(DrawingContext dc)
    {
        var area = new Rect(RenderSize);
        dc.DrawRoundedRectangle(new SolidColorBrush(FromHsv(Hue, 1, 1)), null, area, 6, 6);
        dc.PushClip(new RectangleGeometry(area, 6, 6));
        dc.DrawRectangle(new LinearGradientBrush(Colors.White, Colors.Transparent, 0), null, area);
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90), null, area);
        var point = new Point(Math.Clamp(Saturation * ActualWidth, 5, Math.Max(5, ActualWidth - 5)),
            Math.Clamp((1 - Brightness) * ActualHeight, 5, Math.Max(5, ActualHeight - 5)));
        dc.DrawEllipse(null, new Pen(Brushes.Black, 4), point, 5, 5);
        dc.DrawEllipse(null, new Pen(Brushes.White, 2), point, 5, 5);
        dc.Pop();
        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen(SystemColors.HighlightBrush, 2), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)), 6, 6);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus(); CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(this));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) { Pick(e.GetPosition(this)); ReleaseMouseCapture(); e.Handled = true; }
    }
    private void Pick(Point point)
    {
        Saturation = Math.Clamp(point.X / Math.Max(1, ActualWidth), 0, 1);
        Brightness = 1 - Math.Clamp(point.Y / Math.Max(1, ActualHeight), 0, 1);
        InvalidateVisual(); ColorChanged?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01;
        switch (e.Key)
        {
            case Key.Left: Saturation = Math.Max(0, Saturation - step); break;
            case Key.Right: Saturation = Math.Min(1, Saturation + step); break;
            case Key.Up: Brightness = Math.Min(1, Brightness + step); break;
            case Key.Down: Brightness = Math.Max(0, Brightness - step); break;
            default: return;
        }
        InvalidateVisual(); ColorChanged?.Invoke(this, EventArgs.Empty); e.Handled = true;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    internal static Color FromHsv(double hue, double saturation, double brightness, byte alpha = 255)
    {
        var chroma = brightness * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = brightness - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x),
        };
        return Color.FromArgb(alpha, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    internal static (double Hue, double Saturation, double Brightness) ToHsv(Color color)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6)
            : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return ((hue + 360) % 360, max == 0 ? 0 : delta / max, max);
    }
}
