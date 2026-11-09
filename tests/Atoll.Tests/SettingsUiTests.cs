using Atoll.Activities;
using Atoll.Modules;
using Atoll.Settings;
using Atoll.Shell;
using Atoll.Theme;
using Atoll.Modules.Weather;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

#pragma warning disable WPF0001 // Exercise the same Fluent ThemeMode used by SettingsWindow.

namespace Atoll.Tests;

public class SettingsUiTests
{
    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(50, 100, 200)]
    [InlineData(128, 128, 128)]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    public void Color_square_round_trips_rgb_and_preserves_alpha(byte r, byte g, byte b)
    {
        var color = Color.FromArgb(128, r, g, b);
        var (h, s, v) = ColorSquare.ToHsv(color);
        Assert.Equal(color, ColorSquare.FromHsv(h, s, v, color.A));
    }

    // Shares the process's sole WPF Application and STA thread with MediaSeekTests.
    internal static void VerifySettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "atoll-settings-review", Guid.NewGuid().ToString("N"), "settings.json");
        var service = new SettingsService(new SettingsStore(path));
        foreach (var d in ModuleCatalog.All)
            service.Current.Modules.Add(new ModuleEntry { Id = d.Id });
        var vm = new SettingsViewModel(service, ModuleCatalog.All) { PageIndex = 1 };
        vm.AddCalendarCommand.Execute(null);
        var window = new SettingsWindow(vm);
        Assert.NotNull(window.Icon); // its own taskbar icon
        window.Show();
        try
        {
            window.UpdateLayout();
            var picker = Descendants<ColorPicker>(window).First(p => p.Label == "Background color");
            var red = (TextBox)picker.FindName("RedField");
            var alpha = (TextBox)picker.FindName("AlphaField");
            var hex = (TextBox)picker.FindName("HexField");
            picker.SetCurrentValue(ColorPicker.ValueProperty, "#80123456");
            Assert.Equal("#80123456", vm.Background);
            red.Text = "255";
            Assert.True(picker.Commit(red));
            Assert.Equal("#80FF3456", vm.Background);
            var before = vm.Background;
            red.Text = "256";
            Assert.False(picker.Commit(red));
            Assert.Equal(before, vm.Background);
            alpha.Text = "0";
            Assert.True(picker.Commit(alpha));
            Assert.Equal("#00FF3456", vm.Background);
            alpha.Text = "100";
            Assert.True(picker.Commit(alpha));
            Assert.Equal("#FF3456", vm.Background);
            hex.Text = "#12";
            Assert.False(picker.Commit(hex));
            Assert.Equal("#FF3456", vm.Background);
            vm.ApplyThemeCommand.Execute(vm.Themes.First(t => t.Preset.Name == "Ocean"));
            Assert.Equal("#0B1E33", picker.Value);
            Assert.Equal("#0B1E33", hex.Text);

            vm.Background = "#80123456";
            vm.Opacity = 0.5;
            var preview = Assert.IsType<SolidColorBrush>(vm.PreviewFill);
            Assert.Equal(128, preview.Color.A);
            Assert.Equal(0.5, preview.Opacity);
            ThemeService.Apply(service.Current.Theme);
            var fill = Assert.IsType<SolidColorBrush>(Application.Current.Resources["Island.Background"]);
            Assert.Equal(preview.Color, fill.Color);
            Assert.Equal(preview.Opacity, fill.Opacity);

            vm.PageIndex = 5;
            window.UpdateLayout();
            var calendarPicker = Descendants<ColorPicker>(window).Single(p => p.Label == "Calendar color");
            calendarPicker.SetCurrentValue(ColorPicker.ValueProperty, "#123ABC");
            Assert.Equal("#123ABC", Assert.Single(vm.CalendarSources).Color);

            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                window.ThemeMode = mode;
                Drain();
                foreach (var width in new[] { 1040, 760 })
                {
                    window.Width = width;
                    for (var page = 0; page < vm.Pages.Count; page++)
                    {
                        vm.PageIndex = page;
                        Drain();
                        window.UpdateLayout();
                        var scroll = (ScrollViewer)window.FindName("PageScroll");
                        Assert.Equal(0, scroll.HorizontalOffset);
                        Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
                        Render(window, $"{mode}-{width}-{page}");
                        if (page == 4)
                        {
                            // The API keys section sits below the agent adapters.
                            var keys = (FrameworkElement)window.FindName("ProviderList");
                            scroll.ScrollToVerticalOffset(keys.TranslatePoint(new Point(), (UIElement)scroll.Content).Y - 160);
                            Drain(); window.UpdateLayout();
                            Render(window, $"{mode}-{width}-4-api-keys");
                            scroll.ScrollToVerticalOffset(0);
                        }
                    }
                }
            }
            vm.PageIndex = 1;
            window.Width = 1040;
            window.UpdateLayout();
            vm.IsNotch = true;
            Drain(); window.UpdateLayout();
            Assert.Equal(IslandShape.Notch, service.Current.Theme.Shape);
            Assert.Equal(new CornerRadius(0, 0, 17, 17), vm.PreviewCorner);
            Render(window, "appearance-notch");
            vm.IsPill = true;
            vm.PageIndex = 0;
            Drain(); window.UpdateLayout();
            var shortcut = Descendants<HotkeyBox>(window).First();
            Assert.Equal("Ctrl + Alt + Space", shortcut.Content);
            var generalScroll = (ScrollViewer)window.FindName("PageScroll");
            generalScroll.ScrollToVerticalOffset(shortcut.TranslatePoint(new Point(), (UIElement)generalScroll.Content).Y - 200);
            Drain(); window.UpdateLayout();
            Render(window, "general-keyboard");
            generalScroll.ScrollToVerticalOffset(0);
            vm.PageIndex = 1;
            Drain(); window.UpdateLayout();
            Drain(); window.UpdateLayout();
            var popup = (Popup)picker.FindName("Flyout");
            popup.IsOpen = true;
            Drain();
            popup.Child.UpdateLayout();
            Render((FrameworkElement)popup.Child, "color-picker");
            popup.IsOpen = false;

            using var forecast = new WeatherViewModel(service);
            forecast.Show(WeatherForecastTests.Example(), new Place("Chicago, Illinois, United States", 41.85, -87.65), false);
            var weather = new WeatherDetails { DataContext = forecast, Width = 476, Height = 600, Foreground = Brushes.White };
            var backdrop = new Border { Background = Brushes.Black, Child = weather };
            backdrop.Measure(new Size(476, 600)); backdrop.Arrange(new Rect(0, 0, 476, 600)); backdrop.UpdateLayout();
            var days = Descendants<ListBox>(weather).Single(list => ReferenceEquals(list.ItemsSource, forecast.Days));
            days.SelectedIndex = 1;
            Assert.Same(forecast.Days[1], forecast.SelectedDay);
            Assert.Equal(24, forecast.Hours.Count);
            Drain(); backdrop.UpdateLayout();
            Render(backdrop, "weather-forecast");
            var chart = Descendants<ForecastChart>(weather).Single();
            chart.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Right) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            Drain();
            Assert.Equal(1, forecast.SelectedChartIndex);
            Assert.Contains(forecast.ChartPoints[1].Time.ToString("t"), forecast.ChartReadout);
            forecast.SelectedMetric = WeatherMetric.Temperature;
            Drain();
            Assert.Equal(17, forecast.ChartPoints[0].Value);
            Assert.Equal(WeatherMetric.Temperature, chart.Metric);
            weather.Width = 290;
            backdrop.Measure(new Size(290, 600)); backdrop.Arrange(new Rect(0, 0, 290, 600)); backdrop.UpdateLayout();
            Render(backdrop, "weather-narrow");
            ThemeService.Apply(new ThemeSettings { Background = "#F5F5F7", Foreground = "#1C1C1E", Accent = "#007AFF" });
            // Detached render trees do not receive application resource invalidations.
            weather = new WeatherDetails { DataContext = forecast, Width = 476, Height = 600, Foreground = Brushes.Black };
            backdrop.Child = weather; backdrop.Background = Brushes.WhiteSmoke;
            backdrop.Measure(new Size(476, 600)); backdrop.Arrange(new Rect(0, 0, 476, 600)); Drain(); backdrop.UpdateLayout();
            var placeLabel = Descendants<TextBlock>(weather).Single(text => text.Text == forecast.PlaceLabel);
            Assert.True(ThemeService.Contrast(Assert.IsType<SolidColorBrush>(placeLabel.Foreground).Color, Colors.WhiteSmoke) >= 4.5);
            Render(backdrop, "weather-light");
            ThemeService.Apply(service.Current.Theme);

            var stats = new Atoll.Modules.Stats.StatsViewModel();
            var firstObserver = new Border(); var secondObserver = new Border();
            stats.Observe(firstObserver, true); stats.Observe(secondObserver, true); stats.Observe(firstObserver, false);
            Assert.True(stats.IsObserved);
            stats.Observe(secondObserver, false);
            Assert.False(stats.IsObserved);
            var statsView = (FrameworkElement)((DataTemplate)Application.Current.Resources["StatsViewModel.Panel"]).LoadContent();
            statsView.DataContext = stats;
            var originalContent = window.Content;
            window.Content = statsView;
            Drain();
            Assert.True(stats.IsObserved);
            statsView.DataContext = null;
            Drain();
            Assert.False(stats.IsObserved);
            window.Content = originalContent;
        }
        finally { window.Close(); }

        using var host = new ActivityHost();
        using var manager = new ModuleManager([], host, Application.Current.Dispatcher, service);
        foreach (var tab in PanelGroups.Combine(GroupedPanelsTests.Modules())) manager.Tabs.Add(tab);
        var island = new IslandWindow(Monitors.GetAll()[0], service, manager, _ => { });
        island.Show();
        try
        {
            island.OpenModule("clock");
            island.UpdateLayout();
            var panel = (Grid)island.FindName("PanelRoot");
            var expected = service.Current.Theme.ExpandedHeight - 26;
            Assert.Equal(expected, panel.Height);
            Assert.Equal(expected, panel.ActualHeight);
            var clockScroll = Descendants<ScrollViewer>(panel).First(s => s.Content is ContentControl c && c.Content is Atoll.Modules.Clock.ClockPanelViewModel);
            var viewport = clockScroll.ViewportHeight;
            // Reproduces the first opening frame without advancing the animation clock.
            var pill = (PillBorder)island.FindName("Pill");
            pill.BeginAnimation(FrameworkElement.HeightProperty, null);
            pill.Height = 32;
            island.UpdateLayout();
            Assert.Equal(viewport, clockScroll.ViewportHeight);
            Assert.Equal(expected, panel.ActualHeight);

            // Media needs more room than the default height; the song title must not be clipped at the top.
            island.OpenModule("media");
            island.UpdateLayout();
            Assert.True(panel.ActualHeight >= expected); // never shorter than the setting; grows when the content needs it
            var panelBody = (FrameworkElement)island.FindName("PanelBody");
            var title = Descendants<TextBlock>(panelBody).Single(t => t.Text == "A song with a longer title");
            Assert.InRange(title.TranslatePoint(new Point(), panelBody).Y, 0, panelBody.ActualHeight - title.ActualHeight);
            Assert.InRange(panelBody.DesiredSize.Height - panelBody.Margin.Top - panelBody.Margin.Bottom, 0, panelBody.ActualHeight); // desired size includes the margin

            // The hardware view grows until everything is visible without scrolling.
            island.OpenModule("system");
            island.UpdateLayout();
            var systemScroll = Descendants<ScrollViewer>(panelBody).First();
            Assert.True(systemScroll.ExtentHeight > 0);
            Assert.Equal(0, systemScroll.ScrollableHeight);
            island.OpenModule("clock");
            island.UpdateLayout();

            var notifications = new Atoll.Modules.Notifications.NotificationsViewModel(() => { }) { Access = Atoll.Modules.Notifications.NotificationAccess.Allowed };
            notifications.Items.Add(new Atoll.Modules.Notifications.NotificationItemViewModel(_ => { }, _ => { })
            { Id = 1, AppName = "Claude", IsClaude = true, Title = "Rendering regression test", Body = "Notification history opens", Time = DateTimeOffset.Now });
            notifications.ItemsChanged();
            var notificationIndex = manager.Tabs.ToList().FindIndex(t => t.Contains("notifications"));
            manager.Tabs[notificationIndex] = new ModuleTab(ModuleCatalog.All.Single(d => d.Id == "notifications"), notifications);
            island.OpenModule("notifications");
            Drain(); island.UpdateLayout();
            Assert.Same(notifications, ((ContentPresenter)island.FindName("PanelBody")).Content);

            using var weatherVm = new WeatherViewModel(service);
            weatherVm.Show(WeatherForecastTests.Example(), new Place("Chicago", 41.85, -87.65), false);
            var weatherIndex = manager.Tabs.ToList().FindIndex(t => t.Contains("weather"));
            manager.Tabs[weatherIndex] = new ModuleTab(ModuleCatalog.All.Single(d => d.Id == "weather"), weatherVm);
            island.OpenModule("weather");
            Drain(); island.UpdateLayout();
            Assert.True(panel.Height > expected);
            Assert.True(panel.Height < island.Height);
            var body = (ContentPresenter)island.FindName("PanelBody");
            Assert.Single(Descendants<ForecastChart>(body));
            Assert.DoesNotContain(Descendants<Button>(body), b => b.Content is "Location" or "Refresh");
        }
        finally { island.Close(); }

        // Hidden island: holding at the top edge fills a line, then opens the island fully without a click.
        service.Current.HideWhenIdle = true;
        Assert.Equal(1000, service.Current.EdgeHoldMs); // default
        var hidden = new IslandWindow(Monitors.GetAll()[0], service, manager, _ => { });
        hidden.Show();
        try
        {
            hidden.SetEnvironment(false, ForegroundKind.Normal);
            Assert.False(hidden.IsVisible); // nothing to show
            var bounds = hidden.Monitor.Bounds;
            var top = new Point(bounds.X + bounds.Width / 2.0, bounds.Y + 2);
            var start = TimeSpan.FromSeconds(100);
            hidden.CheckFullscreenEdge(top, start);
            hidden.CheckFullscreenEdge(top, start + TimeSpan.FromMilliseconds(500));
            var hint = Application.Current.Windows.OfType<RevealHint>().Single();
            Assert.True(hint.IsVisible);
            // As wide as the open island, centered, 4 px tall.
            Assert.True(Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(hint).Handle, out var line));
            var scale = hidden.Monitor.Scale;
            Assert.InRange(line.Right - line.Left, service.Current.Theme.ExpandedWidth * scale - 2, service.Current.Theme.ExpandedWidth * scale + 2);
            Assert.InRange((line.Left + line.Right) / 2.0, bounds.X + bounds.Width / 2.0 - 2, bounds.X + bounds.Width / 2.0 + 2);
            Assert.InRange(line.Bottom - line.Top, 1, Math.Ceiling(4 * scale));
            Assert.False(hidden.IsVisible);
            hidden.CheckFullscreenEdge(top, start + TimeSpan.FromMilliseconds(1000));
            Assert.True(hidden.IsVisible);
            Assert.False(hint.IsVisible);
            Assert.Equal(Visibility.Visible, ((Grid)hidden.FindName("PanelRoot")).Visibility); // opened, not just peeking
        }
        finally { hidden.Close(); service.Current.HideWhenIdle = false; }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Render(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var dir = Path.Combine(Path.GetTempPath(), "atoll-settings-review");
        Directory.CreateDirectory(dir);
        using var output = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(output);
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
