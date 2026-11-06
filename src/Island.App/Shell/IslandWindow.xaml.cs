using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Island.Activities;
using Island.Modules;
using Island.Settings;

namespace Island.Shell;

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
    private static readonly Duration MorphDuration = TimeSpan.FromMilliseconds(520);

    private readonly SettingsService _settings;
    private readonly ModuleManager _modules;
    private readonly Action _openSettings;
    private const int HoverWatchMs = 80;
    private const int CollapseAfterMs = 900;

    private readonly DispatcherTimer _hoverTimer;

    // Layered WPF windows raise spurious MouseLeave events, so "has the cursor left?" is answered
    // by polling the real cursor position while the island is hovered or open.
    private readonly DispatcherTimer _hoverWatch;
    private int _outsideMs;
    private bool _alertPeek;

    private nint _hwnd;
    private IslandMode _mode = IslandMode.Compact;
    private Activity? _primary;
    private Activity? _secondary;
    private object? _shownContent;
    private string? _shownTemplateMode;
    private INotifyPropertyChanged? _watched;
    private bool _renderQueued;
    private (double W, double H, double R) _target = (-1, -1, -1);

    public IslandWindow(MonitorInfo monitor, SettingsService settings, ModuleManager modules, Action openSettings)
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

        _hoverTimer = new DispatcherTimer();
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_mode == IslandMode.Compact && _primary is not null && IsCursorOverPill()) SetMode(IslandMode.Peek);
        };
        _hoverWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverWatchMs) };
        _hoverWatch.Tick += (_, _) => OnHoverWatch();

        Tabs.ItemsSource = modules.Tabs;
        Tabs.SelectionChanged += (_, _) => ShowSelectedTab();

        Pill.MouseEnter += OnPillEnter;
        Pill.MouseLeftButtonUp += (_, e) =>
        {
            if (_mode == IslandMode.Expanded) return;
            SetMode(IslandMode.Expanded, _primary?.ModuleId);
            e.Handled = true;
        };
        Bubble.MouseLeftButtonUp += (_, e) =>
        {
            SetMode(IslandMode.Expanded, _secondary?.ModuleId);
            e.Handled = true;
        };
        Pill.SizeChanged += (_, _) => UpdateBubbleOffset();

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
            if (e.Key == Key.Escape && _mode == IslandMode.Expanded) SetMode(IslandMode.Compact);
        };
        Closed += (_, _) =>
        {
            Watch(null);
            _hoverTimer.Stop();
            _hoverWatch.Stop();
        };
    }

    public MonitorInfo Monitor { get; }

    private double CompactHeight => Math.Clamp(_settings.Current.Theme.CompactHeight, 24, 60);

    private double Roundness => Math.Clamp(_settings.Current.Theme.Roundness, 0, 1);

    public void SetActivities(Activity? primary, Activity? secondary)
    {
        var isNewPrimary = !ReferenceEquals(primary, _primary);        _primary = primary;
        _secondary = secondary;

        if (isNewPrimary && primary is { Alert: true } && _mode == IslandMode.Compact)
        {
            // Alerts open the island on their own; hovering is not required to keep them open.
            _alertPeek = true;
            _mode = IslandMode.Peek;
        }
        else if (isNewPrimary && _alertPeek)
        {
            _alertPeek = false;
            if (_mode == IslandMode.Peek && !IsCursorOverPill()) _mode = IslandMode.Compact;
        }

        if (_mode == IslandMode.Peek && primary is null) _mode = IslandMode.Compact;
        Render();
    }

    /// <summary>Re-reads size/offset/theme settings.</summary>
    public void ApplySettings()
    {
        var t = _settings.Current.Theme;
        var offset = Math.Clamp(_settings.Current.TopOffset, 0, 80);
        Stage.Margin = new Thickness(0, offset, 0, 0);
        Width = Math.Max(760, Math.Clamp(t.ExpandedWidth, 320, 900) + 2 * (CompactHeight + 24));
        Height = Math.Clamp(t.ExpandedHeight, 140, 600) + offset + 24;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.Current.HoverDelayMs, 0, 2000));
        _target = (-1, -1, -1);
        _shownContent = null; // template choice may depend on size; rebuild
        Place();
        Render();
    }

    public void SetShown(bool shown)
    {
        if (shown == IsVisible) return;
        if (shown)
        {
            Show();
            EnsureTopmost();
        }
        else
        {
            if (_mode != IslandMode.Compact) SetMode(IslandMode.Compact);
            Hide();
        }
    }

    public void EnsureTopmost()
    {
        if (_hwnd != 0)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
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
        if (mode == IslandMode.Expanded)
        {
            _hoverTimer.Stop();
            SetNoActivate(false);
            Activate();
            var tab = moduleId is null ? null : _modules.Tabs.FirstOrDefault(t => t.Descriptor.Id == moduleId);
            if (tab is not null) Tabs.SelectedItem = tab;
            else if (Tabs.SelectedItem is null && Tabs.Items.Count > 0) Tabs.SelectedIndex = 0;
            ShowSelectedTab();
        }
        else if (_mode == IslandMode.Expanded)
        {
            SetNoActivate(true);
        }
        _alertPeek = false;
        _mode = mode;
        _outsideMs = 0;
        if (mode != IslandMode.Compact) _hoverWatch.Start();
        Render();
    }

    private void SetNoActivate(bool on)
    {
        if (_hwnd == 0) return;
        var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex = on ? ex | Native.WS_EX_NOACTIVATE : ex & ~Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (nint)ex);
    }

    private void Render()
    {
        var t = _settings.Current.Theme;
        var h0 = CompactHeight;

        if (_mode == IslandMode.Expanded)
        {
            PillContent.Visibility = Visibility.Collapsed;
            PanelRoot.Visibility = Visibility.Visible;
            Animate(Math.Clamp(t.ExpandedWidth, 320, 900), Math.Clamp(t.ExpandedHeight, 140, 600), Roundness * 32);
            RenderBubble(null, h0);
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
            if (isNewContent)
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
            w = Math.Max(h0 * 3.2, desired.Width + h0);
        }
        Animate(w, h, Roundness * Math.Min(h / 2, 28));
        RenderBubble(_secondary?.Content, h0);
    }

    private void RenderBubble(object? content, double size)
    {
        if (content is null)
        {
            Bubble.Visibility = Visibility.Collapsed;
            BubbleContent.Content = null;
            return;
        }
        if (!ReferenceEquals(BubbleContent.Content, content))
        {
            BubbleContent.Content = null;
            BubbleContent.ContentTemplate = Templates.Find(this, content, "Mini");
            BubbleContent.Content = content;
        }
        BubbleContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Bubble.Height = size;
        Bubble.Width = Math.Max(size, BubbleContent.DesiredSize.Width + size * 0.6); // grows for text like "4:59"
        Bubble.Radius = Roundness * size / 2;
        Bubble.Visibility = Visibility.Visible;
        UpdateBubbleOffset();
    }

    private void UpdateBubbleOffset() => BubbleShift.X = Pill.ActualWidth / 2 + 8 + Bubble.Width / 2;

    private void Animate(double w, double h, double r)
    {
        if (Math.Abs(w - _target.W) < 0.5 && Math.Abs(h - _target.H) < 0.5 && Math.Abs(r - _target.R) < 0.5) return;
        _target = (w, h, r);
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, MorphDuration) { EasingFunction = Spring });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, MorphDuration) { EasingFunction = Spring });
        Pill.BeginAnimation(PillBorder.RadiusProperty, new DoubleAnimation(r, MorphDuration) { EasingFunction = Spring });
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

    private void ShowSelectedTab()
    {
        if (Tabs.SelectedItem is not ModuleTab tab)
        {
            if (Tabs.Items.Count > 0) Tabs.SelectedIndex = 0; // raises SelectionChanged again
            else PanelBody.Content = null;
            return;
        }
        if (ReferenceEquals(PanelBody.Content, tab.Panel)) return;
        PanelBody.Content = null;
        PanelBody.ContentTemplate = Templates.Find(this, tab.Panel, "Panel");
        PanelBody.Content = tab.Panel;
    }

    private void OnPillEnter(object sender, MouseEventArgs e)
    {
        if (_mode == IslandMode.Compact && !_hoverTimer.IsEnabled) _hoverTimer.Start();
        _hoverWatch.Start();
    }

    private void OnHoverWatch()
    {
        if (IsCursorOverPill())
        {
            _outsideMs = 0;
            return;
        }
        _hoverTimer.Stop();
        switch (_mode)
        {
            case IslandMode.Peek when _alertPeek:
                _hoverWatch.Stop(); // the alert closes itself when its activity ends
                return;
            case IslandMode.Peek:
                SetMode(IslandMode.Compact);
                break;
            case IslandMode.Expanded:
                _outsideMs += HoverWatchMs;
                if (_outsideMs >= CollapseAfterMs) SetMode(IslandMode.Compact);
                return;
        }
        if (_mode == IslandMode.Compact) _hoverWatch.Stop();
    }

    private bool IsCursorOverPill()
    {
        if (!IsVisible || !Native.GetCursorPos(out var p)) return false;
        var local = Pill.PointFromScreen(new Point(p.X, p.Y));
        return local.X >= 0 && local.Y >= 0 && local.X <= Pill.ActualWidth && local.Y <= Pill.ActualHeight;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SetMode(IslandMode.Compact);
        _openSettings();
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
