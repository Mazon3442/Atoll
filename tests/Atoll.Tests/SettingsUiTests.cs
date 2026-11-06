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
                    }
                }
            }
            vm.PageIndex = 1;
            window.Width = 1040;
            window.UpdateLayout();
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
        var island = new IslandWindow(Monitors.GetAll()[0], service, manager, () => { });
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
