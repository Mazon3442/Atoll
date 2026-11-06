using System.Windows;

namespace Atoll.Modules.Stats;

public partial class StatsTemplates : ResourceDictionary
{
    public StatsTemplates() => InitializeComponent();
    private void OnViewVisible(object sender, RoutedEventArgs e) => Observe(sender);
    private void OnViewVisible(object sender, DependencyPropertyChangedEventArgs e) => Observe(sender);
    private static void Observe(object sender)
    {
        if (sender is not FrameworkElement view) return;
        // Inherited DataContext can be cleared before WPF raises Unloaded.
        if (view.Tag is StatsViewModel previous && !ReferenceEquals(previous, view.DataContext)) previous.Observe(view, false);
        view.Tag = view.DataContext as StatsViewModel;
        if (view.Tag is StatsViewModel vm) vm.Observe(view, view.IsVisible && view.IsLoaded);
    }
    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StatsViewModel vm } view) { vm.Observe(view, false); view.Tag = null; }
    }
}
