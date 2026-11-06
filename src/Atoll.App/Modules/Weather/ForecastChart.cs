using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Atoll.Modules.Weather;

public sealed class ForecastChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(ObservableCollection<WeatherChartPoint>), typeof(ForecastChart), new PropertyMetadata(null, OnPointsChanged));
    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(nameof(Metric), typeof(WeatherMetric), typeof(ForecastChart), new FrameworkPropertyMetadata(WeatherMetric.Wind, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(ForecastChart), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(ForecastChart), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(ForecastChart), new FrameworkPropertyMetadata(Brushes.Turquoise, FrameworkPropertyMetadataOptions.AffectsRender));

    public ObservableCollection<WeatherChartPoint>? Points { get => (ObservableCollection<WeatherChartPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public WeatherMetric Metric { get => (WeatherMetric)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public ForecastChart() { Focusable = true; Cursor = Cursors.Cross; SnapsToDevicePixels = true; }
    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (ForecastChart)d;
        if (e.OldValue is INotifyCollectionChanged old) CollectionChangedEventManager.RemoveHandler(old, chart.OnCollectionChanged);
        if (e.NewValue is INotifyCollectionChanged next) CollectionChangedEventManager.AddHandler(next, chart.OnCollectionChanged);
        chart.InvalidateVisual();
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Points is not { Count: > 0 } points || ActualWidth <= 0 || ActualHeight <= 30) return;
        var values = points.Where(p => p.Value is { } n && double.IsFinite(n)).Select(p => p.Value!.Value).ToArray();
        if (values.Length == 0) { Label(dc, "Hourly readings unavailable", 0, 30, Ink); return; }
        var min = Metric == WeatherMetric.Temperature ? Math.Floor(values.Min()) - 2 : 0;
        var max = Metric == WeatherMetric.Precipitation ? 100 : Math.Max(min + 1, values.Max() + (Metric == WeatherMetric.Temperature ? 2 : Math.Max(1, values.Max() * 0.15)));
        var chartHeight = ActualHeight - 27;
        var slot = ActualWidth / points.Count;
        var barWidth = Math.Max(2, Math.Min(26, slot - 3));
        var labelEvery = Math.Max(1, (int)Math.Ceiling(points.Count / Math.Max(1, ActualWidth / 57)));
        Point? previous = null;
        for (var i = 0; i < points.Count; i++)
        {
            var x = (i + 0.5) * slot;
            var selected = i == SelectedIndex;
            if (points[i].Value is { } value && double.IsFinite(value))
            {
                var y = chartHeight - Math.Clamp((value - min) / (max - min), 0, 1) * (chartHeight - 8);
                if (Metric == WeatherMetric.Temperature)
                {
                    if (previous is { } from) dc.DrawLine(new Pen(Accent, 2), from, new Point(x, y));
                    dc.DrawEllipse(selected ? Ink : Accent, null, new Point(x, y), selected ? 4 : 2, selected ? 4 : 2);
                    previous = new Point(x, y);
                }
                else
                {
                    dc.PushOpacity(selected ? 1 : 0.75);
                    dc.DrawRoundedRectangle(Accent, null, new Rect(x - barWidth / 2, y, barWidth, Math.Max(2, chartHeight - y)), 3, 3);
                    dc.Pop();
                    if (selected) dc.DrawLine(new Pen(Ink, 1), new Point(x, chartHeight + 3), new Point(x, chartHeight + 6));
                }
            }
            else
            {
                previous = null;
                dc.DrawLine(new Pen(Ink, 1), new Point(x - 2, chartHeight - 1), new Point(x + 2, chartHeight - 1));
            }
            if (i % labelEvery == 0)
                Label(dc, points[i].Label, Math.Clamp(x - 13, 0, Math.Max(0, ActualWidth - 36)), chartHeight + 10, Ink);
        }
        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen(Ink, 1), new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1), 5, 5);
    }

    private void Label(DrawingContext dc, string text, double x, double y, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text"), 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }
    private void Pick(Point position)
    {
        if (Points is { Count: > 0 } points)
            SetCurrentValue(SelectedIndexProperty, Math.Clamp((int)(position.X / Math.Max(1, ActualWidth) * points.Count), 0, points.Count - 1));
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Pick(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonDown(e); Focus(); CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonUp(e); if (IsMouseCaptured) { Pick(e.GetPosition(this)); ReleaseMouseCapture(); e.Handled = true; } }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Points is not { Count: > 0 } points) return;
        var index = e.Key switch { Key.Left => SelectedIndex - 1, Key.Right => SelectedIndex + 1, Key.Home => 0, Key.End => points.Count - 1, _ => -1 };
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        SetCurrentValue(SelectedIndexProperty, Math.Clamp(index, 0, points.Count - 1)); e.Handled = true;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
