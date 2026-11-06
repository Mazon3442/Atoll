using System.Windows;
using System.Windows.Media;

namespace Atoll.Modules.Weather;

public sealed class WeatherSymbol : FrameworkElement
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(nameof(Code), typeof(int), typeof(WeatherSymbol), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsDayProperty = DependencyProperty.Register(nameof(IsDay), typeof(bool), typeof(WeatherSymbol), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(WeatherSymbol), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public int Code { get => (int)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public bool IsDay { get => (bool)GetValue(IsDayProperty); set => SetValue(IsDayProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 64, size / 64));
        var darkInk = Ink is SolidColorBrush brush && brush.Color.R + brush.Color.G + brush.Color.B < 380;
        var yellow = new SolidColorBrush(darkInk ? Color.FromRgb(142, 103, 0) : Color.FromRgb(255, 214, 10));
        var pen = new Pen(Code <= 1 ? yellow : Ink, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (Code <= 1 && !IsDay)
        {
            var moon = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(31, 32), 23, 23), new EllipseGeometry(new Point(43, 20), 23, 23));
            dc.DrawGeometry(null, pen, moon);
        }
        else if (Code <= 1)
        {
            dc.DrawEllipse(null, pen, new Point(32, 32), 13, 13);
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Math.PI / 4;
                dc.DrawLine(pen, new Point(32 + 21 * Math.Cos(angle), 32 + 21 * Math.Sin(angle)), new Point(32 + 27 * Math.Cos(angle), 32 + 27 * Math.Sin(angle)));
            }
        }
        else
        {
            var cloud = Geometry.Parse("M16,43 C4,43 4,25 16,25 C18,9 43,9 46,26 C60,24 65,43 50,43 Z");
            dc.DrawGeometry(null, pen, cloud);
            if (Code is >= 51 and <= 67 or >= 80 and <= 82)
                for (var i = 0; i < 3; i++) dc.DrawLine(pen, new Point(22 + i * 12, 49), new Point(18 + i * 12, 57));
            else if (Code is >= 71 and <= 77 or 85 or 86)
                for (var i = 0; i < 3; i++) dc.DrawEllipse(Ink, null, new Point(20 + i * 13, 53), 2, 2);
            else if (Code is 95 or 96 or 99) dc.DrawGeometry(null, pen, Geometry.Parse("M34,44 L26,53 L35,53 L28,62"));
            else if (Code is 45 or 48) { dc.DrawLine(pen, new Point(12, 50), new Point(52, 50)); dc.DrawLine(pen, new Point(18, 57), new Point(46, 57)); }
        }
        dc.Pop(); dc.Pop();
    }
}
