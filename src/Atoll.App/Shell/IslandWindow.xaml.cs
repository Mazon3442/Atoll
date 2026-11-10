using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Modules.Clock;
using Atoll.Modules.Launcher;
using Atoll.Settings;
using Atoll.Theme;

namespace Atoll.Shell;

public enum IslandMode
{
    Compact,
    Peek,
    Expanded,
}

/// <summary>
/// One island on one monitor. The window is a fixed transparent canvas; only the pill inside it
/// animates, so clicks outside the pill fall through to the desktop.
/// </summary>
public partial class IslandWindow : Window
{
    private static readonly IEasingFunction Spring = Freeze(new SpringEase());
    private static readonly IEasingFunction SmoothSpring = Freeze(new SpringEase(1));
    private static readonly Duration MorphDuration = TimeSpan.FromMilliseconds(320);
    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(140);

    private const int PointerTickMs = 60;
    private const int CollapseAfterMs = 900;
    private const int PeekCollapseAfterMs = 450;
    private const int MinClickThroughDwellMs = 350; // short passes (moving to a browser tab) must not open it
    private const double PassThroughOpacity = 0.25;
    private const double SideChipWidth = 118;
    private const double SideChipGap = 4;

    private readonly SettingsService _settings;
    private readonly ModuleManager _modules;
    private readonly Action<int> _openSettings;

    // The cursor position is polled instead of using MouseEnter/Leave: a click-through window gets
    // no mouse events, and layered WPF windows raise spurious MouseLeave events anyway.
    private readonly DispatcherTimer _pointer;
    // Watches the top edge while the island is hidden (or revealed), often enough to draw the hold line smoothly.
    private readonly DispatcherTimer _edgeTimer;
    private RevealHint? _hint;
    private int _dwellMs;
    private int _outsideMs;
    private bool _alertPeek;
    private bool _heldOpen;
    private nint _returnFocusTo; // the app that had focus before a shortcut opened the island
    private int _wheelDelta;
    private bool _faded;

    private nint _hwnd;
    private IslandMode _mode = IslandMode.Compact;
    private Activity? _primary;
    private IReadOnlyList<Activity> _sideActivities = [];
    private readonly SideChipViewModel?[] _sideChips = new SideChipViewModel?[4];
    private readonly ObservableCollection<ModuleTab> _visibleTabs = [];
    private readonly ObservableCollection<ModuleTab> _menuTabs = [];
    private ModuleTab? _transientTab;
    private bool _refreshingTabs;
    private readonly CommandPaletteViewModel _palette;
    private bool _paletteOpen;
    private Rect _peekAnchor = Rect.Empty;
    private object? _shownContent;
    private string? _shownTemplateMode;
    private INotifyPropertyChanged? _watched;
    private INotifyPropertyChanged? _watchedPanel;
    private bool _renderQueued;
    private (double W, double H, double R) _target = (-1, -1, -1);

    private bool _paused;
    private ForegroundKind _foreground;
    private bool? _clickThroughApplied;
    private TimeSpan? _edgeHoverSince;
    private bool _revealed;

    // Frosted glass: the system blur behind the pill and bubbles, or a captured blur fallback.
    private readonly DispatcherTimer _glassTimer;
    private readonly SystemBackdrop _backdrop = new();
    private bool _glassActive;
    private bool _systemGlass;
    private bool _systemGlassTried;
    private bool _captureHidden;
    private string _glassKey = "";
    private BitmapSource? _glassImage;
    private Int32Rect _glassArea;
    private ImageBrush? _pillGlassBrush;
    private ImageBrush? _bubbleGlassBrush;
    private ImageBrush? _bubble2GlassBrush;
    private ImageBrush? _bubble3GlassBrush;
    private ImageBrush? _bubble4GlassBrush;

    public IslandWindow(MonitorInfo monitor, SettingsService settings, ModuleManager modules, Action<int> openSettings)
    {
        InitializeComponent();
        Monitor = monitor;
        _settings = settings;
        _modules = modules;
        _openSettings = openSettings;

        var h0 = CompactHeight;
        Pill.Width = h0 * 3.2;
        Pill.Height = h0;
        Pill.Radius = h0 / 2;

        _pointer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(PointerTickMs) };
        _pointer.Tick += (_, _) => OnPointerTick();
        _edgeTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _edgeTimer.Tick += (_, _) =>
        {
            if (Native.GetCursorPos(out var p)) CheckFullscreenEdge(new Point(p.X, p.Y), System.Diagnostics.Stopwatch.GetElapsedTime(0));
        };
        _glassTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _glassTimer.Tick += (_, _) => SampleGlass();

        Tabs.ItemsSource = _visibleTabs;
        TabMenu.ItemsSource = _menuTabs;
        _palette = new CommandPaletteViewModel(BuildPaletteCommands);
        Palette.DataContext = _palette;
        _palette.CloseRequested += ClosePalette;
        modules.LayoutChanged += OnModulesLayoutChanged;
        modules.Tabs.CollectionChanged += OnTabsChanged;
        BindStatusChips();
        if (FocusService.Current is { } focus) focus.PropertyChanged += OnFocusServiceChanged;
        Tabs.SelectionChanged += (_, _) =>
        {
            if (_refreshingTabs) return;
            if (_paletteOpen) ClosePalette();
            if (Tabs.SelectedItem is { } selected) Tabs.ScrollIntoView(selected); // a narrow island scrolls the tab bar
            // Picking a bookmarked tab leaves a menu tab that was only open for this visit.
            if (_transientTab is not null && !ReferenceEquals(Tabs.SelectedItem, _transientTab))
            {
                _transientTab = null;
                RefreshTabStrip();
            }
            ShowSelectedTab();
            if (_mode == IslandMode.Expanded) Render();
        };
        RefreshTabStrip();

        Pill.MouseLeftButtonUp += (_, e) =>
        {
            if (_mode == IslandMode.Expanded) return;
            SetMode(IslandMode.Expanded, ActivityTab(_primary));
            e.Handled = true;
        };
        Bubble.MouseLeftButtonUp += (_, e) => OpenSide(0, e);
        Bubble2.MouseLeftButtonUp += (_, e) => OpenSide(1, e);
        Bubble3.MouseLeftButtonUp += (_, e) => OpenSide(2, e);
        Bubble4.MouseLeftButtonUp += (_, e) => OpenSide(3, e);
        Pill.SizeChanged += (_, _) => UpdateBubbleOffset();

