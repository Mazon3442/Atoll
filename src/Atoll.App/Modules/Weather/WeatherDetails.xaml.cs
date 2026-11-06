using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
namespace Atoll.Modules.Weather;
public partial class WeatherDetails : UserControl
{
    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(nameof(HoverBrush), typeof(Brush), typeof(WeatherDetails));
    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public WeatherDetails()
    {
        InitializeComponent();
        SetResourceReference(HoverBrushProperty, "Island.Hover");
    }
}
