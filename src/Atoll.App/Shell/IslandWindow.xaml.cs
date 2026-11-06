using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
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
    private static readonly Duration MorphDuration = TimeSpan.FromMilliseconds(520);
    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(140);

    private const int PointerTickMs = 60;
    private const int CollapseAfterMs = 900;
    private const int MinClickThroughDwellMs = 350; // short passes (moving to a browser tab) must not open it
    private const double PassThroughOpacity = 0.25;

    private readonly SettingsService _settings;
    private readonly ModuleManager _modules;
    private readonly Action _openSettings;

    // The cursor position is polled instead of using MouseEnter/Leave: a click-through window gets
    // no mouse events, and layered WPF windows raise spurious MouseLeave events anyway.
    private readonly DispatcherTimer _pointer;
    private int _dwellMs;
    private int _outsideMs;
    private bool _alertPeek;
    private bool _faded;

    private nint _hwnd;
    private IslandMode _mode = IslandMode.Compact;
    private Activity? _primary;
    private Activity? _secondary;
    private object? _shownContent;
    private string? _shownTemplateMode;
    private INotifyPropertyChanged? _watched;
    private bool _renderQueued;
    private (double W, double H, double R) _target = (-1, -1, -1);

    private bool _paused;
    private ForegroundKind _foreground;
    private bool? _clickThroughApplied;

    // Frosted glass: the screen behind the window, blurred, painted as the pill/bubble background.
    private readonly DispatcherTimer _glassTimer;
    private bool _glassActive;
    private BitmapSource? _glassImage;

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

        _pointer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(PointerTickMs) };
        _pointer.Tick += (_, _) => OnPointerTick();
        _glassTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _glassTimer.Tick += (_, _) => SampleGlass();

        Tabs.ItemsSource = modules.Tabs;
        Tabs.SelectionChanged += (_, _) => ShowSelectedTab();

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
        LayoutUpdated += (_, _) => ApplyGlassBrushes(); // follows the pill through every animation frame

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
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _pointer.Start();
            else _pointer.Stop();
            UpdateGlass();
        };
        Closed += (_, _) =>
        {
            Watch(null);
            _pointer.Stop();
            _glassTimer.Stop();
        };
    }

    public MonitorInfo Monitor { get; }

    private double CompactHeight => Math.Clamp(_settings.Current.Theme.CompactHeight, 24, 60);

    private double Roundness => Math.Clamp(_settings.Current.Theme.Roundness, 0, 1);

    /// <summary>Pass clicks through only when a maximized or fullscreen app sits behind the island (tabs, toolbars).</summary>
    private bool ClickThrough =>
        _settings.Current.ClickThrough && _foreground is ForegroundKind.Maximized or ForegroundKind.Fullscreen;

    public void SetActivities(Activity? primary, Activity? secondary)
    {
        var isNewPrimary = !ReferenceEquals(primary, _primary);
        _primary = primary;
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
            if (_mode == IslandMode.Peek && !IsCursorOverIsland()) _mode = IslandMode.Compact;
        }

        if (_mode == IslandMode.Peek && primary is null) _mode = IslandMode.Compact;
        UpdatePresence();
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
        _target = (-1, -1, -1);
        _shownContent = null; // template choice may depend on size; rebuild
        Place();
        UpdatePresence();
        UpdateGlass();
        Render();
    }

    /// <summary>Called by the manager whenever pause or the foreground app's state may have changed.</summary>
    public void SetEnvironment(bool paused, ForegroundKind foreground)
    {
        _paused = paused;
        _foreground = foreground;
        UpdatePresence();
    }

    public void EnsureTopmost()
    {
        if (_hwnd != 0)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Shows, hides or dims the window for pause and the fullscreen setting.</summary>
    private void UpdatePresence()
    {
        var mode = _foreground switch
        {
            ForegroundKind.Game => FullscreenMode.Hide, // games never show the island
            ForegroundKind.Fullscreen => _settings.Current.FullscreenMode,
            _ => FullscreenMode.Stay,
        };
        var shown = !_paused && mode switch
        {
            FullscreenMode.Hide => false,
            // Pop up only for things that end on their own: alerts and short indicators.
            FullscreenMode.PopUp => _primary is { Alert: true } || _primary?.Duration is not null,
            _ => true,
        };
        Opacity = mode == FullscreenMode.Dim ? 0.5 : 1;

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
        if (mode == IslandMode.Expanded)
        {
            SetClickThrough(false);
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
        var over = IsCursorOverIsland();
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
                else if (!_alertPeek) SetMode(IslandMode.Compact); // alerts close when their activity ends
                break;

            case IslandMode.Expanded:
                if (over) _outsideMs = 0;
                else if ((_outsideMs += PointerTickMs) >= CollapseAfterMs) SetMode(IslandMode.Compact);
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
    }

    private bool IsCursorOverIsland()
    {
        if (!IsVisible || !Native.GetCursorPos(out var p)) return false;
        var screen = new Point(p.X, p.Y);
        return Contains(Pill, screen) || (Bubble.IsVisible && Contains(Bubble, screen));
    }

    private static bool Contains(FrameworkElement e, Point screen)
    {
        var local = e.PointFromScreen(screen);
        return local.X >= 0 && local.Y >= 0 && local.X <= e.ActualWidth && local.Y <= e.ActualHeight;
    }

    // ---------- frosted glass ----------

    /// <summary>Starts or stops glass sampling to match the theme and visibility.</summary>
    private void UpdateGlass()
    {
        var wanted = ThemeService.GlassTint is not null && IsVisible && _hwnd != 0;
        if (wanted == _glassActive) return;
        _glassActive = wanted;
        if (wanted)
        {
            if (!GlassSampler.ExcludeFromCapture(_hwnd, true))
                Log.Info("Frosted glass: this Windows build cannot exclude the island from capture; the glass may show the island itself");
            SampleGlass();
            _glassTimer.Start();
        }
        else
        {
            _glassTimer.Stop();
            if (_hwnd != 0) GlassSampler.ExcludeFromCapture(_hwnd, false);
            _glassImage = null;
            Pill.SetResourceReference(BackgroundProperty, "Island.Background");
            Bubble.SetResourceReference(BackgroundProperty, "Island.Background");
        }
    }

    private void SampleGlass()
    {
        if (!_glassActive || ThemeService.GlassTint is not { } tint || !Native.GetWindowRect(_hwnd, out var r)) return;
        _glassImage = GlassSampler.Capture(new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), tint);
        ApplyGlassBrushes();
    }

    /// <summary>Paints the part of the sampled image that lies under the pill and bubble (Border clips it to the rounded shape).</summary>
    private void ApplyGlassBrushes()
    {
        if (!_glassActive || _glassImage is null || ActualWidth <= 0) return;
        Pill.Background = GlassBrush(Pill);
        if (Bubble.IsVisible) Bubble.Background = GlassBrush(Bubble);
    }

    private ImageBrush GlassBrush(FrameworkElement e)
    {
        // Image pixels per DIP; the sample covers this whole window. TransformToAncestor includes the bubble's slide.
        var sx = _glassImage!.PixelWidth / ActualWidth;
        var sy = _glassImage.PixelHeight / ActualHeight;
        var origin = e.TransformToAncestor(this).Transform(new Point(0, 0));
        var brush = new ImageBrush(_glassImage)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(origin.X * sx, origin.Y * sy, Math.Max(1, e.ActualWidth * sx), Math.Max(1, e.ActualHeight * sy)),
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality); // smooth upscale = extra blur
        brush.Freeze();
        return brush;
    }

    // ---------- rendering ----------

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
