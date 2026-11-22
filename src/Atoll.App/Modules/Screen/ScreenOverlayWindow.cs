using System.Windows.Automation;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Atoll.Modules.Screen;

internal enum OverlayMode { Region, Pick }

/// <summary>What the person chose on the frozen screen: a rectangle (Region) or one pixel (Pick), in pixels of the snapshot.</summary>
internal readonly record struct OverlayResult(PixelRect Area, int X, int Y);

/// <summary>
/// A full-screen overlay over a frozen picture of the desktop, the way a snipping tool works. Drag to choose an area, or
/// click to pick a colour; Esc cancels. Working on the frozen picture means the choice is exact whatever moves behind it.
/// </summary>
internal sealed class ScreenOverlayWindow : Window
{
    private const int Zoom = 11, Cells = 11; // the magnifier shows Cells x Cells pixels, each Zoom DIPs wide

    private readonly BitmapSource _snapshot;
    private readonly PixelRect _bounds;
    private readonly OverlayMode _mode;
    private readonly Canvas _canvas = new();
    private readonly System.Windows.Shapes.Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = [4, 3], Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _label = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x10, 0x12)), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(8, 3, 8, 3), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 12 };
    private readonly Image _magnifier = new() { Width = Cells * Zoom, Height = Cells * Zoom, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _magnifierBox = new() { BorderBrush = Brushes.White, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private (double X, double Y)? _start;
    private OverlayResult? _result;

    private ScreenOverlayWindow(BitmapSource snapshot, PixelRect bounds, OverlayMode mode)
    {
        _snapshot = snapshot;
        _bounds = bounds;
        _mode = mode;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = mode == OverlayMode.Pick ? Cursors.Cross : Cursors.Cross;
        AutomationProperties.SetName(this, mode == OverlayMode.Pick ? "Pick a color. Click a pixel, or press Escape to cancel." : "Choose an area. Drag over the screen, or press Escape to cancel.");

        var picture = new Image { Source = snapshot, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        _canvas.Children.Add(picture);
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_frame);
        _label.Child = _labelText;
        _canvas.Children.Add(_label);
        _magnifierBox.Child = _magnifier;
        RenderOptions.SetBitmapScalingMode(_magnifier, BitmapScalingMode.NearestNeighbor);
        _canvas.Children.Add(_magnifierBox);
        Content = _canvas;

        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) => { Size(picture); Activate(); Focus(); Keyboard.Focus(this); Update(Mouse.GetPosition(_canvas)); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _result = null; Close(); }
            else if (e.Key == Key.Enter && _mode == OverlayMode.Region)
            {
                // Enter takes the whole screen the cursor is on.
                _result = new OverlayResult(ScreenCapture.MonitorUnderCursor().Clamp(_bounds).RelativeTo(_bounds), 0, 0);
                Close();
            }
        };
        MouseMove += (_, e) => Update(e.GetPosition(_canvas));
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
    }

    /// <summary>Freezes the screen, lets the person choose, and returns the choice (null if cancelled).</summary>
    public static OverlayResult? Choose(BitmapSource snapshot, PixelRect bounds, OverlayMode mode)
    {
        var window = new ScreenOverlayWindow(snapshot, bounds, mode);
        window.ShowDialog();
        return window._result;
    }

    // Window placement is done in pixels, so it covers every monitor whatever their scaling.
    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, new nint(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x0040); // HWND_TOPMOST, SWP_SHOWWINDOW
    }

    /// <summary>Sizes the layout so one device pixel of the window is one pixel of the snapshot.</summary>
    private void Size(Image picture)
    {
        var scale = Scale;
        picture.Width = _snapshot.PixelWidth / scale;
        picture.Height = _snapshot.PixelHeight / scale;
        _canvas.Width = picture.Width;
        _canvas.Height = picture.Height;
        _dim.Data = new RectangleGeometry(new Rect(0, 0, picture.Width, picture.Height));
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private (int X, int Y) ToPixels(Point point) =>
        ((int)Math.Clamp(Math.Round(point.X * Scale), 0, _snapshot.PixelWidth - 1), (int)Math.Clamp(Math.Round(point.Y * Scale), 0, _snapshot.PixelHeight - 1));

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode == OverlayMode.Pick) return;
        var at = e.GetPosition(_canvas);
        _start = (at.X, at.Y);
        CaptureMouse();
        Update(at);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition(_canvas);
        if (_mode == OverlayMode.Pick)
        {
            var (x, y) = ToPixels(at);
            _result = new OverlayResult(default, x, y);
            Close();
            return;
        }
        if (_start is not { } start) return;
        ReleaseMouseCapture();
        var (x1, y1) = ToPixels(new Point(start.X, start.Y));
        var (x2, y2) = ToPixels(at);
        var area = PixelRect.FromCorners(x1, y1, x2, y2);
        _start = null;
        if (area.Width < 4 || area.Height < 4) { Update(at); return; } // a stray click, not a drag
        _result = new OverlayResult(area, 0, 0);
        Close();
    }

    private void Update(Point at)
    {
        var width = _canvas.Width;
        var height = _canvas.Height;
        if (_mode == OverlayMode.Pick) { ShowMagnifier(at); return; }

        _label.Visibility = Visibility.Visible;
        if (_start is not { } start)
        {
            _frame.Visibility = Visibility.Collapsed;
            _dim.Data = new RectangleGeometry(new Rect(0, 0, width, height));
            _labelText.Text = "Drag to choose an area \u00B7 Enter for the whole screen";
            Canvas.SetLeft(_label, at.X + 16);
            Canvas.SetTop(_label, at.Y + 16);
            return;
        }

        var rect = new Rect(Math.Min(start.X, at.X), Math.Min(start.Y, at.Y), Math.Abs(at.X - start.X), Math.Abs(at.Y - start.Y));
        _frame.Visibility = Visibility.Visible;
        Canvas.SetLeft(_frame, rect.X);
        Canvas.SetTop(_frame, rect.Y);
        _frame.Width = rect.Width;
        _frame.Height = rect.Height;
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, width, height)), new RectangleGeometry(rect));
        _labelText.Text = $"{Math.Round(rect.Width * Scale)} × {Math.Round(rect.Height * Scale)}";
        Canvas.SetLeft(_label, Math.Min(rect.X, width - 90));
        Canvas.SetTop(_label, rect.Bottom + 8 > height - 28 ? Math.Max(0, rect.Y - 28) : rect.Bottom + 8);
    }

    private void ShowMagnifier(Point at)
    {
        var (x, y) = ToPixels(at);
        var half = Cells / 2;
        var left = Math.Clamp(x - half, 0, Math.Max(0, _snapshot.PixelWidth - Cells));
        var top = Math.Clamp(y - half, 0, Math.Max(0, _snapshot.PixelHeight - Cells));
        var piece = new CroppedBitmap(_snapshot, new Int32Rect(left, top, Math.Min(Cells, _snapshot.PixelWidth), Math.Min(Cells, _snapshot.PixelHeight)));
        _magnifier.Source = piece;
        _magnifier.Visibility = _magnifierBox.Visibility = Visibility.Visible;

        var colour = ScreenCapture.PixelAt(_snapshot, x, y) ?? (0, 0, 0);
        var hex = Hex(colour);
        _labelText.Text = $"{hex}   rgb({colour.R}, {colour.G}, {colour.B})";
        _label.Visibility = Visibility.Visible;

        var boxSize = Cells * Zoom + 4;
        var boxX = at.X + 24 + boxSize > _canvas.Width ? at.X - 24 - boxSize : at.X + 24;
        var boxY = at.Y + 24 + boxSize + 30 > _canvas.Height ? at.Y - 24 - boxSize - 30 : at.Y + 24;
        Canvas.SetLeft(_magnifierBox, boxX);
        Canvas.SetTop(_magnifierBox, boxY);
        Canvas.SetLeft(_label, boxX);
        Canvas.SetTop(_label, boxY + boxSize + 4);
        // A box around the centre pixel, so it is clear which one will be picked.
        _frame.Visibility = Visibility.Collapsed;
    }

    /// <summary>"#D97757" for a colour.</summary>
    public static string Hex((byte R, byte G, byte B) colour) => string.Create(CultureInfo.InvariantCulture, $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}");

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
