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
        Activated += (_, _) =>
        {
            viewModel.RefreshNotificationAccessCommand.Execute(null);
            if (viewModel.PageIndex == 4) foreach (var agent in viewModel.AgentIntegrations) agent.Refresh();
        };
        Closed += (_, _) => viewModel.Dispose();
        DataContext = viewModel;
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext;

    private void OnAddProvider(object sender, RoutedEventArgs e)
    {
        if (!Vm.AddProvider(ProviderKey.Password, ProviderMetricsKey.Password)) return;
        ProviderKey.Clear();
        ProviderMetricsKey.Clear();
    }

    private void OnCopyGatewayToken(object sender, RoutedEventArgs e) =>
        CopyToClipboard(Vm.GatewayToken(), secret: true, "Gateway token copied. Use it as your app's API key.");

    private void OnCopyProviderAddress(object sender, RoutedEventArgs e) =>
        CopyToClipboard((string)((FrameworkElement)sender).Tag, secret: false, "Address copied. Use it as your app's API base URL.");

    /// <summary>Secrets are marked so clipboard history, cloud clipboard and ATOLL's own history skip them.</summary>
    private void CopyToClipboard(string text, bool secret, string done)
    {
        try
        {
            var data = new DataObject();
            data.SetText(text);
            if (secret)
            {
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([1, 0, 0, 0]));
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream([0, 0, 0, 0]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream([0, 0, 0, 0]));
            }
            Clipboard.SetDataObject(data, copy: true);
            Vm.ReportProviderMessage(done);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Vm.ReportProviderMessage("Couldn't copy to the clipboard. Try again.");
        }
    }

    private Point _moduleDragOrigin;
    private void OnModuleDragStart(object sender, MouseButtonEventArgs e) => _moduleDragOrigin = e.GetPosition(this);
    private void OnModuleDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: ModuleTabRow row } handle) return;
        var delta = e.GetPosition(this) - _moduleDragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(handle, new DataObject(typeof(ModuleTabRow), row), DragDropEffects.Move);
        e.Handled = true;
    }
    private void OnModuleDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ModuleTabRow)) ? DragDropEffects.Move : DragDropEffects.None;
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
        if (DataContext is SettingsViewModel vm && sender is FrameworkElement { DataContext: ModuleTabRow target }
            && e.Data.GetData(typeof(ModuleTabRow)) is ModuleTabRow source)
            vm.MoveTab(source, vm.ModuleTabs.IndexOf(target));
        e.Handled = true;
    }
    private void OnModuleKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || key is not (Key.Up or Key.Down)
            || sender is not FrameworkElement { DataContext: ModuleTabRow row } || DataContext is not SettingsViewModel vm) return;
        vm.MoveTab(row, vm.ModuleTabs.IndexOf(row) + (key == Key.Up ? -1 : 1));
        e.Handled = true;
    }

    private void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender)) PageScroll?.ScrollToTop();
    }
}
