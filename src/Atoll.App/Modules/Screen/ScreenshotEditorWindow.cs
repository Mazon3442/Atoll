using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Atoll.Settings;

namespace Atoll.Modules.Screen;

/// <summary>
/// Markup for a screenshot: arrows, boxes, a highlighter, text, and blur for private details. Everything is drawn on a
/// layer over the picture, so Undo removes it cleanly, and Save or Copy flatten the result at the picture's own size.
/// </summary>
internal sealed class ScreenshotEditorWindow : Window
{
    private enum Tool { Arrow, Box, Marker, Text, Blur }

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xFF, 0x45, 0x3A), Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0x0A, 0x84, 0xFF), Colors.White, Colors.Black,
    ];

    private readonly BitmapSource _picture;
    private readonly string _sourcePath;
    private readonly ScreenTools _tools;
    private readonly Grid _stage;
    private readonly Canvas _layer = new() { Background = Brushes.Transparent };
    private readonly List<UIElement> _drawn = [];
    private readonly TextBlock _status = new() { Foreground = Brushes.White, Opacity = 0.7, FontSize = 12, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private Tool _tool = Tool.Arrow;
    private Color _colour = Palette[0];
    private Point _start;
    private Shape? _live;
    private Polyline? _stroke;

    private ScreenshotEditorWindow(BitmapSource picture, string sourcePath, ScreenTools tools)
    {
        _picture = picture;
        _sourcePath = sourcePath;
        _tools = tools;
        Title = "Edit screenshot";
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D));
        Width = Math.Clamp(picture.PixelWidth + 40, 640, 1280);
        Height = Math.Clamp(picture.PixelHeight + 120, 420, 860);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var image = new Image { Source = picture, Width = picture.PixelWidth, Height = picture.PixelHeight, Stretch = Stretch.Fill };
        _layer.Width = picture.PixelWidth;
        _layer.Height = picture.PixelHeight;
        _stage = new Grid { Width = picture.PixelWidth, Height = picture.PixelHeight, Children = { image, _layer } };
        _layer.MouseLeftButtonDown += OnDown;
        _layer.MouseMove += OnMove;
        _layer.MouseLeftButtonUp += OnUp;

        var root = new DockPanel { Margin = new Thickness(12) };
        var bar = Toolbar();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16)), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 10, 0, 0),
            Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = _stage, Margin = new Thickness(8) },
        });
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) Undo();
            else if (e.Key == Key.Escape) Close();
        };
    }

    public static void Open(BitmapSource picture, string sourcePath, ScreenTools tools) => new ScreenshotEditorWindow(picture, sourcePath, tools).Show();

    private FrameworkElement Toolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (tool, label) in new[] { (Tool.Arrow, "Arrow"), (Tool.Box, "Box"), (Tool.Marker, "Marker"), (Tool.Text, "Text"), (Tool.Blur, "Blur") })
        {
            var captured = tool;
            var button = new RadioButton { Content = label, GroupName = "tool", IsChecked = tool == _tool, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 5, 12, 5), Style = null };
            ChipStyle(button);
            button.Checked += (_, _) => { _tool = captured; _status.Text = Hint(captured); };
            AutomationProperties.SetName(button, label);
            bar.Children.Add(button);
        }
        bar.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 4, 12, 4), Background = Brushes.White, Opacity = 0.15 });
        foreach (var colour in Palette)
        {
            var swatch = new RadioButton { GroupName = "colour", IsChecked = colour == _colour, Width = 24, Height = 24, Margin = new Thickness(0, 0, 6, 0), Style = null, Cursor = Cursors.Hand };
            swatch.Template = SwatchTemplate(colour);
            var captured = colour;
            swatch.Checked += (_, _) => _colour = captured;
            AutomationProperties.SetName(swatch, $"Color {colour}");
            bar.Children.Add(swatch);
        }
        bar.Children.Add(Action("Undo", Undo));
        bar.Children.Add(Action("Copy", () => { ScreenTools.CopyImage(Flatten()); _status.Text = "Copied."; }));
        bar.Children.Add(Action("Save", Save));
        bar.Children.Add(_status);
        return bar;
    }

    private static string Hint(Tool tool) => tool switch
    {
        Tool.Arrow => "Drag to draw an arrow.",
        Tool.Box => "Drag to draw a box.",
        Tool.Marker => "Drag over text to highlight it.",
        Tool.Text => "Click where the text goes, type, press Enter.",
        _ => "Drag over private details to blur them.",
    };

    private static Button Action(string label, System.Action run)
    {
        var button = new Button { Content = label, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5), Style = null, Cursor = Cursors.Hand };
        ChipStyle(button);
        button.Click += (_, _) => run();
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static void ChipStyle(Control control)
    {
        control.Foreground = Brushes.White;
        control.Cursor = Cursors.Hand;
        var template = new ControlTemplate(control is RadioButton ? typeof(RadioButton) : typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(999));
        border.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 5));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent, Path = new PropertyPath("(Tag)") });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        control.Template = template;
        control.Tag = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        if (control is RadioButton radio)
        {
            radio.Checked += (_, _) => { radio.Tag = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)); radio.Foreground = Brushes.Black; };
            radio.Unchecked += (_, _) => { radio.Tag = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)); radio.Foreground = Brushes.White; };
            if (radio.IsChecked == true) { radio.Tag = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)); radio.Foreground = Brushes.Black; }
        }
    }

    private static ControlTemplate SwatchTemplate(Color colour)
    {
        var template = new ControlTemplate(typeof(RadioButton));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var fill = new FrameworkElementFactory(typeof(Ellipse));
        fill.SetValue(Shape.FillProperty, new SolidColorBrush(colour));
        fill.SetValue(Shape.StrokeProperty, new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
        fill.SetValue(Shape.StrokeThicknessProperty, 1.0);
        var ring = new FrameworkElementFactory(typeof(Ellipse)) { Name = "ring" };
        ring.SetValue(Shape.StrokeProperty, Brushes.White);
        ring.SetValue(Shape.StrokeThicknessProperty, 2.0);
        ring.SetValue(MarginProperty, new Thickness(-3));
        ring.SetValue(VisibilityProperty, Visibility.Collapsed);
        grid.AppendChild(fill);
        grid.AppendChild(ring);
        template.VisualTree = grid;
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "ring"));
        template.Triggers.Add(on);
        return template;
    }

    // ---------- drawing ----------

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_layer);
        if (_tool == Tool.Text) { PlaceText(_start); e.Handled = true; return; }
        _layer.CaptureMouse();
        var brush = new SolidColorBrush(_colour);
        switch (_tool)
        {
            case Tool.Box:
            case Tool.Blur:
                _live = new Rectangle { Stroke = _tool == Tool.Blur ? Brushes.White : brush, StrokeThickness = _tool == Tool.Blur ? 1 : 4, StrokeDashArray = _tool == Tool.Blur ? [4, 3] : null, RadiusX = 3, RadiusY = 3 };
                break;
            case Tool.Arrow:
                _live = new System.Windows.Shapes.Path { Stroke = brush, Fill = brush, StrokeThickness = 5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                break;
            case Tool.Marker:
                _stroke = new Polyline { Stroke = new SolidColorBrush(Color.FromArgb(0x70, _colour.R, _colour.G, _colour.B)), StrokeThickness = 22, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _stroke.Points.Add(_start);
                _layer.Children.Add(_stroke);
                return;
        }
        if (_live is not null) _layer.Children.Add(_live);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_layer.IsMouseCaptured) return;
        var at = e.GetPosition(_layer);
        if (_stroke is not null) { _stroke.Points.Add(at); return; }
        switch (_live)
        {
            case Rectangle rect:
                Canvas.SetLeft(rect, Math.Min(_start.X, at.X));
                Canvas.SetTop(rect, Math.Min(_start.Y, at.Y));
                rect.Width = Math.Abs(at.X - _start.X);
                rect.Height = Math.Abs(at.Y - _start.Y);
                break;
            case System.Windows.Shapes.Path arrow:
                arrow.Data = ArrowGeometry(_start, at);
                break;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_layer.IsMouseCaptured) return;
        _layer.ReleaseMouseCapture();
        if (_stroke is not null)
        {
            _drawn.Add(_stroke);
            _stroke = null;
            return;
        }
        if (_live is null) return;
        var live = _live;
        _live = null;
        if (live is Rectangle { Width: < 4 } or Rectangle { Height: < 4 }) { _layer.Children.Remove(live); return; }
        if (live is System.Windows.Shapes.Path { Data: null }) { _layer.Children.Remove(live); return; }
        if (_tool == Tool.Blur && live is Rectangle area)
        {
            _layer.Children.Remove(live);
            var pixelated = Pixelate(new Rect(Canvas.GetLeft(area), Canvas.GetTop(area), area.Width, area.Height));
            if (pixelated is not null) { _layer.Children.Add(pixelated); _drawn.Add(pixelated); }
            return;
        }
        _drawn.Add(live);
    }

    /// <summary>A shaft with a filled head, so one shape is one undo step.</summary>
    private static Geometry ArrowGeometry(Point from, Point to)
    {
        var vector = to - from;
        if (vector.Length < 6) return Geometry.Empty;
        vector.Normalize();
        var normal = new Vector(-vector.Y, vector.X);
        var head = 22.0;
        var baseCentre = to - vector * head;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(from, false, false);
            context.LineTo(baseCentre, true, false);
            context.BeginFigure(to, true, true);
            context.LineTo(baseCentre + normal * 11, true, false);
            context.LineTo(baseCentre - normal * 11, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The chosen area redrawn as coarse blocks, which hides what was there instead of just softening it.</summary>
    private Image? Pixelate(Rect area)
    {
        var bounds = new Int32Rect((int)Math.Max(0, area.X), (int)Math.Max(0, area.Y),
            (int)Math.Min(area.Width, _picture.PixelWidth - Math.Max(0, area.X)), (int)Math.Min(area.Height, _picture.PixelHeight - Math.Max(0, area.Y)));
        if (bounds.Width < 2 || bounds.Height < 2) return null;
        var piece = new CroppedBitmap(_picture, bounds);
        var factor = 1.0 / Math.Max(6, Math.Min(bounds.Width, bounds.Height) / 8.0);
        var small = new TransformedBitmap(piece, new ScaleTransform(factor, factor));
        small.Freeze();
        var image = new Image { Source = small, Width = bounds.Width, Height = bounds.Height, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, bounds.X);
        Canvas.SetTop(image, bounds.Y);
        return image;
    }

    private void PlaceText(Point at)
    {
        var box = new TextBox
        {
            MinWidth = 120, FontSize = 30, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(_colour), CaretBrush = new SolidColorBrush(_colour),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
        };
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y);
        _layer.Children.Add(box);
        void Commit()
        {
            if (!_layer.Children.Contains(box)) return;
            _layer.Children.Remove(box);
            if (box.Text.Trim().Length == 0) return;
            var label = new TextBlock { Text = box.Text, FontSize = 30, FontWeight = FontWeights.Bold, Foreground = box.Foreground };
            Canvas.SetLeft(label, at.X);
            Canvas.SetTop(label, at.Y);
            _layer.Children.Add(label);
            _drawn.Add(label);
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { _layer.Children.Remove(box); e.Handled = true; } };
        box.Loaded += (_, _) => box.Focus();
    }

    private void Undo()
    {
        if (_drawn.Count == 0) return;
        _layer.Children.Remove(_drawn[^1]);
        _drawn.RemoveAt(_drawn.Count - 1);
    }

    /// <summary>The picture with the markup on it, at the picture's own pixel size.</summary>
    private BitmapSource Flatten()
    {
        _stage.Measure(new Size(_stage.Width, _stage.Height));
        _stage.Arrange(new Rect(0, 0, _stage.Width, _stage.Height));
        var target = new RenderTargetBitmap(_picture.PixelWidth, _picture.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(_stage);
        target.Freeze();
        return target;
    }

    private void Save()
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(_sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var path = ScreenFiles.NextPath(folder, DateTime.Now, ".png", File.Exists);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Flatten()));
            using (var stream = File.Create(path)) encoder.Save(stream);
            _status.Text = "Saved " + System.IO.Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Could not save the picture.";
        }
    }
}
