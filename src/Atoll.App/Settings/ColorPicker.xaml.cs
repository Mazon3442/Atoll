using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Atoll.Theme;

namespace Atoll.Settings;

public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ColorPicker),
        new PropertyMetadata("Color"));

    private Color _color = Colors.White;
    private bool _syncing;

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public ColorPicker()
    {
        InitializeComponent();
        Sync();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPicker)d;
        if (ThemeService.TryParseColor(e.NewValue as string, out var color))
        {
            picker._color = color;
            if (picker.Square is not null) picker.Sync();
        }
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            var (hue, saturation, brightness) = ColorSquare.ToHsv(_color);
            // Gray and black have no hue; keep the chosen hue so the square remains useful.
            if (saturation > 0) HueSlider.Value = hue;
            Square.Hue = HueSlider.Value;
            if (brightness > 0) Square.Saturation = saturation;
            Square.Brightness = brightness;
            Square.InvalidateVisual();
            AlphaSlider.Value = _color.A * 100.0 / 255;
            RedField.Text = _color.R.ToString(CultureInfo.InvariantCulture);
            GreenField.Text = _color.G.ToString(CultureInfo.InvariantCulture);
            BlueField.Text = _color.B.ToString(CultureInfo.InvariantCulture);
            AlphaField.Text = (_color.A * 100.0 / 255).ToString("0.#", CultureInfo.InvariantCulture);
            HexField.Text = SwatchHex.Text = Format(_color);
            Swatch.Background = new SolidColorBrush(_color);
            ErrorText.Visibility = Visibility.Collapsed;
        }
        finally { _syncing = false; }
    }

    internal static string Format(Color color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private void Publish(Color color)
    {
        _color = color;
        SetCurrentValue(ValueProperty, Format(color));
        Sync();
    }

    private void OnSquareChanged(object? sender, EventArgs e)
    {
        if (!_syncing) Publish(ColorSquare.FromHsv(HueSlider.Value, Square.Saturation, Square.Brightness, _color.A));
    }

    private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || Square is null) return;
        Square.Hue = e.NewValue;
        Publish(ColorSquare.FromHsv(e.NewValue, Square.Saturation, Square.Brightness, _color.A));
    }

    private void OnAlphaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing && Square is not null)
            Publish(Color.FromArgb((byte)Math.Round(e.NewValue * 255 / 100), _color.R, _color.G, _color.B));
    }

    private void OnFieldCommit(object sender, KeyboardFocusChangedEventArgs e) => Commit((TextBox)sender);

    internal bool Commit(TextBox field)
    {
        if (_syncing) return true;
        var text = field.Text.Trim();
        var color = _color;
        var valid = false;
        if (field == HexField)
        {
            valid = (text.Length == 7 || text.Length == 9) && text.StartsWith('#') && ThemeService.TryParseColor(text, out color);
        }
        else if (field == AlphaField)
        {
            valid = double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var alpha)
                && double.IsFinite(alpha) && alpha >= 0 && alpha <= 100;
            if (valid) color.A = (byte)Math.Round(alpha * 255 / 100);
        }
        else if (byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var channel))
        {
            valid = true;
            if (field == RedField) color.R = channel;
            else if (field == GreenField) color.G = channel;
            else if (field == BlueField) color.B = channel;
        }
        if (valid) Publish(color);
        else
        {
            ErrorText.Text = field == HexField ? "Use #RRGGBB or #AARRGGBB."
                : field == AlphaField ? "Opacity must be between 0 and 100%." : $"{field.Tag} must be a whole number from 0 to 255.";
            ErrorText.Visibility = Visibility.Visible;
        }
        return valid;
    }

    private void OnOpened(object? sender, EventArgs e) { Sync(); Square.Focus(); }
    private void OnClosed(object? sender, EventArgs e) => SwatchButton.Focus();
    private void OnDone(object sender, RoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox field && !Commit(field)) return;
        if (ErrorText.Visibility != Visibility.Visible) Flyout.IsOpen = false;
    }

    private void OnFlyoutKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Flyout.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Enter && e.OriginalSource is TextBox field) { Commit(field); e.Handled = true; }
    }
}
