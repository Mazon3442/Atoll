using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

    private void OnOpenOnboarding(object sender, RoutedEventArgs e) => Navigation.OpenOnboarding?.Invoke();

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
        if (!ReferenceEquals(e.Source, sender)) return;
        if (SearchBox is { Text.Length: > 0 }) SearchBox.Clear(); // picking a page ends the search
        PageScroll?.ScrollToTop();
    }

    private void OnOpenPage(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var page)) Vm.PageIndex = page;
    }

    // ---------- search ----------

    private sealed record SettingHit(SettingCard Card, int Page, string Section);

    private List<SettingHit>? _index;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Every setting card on every page, with the section it sits in. Built once, on the first search.</summary>
    private List<SettingHit> Index()
    {
        if (_index is not null) return _index;
        var hits = new List<SettingHit>();
        foreach (var page in ((Panel)PageScroll.Content).Children.OfType<FrameworkElement>())
        {
            if (BindingOperations.GetBinding(page, VisibilityProperty)?.ConverterParameter is not string text
                || !int.TryParse(text, out var index)) continue;
            var section = "";
            Collect(page, index, ref section, hits);
        }
        return _index = hits;
    }

    private void Collect(DependencyObject node, int page, ref string section, List<SettingHit> hits)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            switch (child)
            {
                case SettingCard card when card.Header.Length > 0:
                    hits.Add(new SettingHit(card, page, section));
                    break;
                case TextBlock title when ReferenceEquals(title.Style, TryFindResource("SectionHeader")):
                    section = title.Text;
                    break;
                case ItemsControl: // lists built from data (muted apps, API keys) are not fixed settings
                    break;
                case Expander expander:
                    var outer = section;
                    var inner = ExpanderTitle(expander) ?? section;
                    Collect(expander, page, ref inner, hits);
                    section = outer;
                    break;
                case DependencyObject next:
                    Collect(next, page, ref section, hits);
                    break;
            }
        }
    }

    /// <summary>A card its own page hides on purpose (no battery on a desktop, the pill gap while the notch is chosen).</summary>
    private bool IsHidden(SettingCard card)
    {
        var pages = PageScroll.Content;
        for (DependencyObject? node = card; node is not null && !ReferenceEquals(node, pages); node = LogicalTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(LogicalTreeHelper.GetParent(node), pages)) break; // the page panel itself is hidden when not selected
            if (node is UIElement { Visibility: Visibility.Collapsed }) return true;
        }
        return false;
    }

    private static string? ExpanderTitle(Expander expander) => expander.Header switch
    {
        string text => text,
        TextBlock block => block.Text,
        Panel panel => panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
        _ => null,
    };

    /// <summary>What kind of control a card has, and for a switch whether it is on: shown in each search result.</summary>
    private static string Kind(SettingCard card) => card.Content switch
    {
        CheckBox box => box.IsChecked == true ? "Switch · On" : "Switch · Off",
        ComboBox => "Choice",
        HotkeyBox => "Shortcut",
        ColorPicker => "Color",
        TextBox => "Text",
        Slider => "Slider",
        Button => "Button",
        Panel panel when panel.Children.OfType<Slider>().Any() => "Slider",
        Panel panel when panel.Children.OfType<CheckBox>().FirstOrDefault() is { } box && panel.Children.Count == 1 + panel.Children.OfType<Button>().Count() =>
            box.IsChecked == true ? "Switch · On" : "Switch · Off",
        Panel panel when panel.Children.OfType<CheckBox>().Any() => "Options",
        Panel panel when panel.Children.OfType<Button>().Any() => "Button",
        _ => "",
    };

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        var searching = query.Length > 0;
        ResultsScroll.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        PageHeader.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        ResultsPanel.Children.Clear();
        if (!searching) return;
        ResultsScroll.ScrollToTop();

        var pages = Vm.Pages;
        var scored = new List<(int Score, Button Row)>();
        for (var i = 0; i < pages.Count; i++)
            if (SettingsSearch.Score(query, pages[i].Title, "") > 0)
            {
                var page = i;
                scored.Add((3, Row($"Open {pages[i].Title}", "Page", "", "", () => Vm.PageIndex = page)));
            }
        foreach (var hit in Index().Where(h => !IsHidden(h.Card)))
        {
            var kind = Kind(hit.Card);
            var where = Vm.Pages[hit.Page].Title + (hit.Section.Length > 0 ? " › " + hit.Section : "");
            var score = SettingsSearch.Score(query, hit.Card.Header, hit.Card.Description + " " + where + " " + kind);
            if (score > 0) scored.Add((score, Row(hit.Card.Header, where, hit.Card.Description, kind, () => Open(hit))));
        }

        var rows = scored.OrderByDescending(r => r.Score).Take(40).Select(r => r.Row).ToList();
        var summary = new TextBlock
        {
            Text = rows.Count == 0 ? $"No settings match “{query}”. Try a shorter word." : rows.Count == 1 ? "1 setting matches" : $"{rows.Count} settings match",
            FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap,
        };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(summary, System.Windows.Automation.AutomationLiveSetting.Polite);
        ResultsPanel.Children.Add(summary);
        foreach (var row in rows) ResultsPanel.Children.Add(row);
    }

    private Button Row(string title, string where, string description, string kind, Action open)
    {
        var hint = (Style)FindResource("Hint");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = where, Style = hint, Margin = new Thickness(0, 2, 0, 0) });
        if (description.Length > 0)
            text.Children.Add(new TextBlock { Text = description, Style = hint, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 2, 0, 0) });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        if (kind.Length > 0)
        {
            var chip = new TextBlock { Text = kind, Style = hint, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);
        }
        var button = new Button { Style = (Style)FindResource("SearchHit"), Content = grid };
        System.Windows.Automation.AutomationProperties.SetName(button, kind.Length > 0 ? $"{title}, {where}, {kind}" : $"{title}, {where}");
        button.Click += (_, _) => open();
        return button;
    }

    /// <summary>Goes to the page, opens any folded section around the card and puts the keyboard on its control.</summary>
    private void Open(SettingHit hit)
    {
        SearchBox.Clear();
        Vm.PageIndex = hit.Page;
        for (DependencyObject? node = hit.Card; node is not null; node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node))
            if (node is Expander expander) expander.IsExpanded = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            hit.Card.BringIntoView();
            if (hit.Card.Content is UIElement editor && !editor.Focus() && FirstFocusable(editor) is { } inner) inner.Focus();
        });
    }

    private static UIElement? FirstFocusable(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { Focusable: true, IsEnabled: true, IsVisible: true } element) return element;
            if (FirstFocusable(child) is { } found) return found;
        }
        return null;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when SearchBox.Text.Length > 0:
                SearchBox.Clear();
                e.Handled = true;
                break;
            case Key.Down or Key.Enter when ResultsPanel.Children.OfType<Button>().FirstOrDefault() is { } first:
                if (e.Key == Key.Enter) first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else first.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not Button current) return;
        var rows = ResultsPanel.Children.OfType<Button>().ToList();
        var at = rows.IndexOf(current);
        switch (e.Key)
        {
            case Key.Down when at >= 0 && at < rows.Count - 1:
                rows[at + 1].Focus();
                e.Handled = true;
                break;
            case Key.Up when at > 0:
                rows[at - 1].Focus();
                e.Handled = true;
                break;
            case Key.Up when at == 0:
            case Key.Escape:
                SearchBox.Focus();
                if (e.Key == Key.Escape) SearchBox.Clear();
                e.Handled = true;
                break;
        }
    }
}
