using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Atoll.Settings;

/// <summary>
/// One row of the settings window, Windows 11 style: icon, title, description on the left and the
/// control (the card's Content) on the right. Template lives in SettingsWindow.xaml.
/// </summary>
public sealed class SettingCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public static readonly DependencyProperty IsSwitchRowProperty =
        DependencyProperty.Register(nameof(IsSwitchRow), typeof(bool), typeof(SettingCard), new PropertyMetadata(false));

    /// <summary>True when the card's editor is a single on/off switch: the whole row then toggles it.</summary>
    public bool IsSwitchRow
    {
        get => (bool)GetValue(IsSwitchRowProperty);
        private set => SetValue(IsSwitchRowProperty, value);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        IsSwitchRow = newContent is CheckBox;
        Cursor = IsSwitchRow ? System.Windows.Input.Cursors.Hand : null;
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        // Clicking the title or the empty space flips the switch like clicking the switch itself.
        if (e.Handled || !IsSwitchRow || Content is not CheckBox { IsEnabled: true, IsMouseOver: false } box) return;
        box.IsChecked = box.IsChecked != true;
        e.Handled = true;
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
}

/// <summary>Visible when the bound int equals ConverterParameter (used to switch settings pages).</summary>
public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && int.TryParse(parameter?.ToString(), out var p) && i == p ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Hidden when the string is empty.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
