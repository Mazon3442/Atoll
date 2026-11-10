using Atoll.Activities;
using Atoll.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Atoll.Desktop;

public sealed class IslandWindow : Window, IDisposable
{
    private readonly SettingsService _settings;
    private readonly ActivityHost _activities;
    private readonly DesktopModules _modules;
    private readonly Border _surface;
    private readonly StackPanel _body;
    private readonly Button _compact;
    private readonly TabControl _tabs;
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _collapse = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _hover = new();
    private readonly DispatcherTimer _pointer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool _expanded;
    private bool _pinned;
    private bool _disposed;
#if WINDOWS
    private int _dwell;
    private int _environmentTick;
    private Atoll.Shell.ForegroundKind _foreground;
    private DateTimeOffset? _edgeSince;
    private DateTimeOffset _revealUntil;
    private DateTimeOffset _alertUntil;
#endif
    public bool Paused { get; set; }
    public Screen? Monitor { get; set; }

    public IslandWindow(SettingsService settings, ActivityHost activities, DesktopModules modules, Action<int> openSettings)
    {
        _settings = settings; _activities = activities; _modules = modules;
        Title = "ATOLL"; CanResize = false; SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Width = 300; SizeToContent = SizeToContent.Height;
        _compact = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(16, 7) };
        Avalonia.Automation.AutomationProperties.SetName(_compact, "Expand ATOLL");
        _compact.Click += (_, _) => SetExpanded(!_expanded);
        var header = new DockPanel { Margin = new Thickness(8, 4, 8, 8), LastChildFill = false };
        var options = Views.Button("Settings", () => openSettings(0)); DockPanel.SetDock(options, Dock.Right); header.Children.Add(options);
        var pin = Views.Button("Keep open", () => { _pinned = !_pinned; _collapse.Stop(); }); header.Children.Add(pin);
        _tabs = new TabControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
            ItemsSource = modules.Tabs,
            ItemTemplate = new FuncDataTemplate<ModuleTab>((tab, _) => new TextBlock { Text = tab?.Name, FontSize = 12 }),
            ContentTemplate = new FuncDataTemplate<ModuleTab>((tab, _) => tab is null ? null : Views.For(tab.Panel)),
        };
        _scroll = new ScrollViewer { Content = _tabs, MaxHeight = 580, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _body = new StackPanel { IsVisible = false, Margin = new Thickness(16, 0, 16, 16), Spacing = 4, Children = { header, _scroll } };
        _surface = new Border { Padding = new Thickness(4), Child = new StackPanel { Children = { _compact, _body } }, BorderThickness = new Thickness(1) };
        Content = _surface;
        _collapse.Tick += (_, _) => { _collapse.Stop(); if (!_pinned && !IsKeyboardFocusWithin) SetExpanded(false); };
        _hover.Tick += (_, _) => { _hover.Stop(); SetExpanded(true); };
        PointerEntered += (_, _) => { _collapse.Stop(); _hover.Interval = TimeSpan.FromMilliseconds(Math.Max(100, settings.Current.HoverDelayMs)); _hover.Start(); };
        PointerExited += (_, _) => { _hover.Stop(); if (!_pinned) _collapse.Start(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { _pinned = false; SetExpanded(false); e.Handled = true; } };
        activities.Changed += OnActivitiesChanged;
        settings.Changed += ApplySettings;
        _pointer.Tick += (_, _) => PollPointer(); _pointer.Start();
        Closed += (_, _) => Dispose();
        UpdateActivity(); ApplySettings();
    }

    public void ApplySettings()
    {
        var theme = _settings.Current.Theme;
        Color Parse(string value, string fallback) => Color.TryParse(value, out var c) ? c : Color.Parse(fallback);
        var background = Parse(theme.Background, "#000000");
        _surface.Background = new SolidColorBrush(background, Math.Clamp(theme.Opacity, .3, 1));
        _surface.BorderBrush = new SolidColorBrush(Parse(theme.Foreground, "#FFFFFF"), .15);
        Foreground = new SolidColorBrush(Parse(theme.Foreground, "#FFFFFF"));
        RequestedThemeVariant = background.R + background.G + background.B > 420 ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        _surface.CornerRadius = new CornerRadius(Math.Clamp(theme.Roundness, 0, 1) * (_expanded ? 28 : 24));
        if (Application.Current is { } app)
        {
            app.Resources["SystemAccentColor"] = Parse(theme.Accent, "#0A84FF");
            app.Resources["Atoll.Accent"] = new SolidColorBrush(Parse(theme.Accent, "#0A84FF"));
            app.Resources["Atoll.Foreground"] = Foreground;
        }
        TransparencyLevelHint = theme.Glass ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent] : [WindowTransparencyLevel.Transparent];
        Width = _expanded ? Math.Clamp(theme.ExpandedWidth, 420, 900) : 300;
        _compact.MinHeight = Math.Clamp(theme.CompactHeight, 28, 80);
        _scroll.MaxHeight = Math.Clamp(theme.ExpandedHeight, 200, 720);
        PositionAtTop();
    }

    private void PositionAtTop()
    {
        var monitor = Monitor ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (monitor is null) return;
        var bounds = monitor.WorkingArea;
        var width = (int)Math.Round(Width * monitor.Scaling);
        Position = new PixelPoint(bounds.X + (bounds.Width - width) / 2, bounds.Y + (int)(Math.Clamp(_settings.Current.TopOffset, 0, 200) * monitor.Scaling));
        _tabs.MaxWidth = Width - 48;
    }

    private void SetExpanded(bool value)
    {
        _expanded = value; _body.IsVisible = value;
        ApplySettings();
        Avalonia.Automation.AutomationProperties.SetName(_compact, value ? "Collapse ATOLL" : "Expand ATOLL");
    }

    public void OpenModule(string id)
    {
        _tabs.SelectedItem = _modules.Tabs.FirstOrDefault(t => t.Contains(id));
        SetExpanded(true);
    }

    private void OnActivitiesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateActivity);
    private void UpdateActivity()
    {
        if (_disposed) return;
        var (primary, _, _) = _activities.Current;
        _compact.Content = Views.Compact(primary?.Content);
        if (primary?.Alert == true)
        {
#if WINDOWS
            _alertUntil = DateTimeOffset.UtcNow.AddSeconds(8);
#endif
            if (!_expanded) { OpenModule(primary.ModuleId); _collapse.Start(); }
        }
    }

    private void PollPointer()
    {
        if (Paused) { IsVisible = false; return; }
#if WINDOWS
        var handle = TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) return;
        if (++_environmentTick % 16 == 0)
        {
            var native = Atoll.Shell.Monitors.GetAll().FirstOrDefault(m => m.Handle == Atoll.Shell.Native.MonitorFromWindow(handle, 0));
            if (native is not null) _foreground = Atoll.Shell.FullscreenWatcher.Classify(native) ?? _foreground;
        }
        var now = DateTimeOffset.UtcNow;
        var overlay = _foreground is Atoll.Shell.ForegroundKind.Fullscreen or Atoll.Shell.ForegroundKind.Game;
        var revealed = now < _revealUntil;
        var mode = _settings.Current.FullscreenMode;
        IsVisible = !overlay || revealed || _foreground != Atoll.Shell.ForegroundKind.Game && (mode is FullscreenMode.Stay or FullscreenMode.Dim || mode == FullscreenMode.PopUp && now < _alertUntil);
        Opacity = overlay && !revealed && mode == FullscreenMode.Dim ? .5 : 1;
        var pass = _settings.Current.ClickThrough && !_expanded && _foreground == Atoll.Shell.ForegroundKind.Maximized;
        if (Atoll.Shell.Native.GetCursorPos(out var cursor))
        {
            if (Monitor is { } monitor && Math.Abs(cursor.X - (monitor.Bounds.X + monitor.Bounds.Width / 2)) < 150 && cursor.Y <= monitor.Bounds.Y + 3 && cursor.Y >= monitor.Bounds.Y)
            {
                _edgeSince ??= now;
                if (now - _edgeSince > TimeSpan.FromSeconds(3)) _revealUntil = now.AddSeconds(10);
            }
            else _edgeSince = null;
            var bounds = new PixelRect(Position, new PixelSize((int)(Width * RenderScaling), (int)(Bounds.Height * RenderScaling)));
            if (bounds.Contains(new PixelPoint(cursor.X, cursor.Y)))
            {
                _dwell += 60;
                if (pass && _dwell >= Math.Max(350, _settings.Current.HoverDelayMs)) { SetExpanded(true); pass = false; }
            }
            else _dwell = 0;
        }
        var style = Atoll.Shell.Native.GetWindowLongPtr(handle, Atoll.Shell.Native.GWL_EXSTYLE).ToInt64();
        var updated = pass ? style | Atoll.Shell.Native.WS_EX_TRANSPARENT : style & ~Atoll.Shell.Native.WS_EX_TRANSPARENT;
        if (style != updated) Atoll.Shell.Native.SetWindowLongPtr(handle, Atoll.Shell.Native.GWL_EXSTYLE, (nint)updated);
#else
        if (!IsVisible) IsVisible = true;
#endif
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _activities.Changed -= OnActivitiesChanged; _settings.Changed -= ApplySettings;
        _pointer.Stop(); _collapse.Stop(); _hover.Stop();
    }
}
