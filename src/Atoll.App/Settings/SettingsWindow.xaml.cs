using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Atoll.Settings;

public partial class SettingsWindow : Window
{
    public static readonly DependencyProperty IsNarrowProperty = DependencyProperty.Register(nameof(IsNarrow), typeof(bool),
        typeof(SettingsWindow), new PropertyMetadata(false));
    public bool IsNarrow { get => (bool)GetValue(IsNarrowProperty); private set => SetValue(IsNarrowProperty, value); }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        SizeChanged += (_, _) => IsNarrow = ActualWidth < 960;
        Activated += (_, _) => viewModel.RefreshNotificationAccessCommand.Execute(null);
        Closed += (_, _) => viewModel.Dispose();
        DataContext = viewModel;
    }

    private Point _moduleDragOrigin;
    private void OnModuleDragStart(object sender, MouseButtonEventArgs e) => _moduleDragOrigin = e.GetPosition(this);
    private void OnModuleDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: ModuleRow row } handle) return;
        var delta = e.GetPosition(this) - _moduleDragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(handle, new DataObject(typeof(ModuleRow), row), DragDropEffects.Move);
        e.Handled = true;
    }
    private void OnModuleDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ModuleRow)) ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effects == DragDropEffects.Move && sender is Border border)
        {
            border.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
            var pointer = e.GetPosition(PageScroll);
            if (pointer.Y < 36) PageScroll.LineUp();
            else if (pointer.Y > PageScroll.ActualHeight - 36) PageScroll.LineDown();
        }
        e.Handled = true;
    }
    private void OnModuleDragLeave(object sender, DragEventArgs e) => ResetDropBorder(sender);
    private static void ResetDropBorder(object sender)
    {
        if (sender is Border border) border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
    }
    private void OnModuleDrop(object sender, DragEventArgs e)
    {
        ResetDropBorder(sender);
        if (DataContext is SettingsViewModel vm && sender is FrameworkElement { DataContext: ModuleRow target }
            && e.Data.GetData(typeof(ModuleRow)) is ModuleRow source)
            vm.MoveModule(source, vm.Modules.IndexOf(target));
        e.Handled = true;
    }
    private void OnModuleKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || key is not (Key.Up or Key.Down)
            || sender is not FrameworkElement { DataContext: ModuleRow row } || DataContext is not SettingsViewModel vm) return;
        vm.MoveModule(row, vm.Modules.IndexOf(row) + (key == Key.Up ? -1 : 1));
        e.Handled = true;
    }

    private void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender)) PageScroll?.ScrollToTop();
    }
}
