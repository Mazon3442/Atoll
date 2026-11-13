using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Atoll.Shell;

/// <summary>
/// A corner radius that can never turn into an oval. WPF scales an oversized radius per axis, so a 14 radius on a 24 px
/// tall chip, or a huge "pill" value, draws elliptical ends. <c>{shell:Round}</c> is a true pill (half the shorter
/// side); <c>{shell:Round 14}</c> is the radius from the design ladder, clamped to half the shorter side.
/// </summary>
[MarkupExtensionReturnType(typeof(CornerRadius))]
public sealed class RoundExtension : MarkupExtension
{
    private readonly double _radius;

    public RoundExtension() => _radius = double.PositiveInfinity;
    public RoundExtension(double radius) => _radius = radius;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Converter = RoundConverter.Instance, ConverterParameter = _radius };
        binding.Bindings.Add(new Binding(nameof(FrameworkElement.ActualWidth)) { RelativeSource = RelativeSource.Self });
        binding.Bindings.Add(new Binding(nameof(FrameworkElement.ActualHeight)) { RelativeSource = RelativeSource.Self });
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class RoundConverter : IMultiValueConverter
    {
        public static RoundConverter Instance { get; } = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var width = values.Length > 0 && values[0] is double w ? w : 0;
            var height = values.Length > 1 && values[1] is double h ? h : 0;
            var limit = Math.Max(0, Math.Min(width, height) / 2);
            var wanted = parameter is double radius ? radius : double.PositiveInfinity;
            return new CornerRadius(Math.Min(wanted, limit));
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
