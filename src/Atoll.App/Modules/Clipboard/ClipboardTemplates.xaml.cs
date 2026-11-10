using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Atoll.Modules.Clipboard;

/// <summary>
/// Rows are ordinary buttons (Enter/Space copy), so this only adds the list motions: Up/Down move
/// between rows and Delete removes the focused copy.
/// </summary>
public partial class ClipboardTemplates : ResourceDictionary
{
    public ClipboardTemplates() => InitializeComponent();

    private void OnRowKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        switch (e.Key)
        {
            case Key.Up:
                Move(row, -1);
                e.Handled = true;
                break;
            case Key.Down:
                Move(row, 1);
                e.Handled = true;
                break;
            case Key.Delete when row.DataContext is ClipItem item:
                var list = Ancestor<ItemsControl>(row);
                var index = list?.Items.IndexOf(item) ?? -1;
                item.RemoveCommand.Execute(null);
                if (list is not null && list.Items.Count > 0) FocusRow(list, Math.Min(index, list.Items.Count - 1));
                e.Handled = true;
                break;
        }
    }

    private static void Move(FrameworkElement row, int delta)
    {
        if (Ancestor<ItemsControl>(row) is not { } list) return;
        var index = list.Items.IndexOf(row.DataContext);
        if (index < 0) return;
        FocusRow(list, Math.Clamp(index + delta, 0, list.Items.Count - 1));
    }

    private static void FocusRow(ItemsControl list, int index)
    {
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container) return;
        if (RowButton(container) is not { } target) return;
        target.Focus();
        target.BringIntoView();
    }

    /// <summary>The item container is a ContentPresenter; the focusable row is the button inside it.</summary>
    private static Button? RowButton(DependencyObject root)
    {
        if (root is Button { Focusable: true } button) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (RowButton(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}