        // Dragging files onto the island opens the shelf so they can be dropped there.
        Pill.AllowDrop = true;
        Pill.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && _mode != IslandMode.Expanded
                && _modules.Tabs.Any(t => t.Contains("shelf")))
                SetMode(IslandMode.Expanded, "shelf");
        };
        LayoutUpdated += (_, _) => UpdateGlassFrame(); // follows the pill through every animation frame

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
            ex = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (nint)ex);
            ApplySettings();
        };
        // Moving to a monitor with another DPI makes WPF rescale the window; re-center afterwards.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        Deactivated += (_, _) =>
        {
            if (_mode == IslandMode.Expanded) SetMode(IslandMode.Compact);
        };
        PreviewKeyDown += (_, e) =>
        {
            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (_paletteOpen)
            {
                if (e.Key == Key.Escape || (ctrl && e.Key == Key.K))
                {
                    ClosePalette();
                    e.Handled = true;
                }
                return;
            }
            if (_mode != IslandMode.Expanded) return;
            if (e.Key == Key.Escape) SetMode(IslandMode.Compact);
            else if (ctrl && e.Key == Key.K)
            {
                ShowPalette();
                e.Handled = true;
                return;
            }
            else if (ctrl && e.Key == Key.Tab) StepTab(shift ? -1 : 1, wrap: true);
            else if (ctrl && e.Key is Key.PageDown or Key.PageUp) StepTab(e.Key == Key.PageDown ? 1 : -1, wrap: true);
            else if (ctrl && e.Key is >= Key.D1 and <= Key.D9 && e.Key - Key.D1 < Tabs.Items.Count)
            {
                Tabs.SelectedIndex = e.Key - Key.D1;
                if (Tabs.IsKeyboardFocusWithin) FocusSelectedTab();
            }
            else return;
            e.Handled = true;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _pointer.Start();
            else _pointer.Stop();
            UpdateGlass();
        };
        Closed += (_, _) =>
        {
            _modules.LayoutChanged -= OnModulesLayoutChanged;
            _modules.Tabs.CollectionChanged -= OnTabsChanged;
            if (FocusService.Current is { } focus) focus.PropertyChanged -= OnFocusServiceChanged;
            _palette.CloseRequested -= ClosePalette;
            Watch(null);
            WatchPanel(null);
            _pointer.Stop();
            _edgeTimer.Stop();
            _hint?.Close();
            _glassTimer.Stop();
            _backdrop.Dispose();
            DisposeSideChips();
        };
    }

    /// <summary>The header's status chips use the System group's sound, battery and quick actions.</summary>
    private void BindStatusChips()
    {
        StatusChips.DataContext = _modules.Tabs.Select(t => t.Panel).OfType<SystemGroupViewModel>().FirstOrDefault();
        UpdateFocusChip();
    }

    private void OnFocusServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FocusService.IsActive) or nameof(FocusService.Status)) UpdateFocusChip();
    }

    /// <summary>Shows the moon chip only when focus is enabled and the chip setting is on.</summary>
    private void UpdateFocusChip()
    {
        var focus = FocusService.Current;
        var show = focus is not null && _settings.Current.Focus.Enabled && _settings.Current.Focus.ShowChip;
        FocusButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            FocusPopup.IsOpen = false;
            return;
        }
        FocusButton.SetResourceReference(Control.ForegroundProperty, focus!.IsActive ? "Island.Accent" : "Island.Foreground");
        FocusButton.ToolTip = focus.Status;
        AutomationProperties.SetName(FocusButton, focus.Status);
        FocusPopupTitle.Text = focus.IsActive ? focus.Status : "Do Not Disturb";
        FocusOffButton.Visibility = focus.IsActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFocusClick(object sender, RoutedEventArgs e)
    {
        if (FocusService.Current is null || !_settings.Current.Focus.Enabled) return;
        UpdateFocusChip();
        FocusPopup.IsOpen = !FocusPopup.IsOpen;
    }

    private void OnFocus30(object sender, RoutedEventArgs e) => FocusFor(TimeSpan.FromMinutes(30));
    private void OnFocus60(object sender, RoutedEventArgs e) => FocusFor(TimeSpan.FromHours(1));
    private void OnFocus120(object sender, RoutedEventArgs e) => FocusFor(TimeSpan.FromHours(2));

    private void OnFocusTomorrow(object sender, RoutedEventArgs e)
    {
        FocusService.Current?.FocusUntil(new DateTimeOffset(DateTime.Today.AddDays(1)));
        FocusPopup.IsOpen = false;
    }

    private void OnFocusOff(object sender, RoutedEventArgs e)
    {
        FocusService.Current?.Clear();
        FocusPopup.IsOpen = false;
    }

    private void FocusFor(TimeSpan duration)
    {
        FocusService.Current?.FocusFor(duration);
        FocusPopup.IsOpen = false;
    }

    private void OnModulesLayoutChanged()
    {
        BindStatusChips();
        _headerWidth = 0;
        RefreshTabStrip();
    }

    private void OnTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnModulesLayoutChanged();

    public MonitorInfo Monitor { get; }

    public bool IsExpanded => _mode == IslandMode.Expanded;

    private double CompactHeight => Math.Clamp(_settings.Current.Theme.CompactHeight, 24, 60);

    private double Roundness => Math.Clamp(_settings.Current.Theme.Roundness, 0, 1);
    private bool Notch => _settings.Current.Theme.Shape == IslandShape.Notch;

    /// <summary>A notch touches the top edge; a pill floats the chosen gap below it.</summary>
    private double TopGap => Notch ? 0 : Math.Clamp(_settings.Current.TopOffset, 0, 80);

    /// <summary>Windows' animation effects switch wins over the island's own motion setting.</summary>
    private bool Animated => SystemParameters.ClientAreaAnimation && _settings.Current.Theme.Motion != MotionStyle.Off;

    private IEasingFunction Ease => _settings.Current.Theme.Motion == MotionStyle.Smooth ? SmoothSpring : Spring;

    private double AvailablePanelHeight => Math.Max(140, Monitor.WorkArea.Height / Monitor.Scale - TopGap - 24);

    /// <summary>Pass clicks through only when a maximized app sits behind the island (tabs, toolbars).</summary>
    private bool ClickThrough => PassesThrough(_settings.Current, _foreground);

    internal static bool PassesThrough(AppSettings settings, ForegroundKind foreground) =>
        settings.ClickThrough && foreground == ForegroundKind.Maximized;

    /// <summary>Compatibility overload for tests and the two-slot callers.</summary>
    public void SetActivities(Activity? primary, Activity? secondary, Activity? tertiary = null) =>
        SetActivitySnapshot(primary, new[] { secondary, tertiary }.OfType<Activity>().ToList());

    public void SetActivitySnapshot(Activity? primary, IReadOnlyList<Activity> sides)
    {
        var isNewPrimary = !ReferenceEquals(primary, _primary);
        _primary = primary;
        _sideActivities = sides.Take(4).ToList();
        RebuildSideChips();

        if (isNewPrimary && primary is { Alert: true } && _mode == IslandMode.Compact)
        {
            // Alerts open the island on their own; hovering is not required to keep them open.
            _alertPeek = true;
            _mode = IslandMode.Peek;
        }
        else if (isNewPrimary && _alertPeek)
        {
            _alertPeek = false;
            if (_mode == IslandMode.Peek && !IsCursorOverIsland()) _mode = IslandMode.Compact;
        }

        if (_mode == IslandMode.Peek && primary is null) _mode = IslandMode.Compact;
        UpdatePresence();
        Render();
    }

    private void RebuildSideChips()
    {
        DisposeSideChips();
        var accent = AccentColor();
        var wide = _sideActivities.Count >= 3;
        for (var i = 0; i < _sideActivities.Count && i < _sideChips.Length; i++)
        {
            var chip = new SideChipViewModel(_sideActivities[i], accent);
            chip.SetWide(wide);
            _sideChips[i] = chip;
        }
    }

    private void DisposeSideChips()
    {
        foreach (var chip in _sideChips) chip?.Dispose();
        Array.Clear(_sideChips, 0, _sideChips.Length);
    }

    private Color AccentColor() =>
        ThemeService.TryParseColor(_settings.Current.Theme.Accent, out var color) ? color : Color.FromRgb(0x0A, 0x84, 0xFF);

    /// <summary>Re-reads size/offset/theme settings.</summary>
    public void ApplySettings()
    {
        var t = _settings.Current.Theme;
        var offset = TopGap;
        foreach (var b in new[] { Pill, Bubble, Bubble2, Bubble3, Bubble4 }) b.Notch = Notch;
        Stage.Margin = new Thickness(0, offset, 0, 0);
        var desiredWidth = Math.Max(1200, Math.Clamp(t.ExpandedWidth, 320, 900) + 4 * (SideChipWidth + 8));
        Width = Math.Min(desiredWidth, Math.Max(760, Monitor.WorkArea.Width / Monitor.Scale - 24));
        Height = AvailablePanelHeight + offset + 24; // room for panels that grow to fit their content
        _target = (-1, -1, -1);
        _shownContent = null; // template choice may depend on size; rebuild
        RebuildSideChips(); // the theme accent may have changed
        Place();
        UpdatePresence();
        UpdateGlass();
        Render();
    }

    /// <summary>
    /// Opens the expanded panel on a module's tab without the pointer being there (launch option,
    /// shortcuts). It then stays open until the pointer has visited it once, or Esc / clicking away.
    /// </summary>
    public void OpenModule(string moduleId)
    {
        if (!IsVisible) return;
        SetMode(IslandMode.Expanded, moduleId);
        _heldOpen = true;
    }

    /// <summary>
    /// The open shortcut: opens the island with keyboard focus on its tabs, or closes it when it is open.
    /// Over a fullscreen app it reveals the island the way an edge hold does. Closing returns focus to the
    /// app that had it.
    /// </summary>
    public void ToggleFromKeyboard()
    {
        if (_paused) return;
        if (_mode == IslandMode.Expanded)
        {
            SetMode(IslandMode.Compact);
            return;
        }
        if (!IsVisible)
        {
            _revealed = true;
            UpdatePresence();
            if (!IsVisible) return;
        }
        var previous = Native.GetForegroundWindow();
        SetMode(IslandMode.Expanded);
        _returnFocusTo = previous == _hwnd ? 0 : previous;
        _heldOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusSelectedTab);
    }

    private void FocusSelectedTab()
    {
        if (_mode != IslandMode.Expanded) return;
        Tabs.UpdateLayout();
        if (Tabs.ItemContainerGenerator.ContainerFromItem(Tabs.SelectedItem) is ListBoxItem item) Keyboard.Focus(item);
        else Keyboard.Focus(Tabs);
    }

    /// <summary>Opens the command palette from a shortcut.</summary>
    public void ShowPalette()
    {
        if (_paused) return;
        if (!IsVisible)
        {
            _revealed = true;
            UpdatePresence();
            if (!IsVisible) return;
        }
        var previous = Native.GetForegroundWindow();
        SetMode(IslandMode.Expanded);
        _returnFocusTo = previous == _hwnd ? 0 : previous;
        _heldOpen = true;
        _paletteOpen = true;
        _palette.Refresh();
        PanelBody.Visibility = Visibility.Collapsed;
        Palette.Visibility = Visibility.Visible;
        Palette.FocusSearch();
    }

    private void ClosePalette()
    {
        if (!_paletteOpen) return;
        _paletteOpen = false;
        Palette.Visibility = Visibility.Collapsed;
        PanelBody.Visibility = Visibility.Visible;
        if (_mode == IslandMode.Expanded) FocusSelectedTab();
    }

    private void RunAndTuck(Action action)
    {
        if (_mode == IslandMode.Expanded) SetMode(IslandMode.Compact);
        action();
    }

    private IReadOnlyList<CommandItem> BuildPaletteCommands()
    {
        var items = new List<CommandItem>();
        foreach (var tab in _modules.Tabs)
        {
            var target = tab;
            items.Add(new CommandItem(tab.Descriptor.Name, "Section", tab.Descriptor.Glyph, "", CommandKind.Tab,
                () => OpenFromMenu(target)));
        }

        var system = _modules.Tabs.Select(tab => tab.Panel).OfType<SystemGroupViewModel>().FirstOrDefault();
        if (system?.Actions is { } actions)
        {
            items.Add(new CommandItem("Screenshot", "Action", "\uE722", "", CommandKind.Action,
                () => RunAndTuck(() => actions.ScreenshotCommand.Execute(null))));
            items.Add(new CommandItem(actions.MicLabel, "Action", "\uE720", "", CommandKind.Action,
                () => RunAndTuck(() => actions.ToggleMicCommand.Execute(null))));
            items.Add(new CommandItem("Lock the PC", "Action", "\uE72E", "", CommandKind.Action,
                () => RunAndTuck(() => actions.LockCommand.Execute(null))));
            items.Add(new CommandItem("Turn off the display", "Action", "\uE7E8", "", CommandKind.Action,
                () => RunAndTuck(() => actions.ScreenOffCommand.Execute(null))));
            items.Add(new CommandItem("Open Downloads", "Action", "\uE896", "", CommandKind.Action,
                () => RunAndTuck(() => actions.DownloadsCommand.Execute(null))));
        }

        var clock = _modules.Tabs.Select(tab => tab.Panel).OfType<ClockPanelViewModel>().FirstOrDefault();
        if (clock is not null)
        {
            items.Add(new CommandItem("Start a 5 minute timer", "Timer", "\uE916", "", CommandKind.Timer,
                () => RunAndTuck(() => clock.Timer.StartCountdownCommand.Execute(5))));
            items.Add(new CommandItem("Start a 15 minute timer", "Timer", "\uE916", "", CommandKind.Timer,
                () => RunAndTuck(() => clock.Timer.StartCountdownCommand.Execute(15))));
            items.Add(new CommandItem("Start a focus session", "Timer", "\uE916", "", CommandKind.Timer,
                () => RunAndTuck(() => clock.Timer.StartPomodoroCommand.Execute(null))));
            items.Add(new CommandItem("Start the stopwatch", "Timer", "\uE916", "", CommandKind.Timer,
                () => RunAndTuck(() => clock.Timer.StartStopwatchCommand.Execute(null))));
            if (clock.Timer.IsActive)
                items.Add(new CommandItem("Stop the timer", "Timer", "\uE916", "", CommandKind.Timer,
                    () => RunAndTuck(() => clock.Timer.ResetCommand.Execute(null))));
        }

        if (FocusService.Current is { } focus)
        {
            items.Add(new CommandItem("Do Not Disturb for 30 minutes", "Focus", "\uE708", "", CommandKind.Action,
                () => RunAndTuck(() => focus.FocusFor(TimeSpan.FromMinutes(30)))));
            items.Add(new CommandItem("Do Not Disturb for 1 hour", "Focus", "\uE708", "", CommandKind.Action,
                () => RunAndTuck(() => focus.FocusFor(TimeSpan.FromHours(1)))));
            items.Add(new CommandItem("Do Not Disturb for 2 hours", "Focus", "\uE708", "", CommandKind.Action,
                () => RunAndTuck(() => focus.FocusFor(TimeSpan.FromHours(2)))));
            items.Add(new CommandItem("Do Not Disturb until tomorrow", "Focus", "\uE708", "", CommandKind.Action,
                () => RunAndTuck(() => focus.FocusUntil(new DateTimeOffset(DateTime.Today.AddDays(1))))));
            if (focus.IsActive)
                items.Add(new CommandItem("Turn off Do Not Disturb", "Focus", "\uE708", "", CommandKind.Action,
                    () => RunAndTuck(focus.Clear)));
        }

        items.Add(new CommandItem("Open Settings", "Action", "\uE713", "", CommandKind.Action,
            () => RunAndTuck(() => _openSettings(0))));

        var launcher = _modules.Tabs.Select(tab => tab.Panel).OfType<LauncherPanelViewModel>().FirstOrDefault();
        if (launcher?.Apps is { } apps)
        {
            foreach (var app in apps.Apps)
            {
                var target = app;
                items.Add(new CommandItem(app.Name, app.Source, "", app.Initial, CommandKind.App,
                    () => RunAndTuck(() => apps.LaunchCommand.Execute(target))));
            }
        }

        return items;
    }

    /// <summary>
    /// Bookmarked tabs sit in the full-width top row. Enabled tabs that are not bookmarked live in the
    /// ⋯ menu. Features switched off in Settings appear in neither place. A tab opened from the menu
    /// joins the top row for this visit and leaves when another tab is picked or the island collapses.
    /// </summary>
    private void RefreshTabStrip()
    {
        if (_refreshingTabs) return;
        _refreshingTabs = true;
        try
        {
            if (_transientTab is not null && !_modules.Tabs.Contains(_transientTab)) _transientTab = null;

            var visible = _modules.Tabs
                .Where(tab => _modules.IsBookmarked(tab) && !ReferenceEquals(tab, _transientTab))
                .ToList();
            // A tab opened from the ⋯ menu joins the end of the row, so it never looks starred in its old slot.
            if (_transientTab is not null) visible.Add(_transientTab);
            var menu = _modules.Tabs
                .Where(tab => !_modules.IsBookmarked(tab) && !ReferenceEquals(tab, _transientTab))
                .ToList();

            if (!_visibleTabs.SequenceEqual(visible))
            {
                var selected = Tabs.SelectedItem as ModuleTab;
                _visibleTabs.Clear();
                foreach (var tab in visible) _visibleTabs.Add(tab);
                if (selected is not null && visible.Contains(selected)) Tabs.SelectedItem = selected;
            }
            if (!_menuTabs.SequenceEqual(menu))
            {
                _menuTabs.Clear();
                foreach (var tab in menu) _menuTabs.Add(tab);
                TabMenu.SelectedItem = null;
            }

            var hasMenu = menu.Count > 0;
            TabMenuButton.Visibility = hasMenu ? Visibility.Visible : Visibility.Collapsed;
            if (!hasMenu) TabMenuPopup.IsOpen = false;

            if (Tabs.SelectedItem is null && _visibleTabs.Count > 0) Tabs.SelectedIndex = 0;
            ShowSelectedTab();
            _headerWidth = 0;
            if (_mode == IslandMode.Expanded) Render();
        }
        finally
        {
            _refreshingTabs = false;
        }
    }

    /// <summary>Moves between tabs. The wheel stops at the ends; Ctrl+Tab wraps around, as in browsers.</summary>
    internal void StepTab(int delta, bool wrap)
    {
        var count = Tabs.Items.Count;
        if (count == 0 || _mode != IslandMode.Expanded) return;
        var index = Tabs.SelectedIndex + delta;
        Tabs.SelectedIndex = wrap ? (index % count + count) % count : Math.Clamp(index, 0, count - 1);
        if (Tabs.IsKeyboardFocusWithin) FocusSelectedTab();
    }

    private void OnBluetoothClick(object sender, RoutedEventArgs e) => BluetoothPopup.IsOpen = !BluetoothPopup.IsOpen;

    private void OnTabMenuClick(object sender, RoutedEventArgs e) => TabMenuPopup.IsOpen = !TabMenuPopup.IsOpen;

    private void OnTabMenuOpened(object? sender, EventArgs e)
    {
        TabMenu.SelectedItem = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => TabMenu.Focus()));
    }

    private void OnTabMenuClickItem(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (TabMenu.ContainerFromElement(source) is not ListBoxItem { Content: ModuleTab tab }) return;
        OpenFromMenu(tab);
        e.Handled = true;
    }

    private void OnTabMenuBookmark(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModuleTab tab }) return;
        _modules.Bookmark(tab);
        OpenFromMenu(tab);
        e.Handled = true;
    }

    private void OnTabMenuKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key is Key.Enter or Key.Space) && TabMenu.SelectedItem is ModuleTab tab)
        {
            OpenFromMenu(tab);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TabMenuPopup.IsOpen = false;
            TabMenuButton.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Opens a menu tab without pinning it; the tab leaves the top row again on the next visit.</summary>
    internal void OpenFromMenu(ModuleTab tab)
    {
        TabMenuPopup.IsOpen = false;
        _transientTab = tab;
        RefreshTabStrip();
        Tabs.SelectedItem = tab;
        Tabs.ScrollIntoView(tab);
        ShowSelectedTab();
        if (_mode == IslandMode.Expanded)
        {
            Render();
            FocusSelectedTab();
        }
        TabMenu.SelectedItem = null;
    }

    private void OnHeaderWheel(object sender, MouseWheelEventArgs e)
    {
        // Precision touchpads send many small deltas; act once per full notch (120).
        if (Math.Sign(e.Delta) != Math.Sign(_wheelDelta)) _wheelDelta = 0;
        _wheelDelta += e.Delta;
        while (Math.Abs(_wheelDelta) >= Mouse.MouseWheelDeltaForOneLine)
        {
            var notch = Math.Sign(_wheelDelta);
            _wheelDelta -= notch * Mouse.MouseWheelDeltaForOneLine;
            StepTab(-notch, wrap: false); // wheel down = next tab
        }
        e.Handled = true;
    }

    /// <summary>Called by the manager whenever pause or the foreground app's state may have changed.</summary>
    /// <param name="foreground">Null while one of ATOLL's own windows is in front: keep the last answer.</param>
    public void SetEnvironment(bool paused, ForegroundKind? foreground)
    {
        _paused = paused;
        if (foreground is { } kind && kind != _foreground)
        {
            Log.Info($"Foreground on {Monitor.DeviceName}: {kind}");
            _foreground = kind;
        }
        if (paused || ShownWithoutReveal())
        {
            _revealed = false;
            _edgeHoverSince = null;
        }
        UpdatePresence();
    }

    internal void CheckFullscreenEdge(Point cursor, TimeSpan now)
    {
        if (_paused || ShownWithoutReveal())
        {
            ResetHold();
            return;
        }
        if (_revealed)
        {
            if (!IsCursorOverIsland(cursor) && _mode == IslandMode.Compact)
            {
                _revealed = false;
                ResetHold();
                UpdatePresence();
            }
            return;
        }
        if (!IsTopEdge(cursor)) { ResetHold(); return; }
        _edgeHoverSince ??= now;
        var hold = TimeSpan.FromMilliseconds(Math.Clamp(_settings.Current.EdgeHoldMs, 250, 10000));
        var progress = (now - _edgeHoverSince.Value) / hold;
        if (progress < 1)
        {
            // The line is as wide as the open island (the hold area) and fills from the center, where the island sits.
            var (left, width) = HoldZone();
            (_hint ??= new RevealHint()).Update((int)Math.Round(left), Monitor.Bounds.Y, (int)Math.Round(width), Monitor.Scale, progress);
            return;
        }
        ResetHold();
        _revealed = true;
        UpdatePresence();
        SetClickThrough(false);
        SetMode(IslandMode.Expanded); // open fully: no click needed after the hold
    }

    private void ResetHold()
    {
        _edgeHoverSince = null;
        _hint?.Clear();
    }

    /// <summary>
    /// Where a hold brings the island back, in physical pixels: the open island's width (at least 400 px), centered,
    /// and the top 12 px of the screen. The hold line covers the same width.
    /// </summary>
    private (double Left, double Width) HoldZone()
    {
        var center = Monitor.Bounds.X + Monitor.Bounds.Width / 2.0;
        var half = Math.Max(200, Math.Clamp(_settings.Current.Theme.ExpandedWidth, 320, 900) / 2) * Monitor.Scale;
        return (center - half, 2 * half);
    }

    private bool IsTopEdge(Point cursor)
    {
        var (left, width) = HoldZone();
        return cursor.X >= left && cursor.X <= left + width
            && cursor.Y >= Monitor.Bounds.Y && cursor.Y <= Monitor.Bounds.Y + 12 * Monitor.Scale;
    }

    public void EnsureTopmost()
    {
        if (_hwnd != 0)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>The visibility setting that applies to what is in front of the island.</summary>
    internal static FullscreenMode ModeFor(ForegroundKind foreground, AppSettings settings) => foreground switch
    {
        // Overlay: the game pill stays up. Hidden: only an intentional edge hold or the open shortcut reveals it.
        ForegroundKind.Game => settings.Games.Mode == GameIslandMode.Overlay ? FullscreenMode.Stay : FullscreenMode.Hide,
        ForegroundKind.Fullscreen => settings.FullscreenMode,
        ForegroundKind.Maximized => settings.MaximizedMode,
        _ => FullscreenMode.Stay,
    };

    /// <summary>
    /// Whether the island shows without an edge hold. PopUp shows only things that end on their own (alerts,
    /// notifications, short indicators). Hide-when-idle hides an island that only has the clock unless it is open.
    /// </summary>
    internal static bool ShownWithoutReveal(FullscreenMode mode, bool hideWhenIdle, Activity? primary, bool open) => mode switch
    {
        FullscreenMode.Hide => false,
        FullscreenMode.PopUp => primary is { Alert: true } || primary?.Duration is not null,
        _ => open || !hideWhenIdle || primary is { Priority: > ActivityPriority.Idle },
    };

    private bool ShownWithoutReveal() => ShownWithoutReveal(ModeFor(_foreground, _settings.Current),
        _settings.Current.HideWhenIdle, _primary, _mode != IslandMode.Compact);

    /// <summary>Shows, hides or dims the window for pause and the visibility settings.</summary>
    private void UpdatePresence()
    {
        var mode = ModeFor(_foreground, _settings.Current);
        var shown = !_paused && (_revealed || ShownWithoutReveal());
        Opacity = mode == FullscreenMode.Dim ? 0.5 : 1;

        // Watch the top edge only while there is something to bring back (or a revealed island to put away).
        if (!_paused && (_revealed || !ShownWithoutReveal())) _edgeTimer.Start();
        else { _edgeTimer.Stop(); ResetHold(); }

        if (shown == IsVisible) return;
        if (shown)
        {
            Show();
            EnsureTopmost();
        }
        else
        {
            if (_mode == IslandMode.Expanded) SetMode(IslandMode.Compact);
            Hide();
        }
    }

    /// <summary>Centers the window horizontally at the top of its monitor's work area (physical pixels).</summary>
    private void Place()
    {
        if (_hwnd == 0) return;
        var m = Monitor;
        var w = (int)Math.Round(Width * m.Scale);
        var h = (int)Math.Round(Height * m.Scale);
        var x = m.Bounds.X + (m.Bounds.Width - w) / 2;
        var y = m.WorkArea.Y; // below a top-docked taskbar, else the screen edge
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
    }

    private void SetMode(IslandMode mode, string? moduleId = null)
    {
        TabMenuPopup.IsOpen = false;
        BluetoothPopup.IsOpen = false;
        // A tab opened from the ⋯ menu only stays in the top row for its visit.
        if (mode != IslandMode.Expanded && _transientTab is not null)
        {
            _transientTab = null;
            RefreshTabStrip();
        }
        if (mode != IslandMode.Expanded && _paletteOpen)
        {
            _paletteOpen = false;
            Palette.Visibility = Visibility.Collapsed;
            PanelBody.Visibility = Visibility.Visible;
        }
        // Keep the original hover region while the pill grows and pushes side activities away.
        _peekAnchor = mode == IslandMode.Peek && _mode != IslandMode.Peek ? IslandBounds() : Rect.Empty;
        if (mode == IslandMode.Expanded)
        {
            SetClickThrough(false);
            SetNoActivate(false);
            Activate();
            var tab = moduleId is null ? null : _modules.Tabs.FirstOrDefault(t => t.Contains(moduleId));
            if (tab is not null)
            {
                if (!_modules.IsBookmarked(tab) && !ReferenceEquals(_transientTab, tab))
                {
                    _transientTab = tab;
                    RefreshTabStrip();
                }
                Tabs.SelectedItem = tab;
                // Opening from a game activity should land on the Launcher's Games section.
                if (tab.Panel is LauncherPanelViewModel launcher && moduleId is "games" or "launcher")
                    launcher.SectionIndex = moduleId == "games" ? 1 : 0;
            }
            else if (Tabs.SelectedItem is null && Tabs.Items.Count > 0) Tabs.SelectedIndex = 0;
            ShowSelectedTab();
        }
        else if (_mode == IslandMode.Expanded)
        {
            SetNoActivate(true);
            // Give focus back only if it is still ours: clicking another app already moved it there.
            if (_returnFocusTo != 0 && Native.GetForegroundWindow() == _hwnd) Native.SetForegroundWindow(_returnFocusTo);
            _returnFocusTo = 0;
        }
        _alertPeek = false;
        _heldOpen = false;
        _mode = mode;
        _outsideMs = 0;
        _dwellMs = 0;
        SetFaded(false);
        Render();
    }

    private void SetNoActivate(bool on) => SetExStyle(Native.WS_EX_NOACTIVATE, on);

    /// <summary>WS_EX_TRANSPARENT makes the whole window invisible to the mouse.</summary>
    private void SetClickThrough(bool on)
    {
        if (_clickThroughApplied == on) return;
        _clickThroughApplied = on;
        SetExStyle(Native.WS_EX_TRANSPARENT, on);
    }

    private void SetExStyle(long flag, bool on)
    {
        if (_hwnd == 0) return;
        var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex = on ? ex | flag : ex & ~flag;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (nint)ex);
    }

    // ---------- pointer ----------

    private void OnPointerTick()
    {
        if (Native.GetCursorPos(out var p)) OnPointerTick(new Point(p.X, p.Y));
    }

    internal void OnPointerTick(Point screen)
    {
        var over = IsCursorOverIsland(screen);
        switch (_mode)
        {
            case IslandMode.Compact:
                SetClickThrough(ClickThrough);
                if (!over)
                {
                    _dwellMs = 0;
                    SetFaded(false);
                    return;
                }
                if (ClickThrough) SetFaded(true); // see (and click) what is underneath while passing over
                _dwellMs += PointerTickMs;
                var dwell = Math.Clamp(_settings.Current.HoverDelayMs, 0, 2000);
                if (ClickThrough) dwell = Math.Max(dwell, MinClickThroughDwellMs);
                if (_dwellMs >= dwell && _primary is not null) SetMode(IslandMode.Peek);
                break;

            case IslandMode.Peek:
                SetClickThrough(false);
                if (over) _outsideMs = 0;
                else if (!_alertPeek && (_outsideMs += PointerTickMs) >= PeekCollapseAfterMs) SetMode(IslandMode.Compact);
                break;

            case IslandMode.Expanded:
                // An open dropdown list may extend past the island; keep the island open while it is in use.
                if (over || Mouse.Captured is ComboBox { IsDropDownOpen: true })
                {
                    _outsideMs = 0;
                    _heldOpen = false;
                }
                else if (!_heldOpen && (_outsideMs += PointerTickMs) >= CollapseAfterMs)
                {
                    SetMode(IslandMode.Compact);
                }
                break;
        }
    }

    private void SetFaded(bool faded)
    {
        if (_faded == faded) return;
        _faded = faded;
        var to = faded ? PassThroughOpacity : 1;
        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(to, FadeDuration));
        Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(to, FadeDuration));
        Bubble2.BeginAnimation(OpacityProperty, new DoubleAnimation(to, FadeDuration));
        Bubble3.BeginAnimation(OpacityProperty, new DoubleAnimation(to, FadeDuration));
        Bubble4.BeginAnimation(OpacityProperty, new DoubleAnimation(to, FadeDuration));
    }

    private bool IsCursorOverIsland()
    {
        if (!IsVisible || !Native.GetCursorPos(out var p)) return false;
        return IsCursorOverIsland(new Point(p.X, p.Y));
    }

    internal bool IsCursorOverIsland(Point screen) => IsVisible &&
        (IslandBounds().Contains(screen) || (_mode == IslandMode.Peek && _peekAnchor.Contains(screen))
            || (_revealed && IsTopEdge(screen)));

    private Rect IslandBounds()
    {
        if (!IsVisible) return Rect.Empty;
        static Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()),
            element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
        var bounds = Bounds(Pill);
        foreach (var bubble in new[] { Bubble, Bubble2, Bubble3, Bubble4 }.Where(b => b.IsVisible))
            bounds.Union(Bounds(bubble));
        return bounds;
    }

    // ---------- frosted glass ----------

    /// <summary>Hides every island from screenshots and screen shares while a private feature runs.</summary>
    public void SetCaptureHidden(bool hidden)
    {
        _captureHidden = hidden;
        ApplyCapturePrivacy();
    }

    private void ApplyCapturePrivacy()
    {
        if (_hwnd == 0) return;
        GlassSampler.ExcludeFromCapture(_hwnd, _captureHidden || _glassActive && !_systemGlass);
    }

    /// <summary>Starts or stops the frosted backdrop to match the theme and visibility.</summary>
    private void UpdateGlass()
    {
        var wanted = ThemeService.GlassTint is not null && IsVisible && _hwnd != 0;
        if (wanted == _glassActive) return;
        _glassActive = wanted;
        if (wanted)
        {
            _systemGlassTried = false;
            _systemGlass = TrySystemGlass();
            if (_systemGlass)
            {
                Log.Info("Frosted glass: using the Windows blur behind the island");
                ApplyCapturePrivacy();
                ResetGlassBackgrounds();
                _glassKey = "";
                UpdateSystemBackdrop();
            }
            else
            {
                if (!GlassSampler.ExcludeFromCapture(_hwnd, true))
                    Log.Info("Frosted glass: this Windows build cannot exclude the island from capture; the glass may show the island itself");
                SampleGlass();
                _glassTimer.Start();
            }
        }
        else
        {
            _glassTimer.Stop();
            _backdrop.Disable(_hwnd);
            _systemGlass = false;
            _systemGlassTried = false;
            ApplyCapturePrivacy();
            _glassImage = null;
            _pillGlassBrush = null; _bubbleGlassBrush = null;
            _bubble2GlassBrush = null; _bubble3GlassBrush = null; _bubble4GlassBrush = null;
            ResetGlassBackgrounds();
        }
    }

    /// <summary>Follows the pill through every layout frame; the system region or the captured brush must track it.</summary>
    private void UpdateGlassFrame()
    {
        if (!_glassActive) return;
        if (!_systemGlass && !_systemGlassTried && TrySystemGlass())
        {
            // The first attempt ran before layout. Switch to the real blur once the pill has a size.
            Log.Info("Frosted glass: using the Windows blur behind the island");
            _glassTimer.Stop();
            ApplyCapturePrivacy();
            ResetGlassBackgrounds();
            _glassKey = "";
            UpdateSystemBackdrop();
            return;
        }
        if (_systemGlass) UpdateSystemBackdrop();
        else ApplyGlassBrushes();
    }

    private bool TrySystemGlass()
    {
        if (_hwnd == 0 || Pill.ActualWidth <= 0 || Pill.ActualHeight <= 0) return false;
        _systemGlassTried = true;
        var enabled = _backdrop.Enable(_hwnd, GlassShapes());
        if (!enabled) Log.Info("Frosted glass: Windows blur unavailable; using the captured blur");
        return enabled;
    }

    private void ResetGlassBackgrounds()
    {
        Pill.SetResourceReference(BackgroundProperty, "Island.Background");
        Bubble.SetResourceReference(BackgroundProperty, "Island.Background");
        Bubble2.SetResourceReference(BackgroundProperty, "Island.Background");
        Bubble3.SetResourceReference(BackgroundProperty, "Island.Background");
        Bubble4.SetResourceReference(BackgroundProperty, "Island.Background");
    }

    private void UpdateSystemBackdrop()
    {
        if (!_systemGlass || _hwnd == 0) return;
        var shapes = GlassShapes();
        var key = string.Join(";", shapes.Select(s =>
            $"{s.Bounds.X:0},{s.Bounds.Y:0},{s.Bounds.Width:0},{s.Bounds.Height:0},{s.Radius:0},{s.Notch}"));
        if (key == _glassKey) return;
        if (_backdrop.Enable(_hwnd, shapes)) _glassKey = key;
    }

    /// <summary>The pill and visible side bubbles, in window pixels, for the blur region.</summary>
    private IReadOnlyList<SystemBackdrop.Shape> GlassShapes()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var shapes = new List<SystemBackdrop.Shape>(5);
        Add(Pill);
        foreach (var bubble in new[] { Bubble, Bubble2, Bubble3, Bubble4 }.Where(b => b.IsVisible)) Add(bubble);
        return shapes;

        void Add(PillBorder element)
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var origin = element.TranslatePoint(new Point(), this);
            shapes.Add(new SystemBackdrop.Shape(
                new Rect(origin.X * dpi.DpiScaleX, origin.Y * dpi.DpiScaleY,
                    element.ActualWidth * dpi.DpiScaleX, element.ActualHeight * dpi.DpiScaleY),
                element.Radius * dpi.DpiScaleX, element.Notch));
        }
    }

    private void SampleGlass()
    {
        if (!_glassActive || ThemeService.GlassTint is not { } tint || Pill.ActualWidth <= 0 || Pill.ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var pill = new Rect(Pill.PointToScreen(new Point()), new Size(Pill.ActualWidth * dpi.DpiScaleX, Pill.ActualHeight * dpi.DpiScaleY));
        var targetWidth = Math.Max(1, _target.W) * dpi.DpiScaleX;
        var targetHeight = Math.Max(1, _target.H) * dpi.DpiScaleY;
        var center = pill.Left + pill.Width / 2;
        var bounds = Rect.Union(pill, new Rect(center - targetWidth / 2, pill.Top, targetWidth, targetHeight));
        foreach (var side in new[] { Bubble, Bubble2, Bubble3, Bubble4 }.Where(side => side.IsVisible))
        {
            var size = new Size(side.ActualWidth * dpi.DpiScaleX, side.ActualHeight * dpi.DpiScaleY);
            bounds.Union(new Rect(side.PointToScreen(new Point()), size));
        }
        // Cover where two side capsules on each side can sit once the pill reaches its target width.
        var margin = 2 * (SideChipWidth + SideChipGap + 8) * dpi.DpiScaleX;
        bounds.Union(new Rect(center - targetWidth / 2 - margin, pill.Top,
            targetWidth + 2 * margin, Math.Max(pill.Height, targetHeight)));
        var moving = Math.Abs(Pill.ActualWidth - _target.W) > 1 || Math.Abs(Pill.ActualHeight - _target.H) > 1;
        bounds.Inflate((moving ? 40 : 8) * dpi.DpiScaleX, (moving ? 40 : 8) * dpi.DpiScaleY);
        var area = GlassSampler.CaptureBounds(bounds);
        var frame = GlassSampler.Capture(area, tint);
        if (frame is null) return;
        _glassArea = area; _glassImage = frame;
        ApplyGlassBrushes();
    }

    /// <summary>Paints the part of the sampled image that lies under the pill and bubble (Border clips it to the rounded shape).</summary>
    private void ApplyGlassBrushes()
    {
        if (!_glassActive || _glassImage is null || ActualWidth <= 0) return;
        Pill.Background = _pillGlassBrush = GlassBrush(Pill, _pillGlassBrush);
        if (Bubble.IsVisible) Bubble.Background = _bubbleGlassBrush = GlassBrush(Bubble, _bubbleGlassBrush);
        if (Bubble2.IsVisible) Bubble2.Background = _bubble2GlassBrush = GlassBrush(Bubble2, _bubble2GlassBrush);
        if (Bubble3.IsVisible) Bubble3.Background = _bubble3GlassBrush = GlassBrush(Bubble3, _bubble3GlassBrush);
        if (Bubble4.IsVisible) Bubble4.Background = _bubble4GlassBrush = GlassBrush(Bubble4, _bubble4GlassBrush);
    }

    private ImageBrush GlassBrush(FrameworkElement e, ImageBrush? previous)
    {
        var sx = _glassImage!.PixelWidth / (double)_glassArea.Width;
        var sy = _glassImage.PixelHeight / (double)_glassArea.Height;
        var origin = e.PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(e);
        var viewbox = new Rect((origin.X - _glassArea.X) * sx, (origin.Y - _glassArea.Y) * sy,
            Math.Max(1, e.ActualWidth * dpi.DpiScaleX * sx), Math.Max(1, e.ActualHeight * dpi.DpiScaleY * sy));
        if (previous is not null && ReferenceEquals(previous.ImageSource, _glassImage) && previous.Viewbox == viewbox) return previous;
        var brush = new ImageBrush(_glassImage)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = viewbox,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality); // smooth upscale = extra blur
        brush.Freeze();
        return brush;
    }

    // ---------- rendering ----------

    /// <summary>The panel height that shows all of the selected tab's content without scrolling or clipping.</summary>
    private double _headerWidth;

    /// <summary>
    /// Width the open island needs for every tab and status chip. Remembers the widest seen (the selected tab's name
    /// changes it), so switching tabs never makes the island jump narrower; it resets when tabs are added or removed.
    /// </summary>
    private double HeaderWidth()
    {
        Header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var needed = Header.DesiredSize.Width + PanelRoot.Margin.Left + PanelRoot.Margin.Right + Pill.BorderThickness.Left + Pill.BorderThickness.Right + 2;
        _headerWidth = Math.Max(_headerWidth, needed);
        return Math.Min(_headerWidth, Monitor.WorkArea.Width / Monitor.Scale - 32);
    }

    private double FittedPanelHeight(double expandedWidth)
    {
        var border = Pill.BorderThickness;
        PanelRoot.Visibility = Visibility.Visible; // collapsed elements measure as zero
        PanelRoot.Width = expandedWidth - PanelRoot.Margin.Left - PanelRoot.Margin.Right - border.Left - border.Right;
        PanelRoot.Height = double.NaN;
        PanelRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return PanelRoot.DesiredSize.Height + border.Top + border.Bottom;
    }

    private void Render()
    {
        var t = _settings.Current.Theme;
        var h0 = CompactHeight;

        if (_mode == IslandMode.Expanded)
        {
            var expandedWidth = Math.Max(Math.Clamp(t.ExpandedWidth, 320, 900), HeaderWidth()); // every tab fits without scrolling
            var expandedHeight = Math.Clamp(t.ExpandedHeight, 140, 600);
            if (Tabs.SelectedItem is ModuleTab { Panel: MediaPanelViewModel or SystemGroupViewModel or Atoll.Modules.Weather.WeatherViewModel or Atoll.Modules.Home.HomeViewModel or Atoll.Modules.Games.GameViewModel or Atoll.Modules.Calendar.CalendarViewModel or Atoll.Modules.Notes.NotesViewModel or LauncherPanelViewModel or ClockPanelViewModel or Atoll.Modules.Tide.TideViewModel })
                expandedHeight = Math.Max(expandedHeight, FittedPanelHeight(expandedWidth)); // show all of it
            expandedHeight = Math.Min(expandedHeight, AvailablePanelHeight);
            // Lay out at the destination size, so opening the pill cannot create transient overflow.
            PanelRoot.Width = expandedWidth - PanelRoot.Margin.Left - PanelRoot.Margin.Right - Pill.BorderThickness.Left - Pill.BorderThickness.Right;
            PanelRoot.Height = expandedHeight - PanelRoot.Margin.Top - PanelRoot.Margin.Bottom - Pill.BorderThickness.Top - Pill.BorderThickness.Bottom;
            PillContent.Visibility = Visibility.Collapsed;
            PanelRoot.Visibility = Visibility.Visible;
            Animate(expandedWidth, expandedHeight, Roundness * 32);
            RenderBubble(Bubble, BubbleContent, null, h0);
            RenderBubble(Bubble2, Bubble2Content, null, h0);
            RenderBubble(Bubble3, Bubble3Content, null, h0);
            RenderBubble(Bubble4, Bubble4Content, null, h0);
            return;
        }

        PanelRoot.Visibility = Visibility.Collapsed;
        PillContent.Visibility = Visibility.Visible;

        var content = _primary?.Content;
        var templateMode = _mode == IslandMode.Peek ? "Peek" : "Compact";
        if (!ReferenceEquals(content, _shownContent) || templateMode != _shownTemplateMode)
        {
            var isNewContent = !ReferenceEquals(content, _shownContent);
            PillContent.Content = null; // force the presenter to apply the new template
            PillContent.ContentTemplate = content is null ? null : Templates.Find(this, content, templateMode);
            PillContent.Content = content;
            _shownContent = content;
            _shownTemplateMode = templateMode;
            Watch(content);
            if (isNewContent && Animated)
                PillContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        PillContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = PillContent.DesiredSize;
        double w, h;
        if (_mode == IslandMode.Peek)
        {
            h = Math.Max(h0 * 1.5, desired.Height + 20);
            w = Math.Max(h0 * 4, desired.Width + 40);
        }
        else
        {
            h = h0;
            w = Math.Max(Math.Clamp(t.CompactWidth, 60, 400), desired.Width + h0);
        }
        Animate(w, h, Roundness * Math.Min(h / 2, 28));
        var sides = t.ShowSideActivities;
        RenderBubble(Bubble, BubbleContent, sides ? _sideChips[0] : null, h0);
        RenderBubble(Bubble2, Bubble2Content, sides ? _sideChips[1] : null, h0);
        RenderBubble(Bubble3, Bubble3Content, sides ? _sideChips[2] : null, h0);
        RenderBubble(Bubble4, Bubble4Content, sides ? _sideChips[3] : null, h0);
        UpdateBubbleOffset();
    }

    private void RenderBubble(PillBorder bubble, ContentPresenter presenter, object? content, double size)
    {
        if (content is null)
        {
            bubble.Visibility = Visibility.Collapsed;
            presenter.Content = null;
            AutomationProperties.SetName(bubble, "");
            return;
        }
        if (!ReferenceEquals(presenter.Content, content))
        {
            presenter.Content = null;
            presenter.ContentTemplate = Templates.Find(this, content, "Mini");
            presenter.Content = content;
        }
        var chip = content as SideChipViewModel;
        var ring = chip?.IsRing == true;
        var height = ring ? Math.Max(size, 34) : size;
        var wide = _sideActivities.Count >= 3;
        bubble.Height = height;
        bubble.Width = ring ? height : wide ? SideChipWidth : size;
        bubble.ClipToBounds = true;
        bubble.Radius = Roundness * height / 2;
        bubble.Visibility = Visibility.Visible;
        AutomationProperties.SetName(bubble, chip?.Label ?? "");
    }

    private void UpdateBubbleOffset()
    {
        var center = Math.Max(0, Pill.ActualWidth / 2);
        var right = center;
        if (Bubble.IsVisible)
        {
            BubbleShift.X = right + SideChipGap + Bubble.Width / 2;
            right = BubbleShift.X + Bubble.Width / 2;
        }
        if (Bubble3.IsVisible) Bubble3Shift.X = right + SideChipGap + Bubble3.Width / 2;

        var left = center;
        if (Bubble2.IsVisible)
        {
            Bubble2Shift.X = -(left + SideChipGap + Bubble2.Width / 2);
            left += SideChipGap + Bubble2.Width;
        }
        if (Bubble4.IsVisible) Bubble4Shift.X = -(left + SideChipGap + Bubble4.Width / 2);
    }

    private void Animate(double w, double h, double r)
    {
        if (Math.Abs(w - _target.W) < 0.5 && Math.Abs(h - _target.H) < 0.5 && Math.Abs(r - _target.R) < 0.5) return;
        _target = (w, h, r);
        if (!Animated)
        {
            Pill.BeginAnimation(WidthProperty, null); Pill.BeginAnimation(HeightProperty, null); Pill.BeginAnimation(PillBorder.RadiusProperty, null);
            Pill.Width = w; Pill.Height = h; Pill.Radius = r;
            return;
        }
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, MorphDuration) { EasingFunction = Ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, MorphDuration) { EasingFunction = Ease });
        Pill.BeginAnimation(PillBorder.RadiusProperty, new DoubleAnimation(r, MorphDuration) { EasingFunction = Ease });
    }

    /// <summary>Re-measure when the shown view model changes (e.g. clock text gets wider).</summary>
    private void Watch(object? content)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnContentPropertyChanged;
        _watched = content as INotifyPropertyChanged;
        if (_watched is not null) _watched.PropertyChanged += OnContentPropertyChanged;
    }

    private void OnContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _renderQueued = false;
            if (_mode != IslandMode.Expanded) Render();
        });
    }

    private void WatchPanel(object? panel)
    {
        if (ReferenceEquals(_watchedPanel, panel)) return;
        if (_watchedPanel is not null) _watchedPanel.PropertyChanged -= OnPanelPropertyChanged;
        _watchedPanel = panel as INotifyPropertyChanged;
        if (_watchedPanel is not null) _watchedPanel.PropertyChanged += OnPanelPropertyChanged;
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Notes, Launcher, Timer and similar panels change height when their section changes.
        if (e.PropertyName is not (nameof(LauncherPanelViewModel.SectionIndex) or nameof(TimerViewModel.SectionIndex) or nameof(Atoll.Modules.Notes.NotesViewModel.SectionIndex) or nameof(Atoll.Modules.Tide.TideViewModel.SectionIndex))) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_mode == IslandMode.Expanded) Render();
        }));
    }

    private void ShowSelectedTab()
    {
        if (Tabs.SelectedItem is not ModuleTab tab)
        {
            if (Tabs.Items.Count > 0) Tabs.SelectedIndex = 0; // raises SelectionChanged again
            else { WatchPanel(null); PanelBody.Content = null; }
            return;
        }
        WatchPanel(tab.Panel);
        if (ReferenceEquals(PanelBody.Content, tab.Panel)) return;
        PanelBody.Content = null;
        PanelBody.ContentTemplate = Templates.Find(this, tab.Panel, "Panel");
        PanelBody.Content = tab.Panel;
    }

    private void OpenSide(int index, MouseButtonEventArgs e)
    {
        if (_sideChips[index] is { } chip) SetMode(IslandMode.Expanded, ActivityTab(chip.Activity));
        e.Handled = true;
    }

    /// <summary>
    /// The tab that belongs to what the compact pill is showing. The idle clock is the resting state, not a
    /// request for the timer, so it opens Home; a running countdown still opens the Timer tab.
    /// </summary>
    private string? ActivityTab(Activity? activity) =>
        ActivityTabFor(activity, _modules.Tabs.Any(t => t.Contains("media")));

    internal static string? ActivityTabFor(Activity? activity, bool hasMediaTab)
    {
        if (activity is { Priority: ActivityPriority.Idle } && activity.ModuleId == "clock") return "home";
        if (activity?.Content is IndicatorViewModel { Detail: "Volume" } && hasMediaTab) return "media";
        return activity?.ModuleId;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SetMode(IslandMode.Compact);
        _openSettings(SettingsPageFor((Tabs.SelectedItem as ModuleTab)?.Descriptor.Id));
    }

    /// <summary>The header's one settings button opens the page for the tab being viewed (General when there is none).</summary>
    internal static int SettingsPageFor(string? tabId) => tabId switch
    {
        "notifications" => 3,
        "claude" or "providers" => 4,
        "calendar" => 5,
        "weather" => 6,
        "clock" or "launcher" or "shelf" or "clipboard" or "notes" or "tide" => 2,
        _ => 0,
    };

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
