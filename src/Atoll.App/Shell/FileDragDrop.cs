using System.Windows;
using System.Windows.Input;

namespace Atoll.Shell;

/// <summary>Attached property: dropping files on the element runs Command with the dropped paths (string[]).</summary>
public static class FileDrop
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(FileDrop), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject d) => (ICommand?)d.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject d, ICommand? value) => d.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.DragOver -= OnDragOver;
        element.Drop -= OnDrop;
        element.AllowDrop = e.NewValue is not null;
        if (e.NewValue is null) return;
        element.DragOver += OnDragOver;
        element.Drop += OnDrop;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && GetCommand((DependencyObject)sender) is { } command
            && command.CanExecute(files))
            command.Execute(files);
        e.Handled = true;
    }
}

/// <summary>Attached property: dragging the element out drags the file at Path (to Explorer, chat apps, ...).</summary>
public static class FileDrag
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(FileDrag), new PropertyMetadata(null, OnPathChanged));

    private static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached(
        "Start", typeof(Point?), typeof(FileDrag));

    public static string? GetPath(DependencyObject d) => (string?)d.GetValue(PathProperty);

    public static void SetPath(DependencyObject d, string? value) => d.SetValue(PathProperty, value);

    private static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseLeftButtonDown -= OnDown;
        element.PreviewMouseMove -= OnMove;
        if (e.NewValue is null) return;
        element.PreviewMouseLeftButtonDown += OnDown;
        element.PreviewMouseMove += OnMove;
    }

    private static void OnDown(object sender, MouseButtonEventArgs e) =>
        ((DependencyObject)sender).SetValue(StartProperty, e.GetPosition((IInputElement)sender));

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var element = (UIElement)sender;
        if (e.LeftButton != MouseButtonState.Pressed || element.GetValue(StartProperty) is not Point start) return;
        var delta = e.GetPosition(element) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        element.ClearValue(StartProperty);
        if (GetPath(element) is not { } path || !(File.Exists(path) || Directory.Exists(path))) return;
        DragDrop.DoDragDrop(element, new DataObject(DataFormats.FileDrop, new[] { path }),
            DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }
}
