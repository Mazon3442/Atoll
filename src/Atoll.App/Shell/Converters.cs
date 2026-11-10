using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Atoll.Shell;

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A corner radius of half the element's height, so a wide element is a true pill. A fixed huge radius in WPF
/// stretches into an oval on anything wider than it is tall.
/// </summary>
public sealed class PillRadius : IValueConverter
{
    public static PillRadius Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new CornerRadius(value is double height ? Math.Max(0, height / 2) : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
