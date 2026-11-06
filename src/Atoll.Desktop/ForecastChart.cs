using System.ComponentModel;
using Atoll.Modules.Weather;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Atoll.Desktop;

public sealed class ForecastChart : Control
{
    private readonly WeatherViewModel _weather;
    public ForecastChart(WeatherViewModel weather)
    {
        _weather = weather; Focusable = true; Cursor = new Cursor(StandardCursorType.Cross);
        Avalonia.Automation.AutomationProperties.SetName(this, "Hourly forecast. Use left and right arrows to select a reading.");
        AttachedToVisualTree += (_, _) => { weather.PropertyChanged += OnChanged; weather.ChartPoints.CollectionChanged += OnPointsChanged; };
        DetachedFromVisualTree += (_, _) => { weather.PropertyChanged -= OnChanged; weather.ChartPoints.CollectionChanged -= OnPointsChanged; };
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
    private void OnPointsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var points = _weather.ChartPoints;
        var values = points.Where(p => p.Value is { } n && double.IsFinite(n)).Select(p => p.Value!.Value).ToArray();
        if (values.Length == 0 || Bounds.Width < 1) return;
        var min = _weather.SelectedMetric == WeatherMetric.Temperature ? Math.Floor(values.Min()) - 2 : 0;
        var max = _weather.SelectedMetric == WeatherMetric.Precipitation ? 100 : Math.Max(min + 1, values.Max() + 2);
        var height = Bounds.Height - 24; var slot = Bounds.Width / points.Count;
        var accent = new SolidColorBrush(Color.Parse("#0A84FF"));
        Point? previous = null;
        for (var i = 0; i < points.Count; i++)
        {
            var x = (i + .5) * slot;
            if (points[i].Value is not { } value || !double.IsFinite(value)) { previous = null; continue; }
            var y = height - Math.Clamp((value - min) / (max - min), 0, 1) * (height - 8);
            if (_weather.SelectedMetric == WeatherMetric.Temperature)
            {
                if (previous is { } p) context.DrawLine(new Pen(accent, 2), p, new Point(x, y));
                context.DrawEllipse(accent, null, new Point(x, y), i == _weather.SelectedChartIndex ? 4 : 2, i == _weather.SelectedChartIndex ? 4 : 2);
                previous = new Point(x, y);
            }
            else context.DrawRectangle(accent, null, new Rect(x - Math.Min(20, slot - 3) / 2, y, Math.Max(2, Math.Min(20, slot - 3)), Math.Max(2, height - y)), 3, 3);
            if (i % Math.Max(1, points.Count / 6) == 0)
            {
                var label = new FormattedText(points[i].Label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 10, new SolidColorBrush(Color.Parse("#898989")));
                context.DrawText(label, new Point(Math.Clamp(x - 12, 0, Math.Max(0, Bounds.Width - 35)), height + 8));
            }
        }
    }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); Pick(e.GetPosition(this)); }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); Focus(); Pick(e.GetPosition(this)); e.Handled = true; }
    private void Pick(Point position) { if (_weather.ChartPoints.Count > 0) _weather.SelectedChartIndex = Math.Clamp((int)(position.X / Math.Max(1, Bounds.Width) * _weather.ChartPoints.Count), 0, _weather.ChartPoints.Count - 1); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (_weather.ChartPoints.Count == 0) return;
        var index = e.Key switch { Key.Left => _weather.SelectedChartIndex - 1, Key.Right => _weather.SelectedChartIndex + 1, Key.Home => 0, Key.End => _weather.ChartPoints.Count - 1, _ => -1 };
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) { _weather.SelectedChartIndex = Math.Clamp(index, 0, _weather.ChartPoints.Count - 1); e.Handled = true; }
    }
}
