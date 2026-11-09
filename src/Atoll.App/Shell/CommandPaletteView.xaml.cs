using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Atoll.Shell;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView() => InitializeComponent();

    private CommandPaletteViewModel? ViewModel => DataContext as CommandPaletteViewModel;

    public void FocusSearch() => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    {
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }));

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            if (ResultsList.Items.Count == 0) return;
            ResultsList.SelectedIndex = e.Key == Key.Down ? 0 : ResultsList.Items.Count - 1;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            Keyboard.Focus(ResultsList);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ViewModel?.RunSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.Close();
            e.Handled = true;
        }
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.RunSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.Close();
            e.Handled = true;
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ResultsList.ContainerFromElement(source) is not ListBoxItem { Content: CommandItem item }) return;
        ViewModel!.Selected = item;
        ViewModel.RunSelected();
        e.Handled = true;
    }
}
