using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Atoll.Shell;

/// <summary>Lets a text box that saves on losing focus also save when Enter is pressed.</summary>
public static class CommitOnEnter
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(CommitOnEnter), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewKeyDown -= OnKeyDown;
        if ((bool)e.NewValue) box.PreviewKeyDown += OnKeyDown;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }
}
