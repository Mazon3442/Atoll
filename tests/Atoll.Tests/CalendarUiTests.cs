using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Modules.Calendar;
using Atoll.Modules.Clock;
using Atoll.Modules.Indicators;
using Atoll.Modules.Stats;
using Atoll.Settings;
using Atoll.Shell;
using Atoll.Theme;

namespace Atoll.Tests;

internal static class CalendarUiTests
{
    internal static void Verify()
    {
        var vm = new CalendarViewModel(() => { }) { HasSources = true, HasEvents = true, Updated = "Test fixture" };
        CalendarEventViewModel Row(CalendarEventItem item)
        {
            CalendarEventViewModel row = null!;
            row = new CalendarEventViewModel(item, _ => row.IsPinned = !row.IsPinned, _ => { row.IsDismissed = true; row.IsPinned = false; });
            return row;
        }
        var start = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var allDay = Row(new CalendarEventItem("day", start, start.AddDays(1), true, "All-day event", null, "Test fixture", "#007AFF"));
        var timed = Row(new CalendarEventItem("timed", start, start.AddHours(1), false, "Meeting with a long title that should truncate", "Conference room", "Test fixture", "#007AFF"));
        vm.SelectedDate = new DateTime(2026, 11, 10);
        vm.VisibleMonth = new DateTime(2026, 11, 1);
        vm.SelectedDayTitle = "Today";
        vm.SelectedDayDate = "Nov 10";
        vm.SelectedDayCount = "2 events";
        vm.HasSelectedDayEvents = true;
        vm.SelectedDayRows = [allDay, timed];
        vm.MonthCells = CalendarViewModel.BuildMonth(new DateTime(2026, 11, 1), new DateTime(2026, 11, 10), new DateTime(2026, 11, 10),
            new Dictionary<DateTime, CalendarDayMark> { [new DateTime(2026, 11, 10)] = new(2, ["#007AFF"]) });
        foreach (var light in new[] { false, true })
        {
            ThemeService.Apply(new ThemeSettings { Background = light ? "#F5F5F7" : "#000000", Foreground = light ? "#1C1C1E" : "#FFFFFF" });
            foreach (var width in new[] { 560, 720 })
            {
                var panel = (FrameworkElement)((DataTemplate)Application.Current.Resources["CalendarViewModel.Panel"]).LoadContent();
                panel.DataContext = vm;
                var surface = new Border { Background = light ? Brushes.WhiteSmoke : Brushes.Black, Child = panel, Padding = new Thickness(10) };
                panel.SetResourceReference(Control.ForegroundProperty, "Island.Foreground");
                surface.Measure(new Size(width, 300)); surface.Arrange(new Rect(0, 0, width, 300)); surface.UpdateLayout();
                // Pin and dismiss stay out of the way until a row is hovered or focused.
                Assert.All(Descendants<StackPanel>(panel).Where(p => p.Name == "Actions"), a => Assert.Equal(Visibility.Collapsed, a.Visibility));
                var pin =Descendants<Button>(panel).Single(b => ReferenceEquals(b.Command, allDay.TogglePinCommand));
                ((IInvokeProvider)new ButtonAutomationPeer(pin).GetPattern(PatternInterface.Invoke)).Invoke();
                Drain();
                Assert.True(allDay.IsPinned);
                Assert.Equal("Unpin from island", System.Windows.Automation.AutomationProperties.GetName(pin));
                var dismiss = Descendants<Button>(panel).Single(b => ReferenceEquals(b.Command, allDay.DismissCommand));
                ((IInvokeProvider)new ButtonAutomationPeer(dismiss).GetPattern(PatternInterface.Invoke)).Invoke();
                Drain();
                Assert.True(allDay.IsDismissed);
                Assert.False(dismiss.IsEnabled);
                Render(surface, $"calendar-{(light ? "light" : "dark")}-{width}");
                allDay.IsPinned = false; allDay.IsDismissed = false;
            }
        }
        VerifyIsland();
        VerifyTabStrip();
    }

    private static void VerifyTabStrip()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        ThemeService.Apply(service.Current.Theme);
        using var host = new ActivityHost();
        using var manager = new ModuleManager(ModuleCatalog.All, host, Application.Current.Dispatcher, service);
        foreach (var descriptor in ModuleCatalog.All)
            service.Current.Modules.Add(new ModuleEntry { Id = descriptor.Id, Enabled = true });
        var modules = PanelGroups.Combine(GroupedPanelsTests.Modules());
        foreach (var tab in modules) manager.Tabs.Add(tab);
        var island = new IslandWindow(Monitors.GetAll()[0], service, manager, _ => { });
        try
        {
            var tabs = (ListBox)island.FindName("Tabs");
            var menu = (ListBox)island.FindName("TabMenu");
            var button = (Button)island.FindName("TabMenuButton");

            // The default top row is Home, Agents, Media, Notifications, Calendar, Files, Clipboard and
            // Weather. Timer, Notes, System and Launcher wait in the ⋯ menu.
            string[] outside = ["clock", "notes", "tide", "system", "providers", "launcher"];
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Equal(outside.Length, menu.Items.Count);
            Assert.Equal(modules.Count - outside.Length, tabs.Items.Count);

            var weather = modules.Single(t => t.Contains("weather"));

            // Off: gone from the strip and from the ⋯ menu.
            service.Current.Modules.Single(entry => entry.Id == "weather").Enabled = false;
            manager.Tabs.Remove(weather);
            Assert.Equal(outside.Length, menu.Items.Count);
            Assert.Equal(modules.Count - outside.Length - 1, tabs.Items.Count);

            // On but not bookmarked: waits in the ⋯ menu.
            service.Current.Modules.Single(entry => entry.Id == "weather").Enabled = true;
            service.Current.Modules.Single(entry => entry.Id == "weather").Bookmarked = false;
            manager.Tabs.Add(weather);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Contains(menu.Items.Cast<ModuleTab>(), tab => tab.Contains("weather"));
            Assert.Equal(outside.Length + 1, menu.Items.Count);
            Assert.Equal(modules.Count - outside.Length - 1, tabs.Items.Count);

            island.Show();
            island.UpdateLayout();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var popup = (Popup)island.FindName("TabMenuPopup");
            Assert.True(popup.IsOpen);
            if (popup.Child is FrameworkElement popupRoot)
            {
                popupRoot.Measure(new Size(260, 400));
                popupRoot.Arrange(new Rect(0, 0, 260, 400));
                popupRoot.UpdateLayout();
                Render(popupRoot, "overflow-menu");
            }
            popup.IsOpen = false;

            // Bookmarked again: back in the top row and out of the menu.
            service.Current.Modules.Single(entry => entry.Id == "weather").Bookmarked = true;
            manager.Tabs.Remove(weather);
            manager.Tabs.Add(weather);
            Assert.DoesNotContain(menu.Items.Cast<ModuleTab>(), tab => tab.Contains("weather"));
            Assert.Equal(outside.Length, menu.Items.Count);
            Assert.Equal(modules.Count - outside.Length, tabs.Items.Count);

            island.ShowPalette();
            island.UpdateLayout();
            var palette = (FrameworkElement)island.FindName("Palette");
            Assert.Equal(Visibility.Visible, palette.Visibility);
            ((Control)palette).Background = Brushes.Black; // the island's dark surface is the parent, not the control
            palette.Measure(new Size(600, 220));
            palette.Arrange(new Rect(0, 0, 600, 220));
            palette.UpdateLayout();
            Render(palette, "command-palette");
        }
        finally { island.Close(); }
    }

    private static void VerifyIsland()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        ThemeService.Apply(service.Current.Theme);
        using var host = new ActivityHost();
        using var manager = new ModuleManager([], host, Application.Current.Dispatcher, service);
        var modules = GroupedPanelsTests.Modules();
        var sound = (SystemPanelViewModel)modules.Single(m => m.Descriptor.Id == "system").Panel;
        var stats = (StatsViewModel)modules.Single(m => m.Descriptor.Id == "stats").Panel;
        sound.Bluetooth = "Headphones";
        stats.Devices.Add(new DeviceBattery("Headphones", 80));
        foreach (var tab in PanelGroups.Combine(modules)) manager.Tabs.Add(tab);
        var monitor = Monitors.GetAll()[0];
        var island = new IslandWindow(monitor, service, manager, _ => { });
        try
        {
            var next = new NextEventViewModel(() => { }, () => { }) { Title = "Calendar interaction fixture", When = "in 10 min", Subtitle = "Test fixture" };
            var timer = new TimerViewModel(TimeProvider.System);
            timer.StartCountdownCommand.Execute(5);
            var mediaPanel = GroupedPanelsTests.Modules().Single(m => m.Descriptor.Id == "media").Panel;
            island.SetActivitySnapshot(new Activity("calendar.next", "calendar", next, ActivityPriority.High),
            [
                new Activity("clock.timer", "clock", timer, ActivityPriority.Normal),
                new Activity("media.song", "media", mediaPanel, ActivityPriority.Normal),
                new Activity("system.battery", "system", new IndicatorViewModel("", "Battery 80%"), ActivityPriority.Ambient),
                new Activity("weather.cpu", "weather", new IndicatorViewModel("", "CPU 34%"), ActivityPriority.Ambient),
            ]);
            island.Show();
            island.UpdateLayout();
            var bluetooth = (Button)island.FindName("BluetoothButton");
            Assert.Equal(Visibility.Visible, bluetooth.Visibility);
            bluetooth.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var bluetoothPopup = (Popup)island.FindName("BluetoothPopup");
            Assert.True(bluetoothPopup.IsOpen);
            if (bluetoothPopup.Child is FrameworkElement popupRoot)
            {
                popupRoot.Measure(new Size(260, 400));
                popupRoot.Arrange(new Rect(0, 0, 260, 400));
                popupRoot.UpdateLayout();
                Render(popupRoot, "bluetooth-popup");
            }
            bluetoothPopup.IsOpen = false;
            var pill = (PillBorder)island.FindName("Pill");
            var bubble = (PillBorder)island.FindName("Bubble");
            var second = (PillBorder)island.FindName("Bubble2");
            var third = (PillBorder)island.FindName("Bubble3");
            var fourth = (PillBorder)island.FindName("Bubble4");
            SnapCompact(pill, (ContentPresenter)island.FindName("PillContent"), service.Current.Theme.CompactHeight);
            island.UpdateLayout();
            Assert.True(bubble.IsVisible);
            Assert.True(second.IsVisible);
            Assert.True(third.IsVisible);
            Assert.True(fourth.IsVisible);
            // Two capsules on each side of the pill, nearest first.
            Assert.True(bubble.PointToScreen(new Point()).X >= pill.PointToScreen(new Point(pill.ActualWidth, 0)).X);
            Assert.True(third.PointToScreen(new Point()).X >= bubble.PointToScreen(new Point(bubble.ActualWidth, 0)).X);
            Assert.True(second.PointToScreen(new Point(second.ActualWidth, 0)).X <= pill.PointToScreen(new Point()).X);
            Assert.True(fourth.PointToScreen(new Point(fourth.ActualWidth, 0)).X <= second.PointToScreen(new Point()).X);
            var pointer = bubble.PointToScreen(new Point(bubble.ActualWidth / 2, bubble.ActualHeight / 2));
            var compactWidth = pill.ActualWidth;
            for (var i = 0; i < 6; i++) island.OnPointerTick(pointer);
            Assert.Same(Templates.Find(island, next, "Peek"), ((ContentPresenter)island.FindName("PillContent")).ContentTemplate);
            pill.BeginAnimation(FrameworkElement.WidthProperty, null);
            pill.Width = compactWidth + 24;
            island.UpdateLayout();
            Assert.True(island.IsCursorOverIsland(pointer));
            for (var i = 0; i < 30; i++) island.OnPointerTick(pointer);
            Assert.True(island.IsCursorOverIsland(pointer));
            var away = new Point(monitor.Bounds.X - 100, monitor.Bounds.Y + 400);
            for (var i = 0; i < 7; i++) island.OnPointerTick(away);
            Assert.Same(Templates.Find(island, next, "Peek"), ((ContentPresenter)island.FindName("PillContent")).ContentTemplate);
            island.OnPointerTick(away);
            Assert.Same(Templates.Find(island, next, "Compact"), ((ContentPresenter)island.FindName("PillContent")).ContentTemplate);
            SnapCompact(pill, (ContentPresenter)island.FindName("PillContent"), service.Current.Theme.CompactHeight); island.UpdateLayout();
            ((ContentPresenter)island.FindName("PillContent")).BeginAnimation(UIElement.OpacityProperty, null);
            Drain(); island.UpdateLayout();
            RenderIsland(pill, bubble, third, second, fourth);

            // Four activities: non-ring chips use wide capsules, the timer stays a circle.
            Assert.InRange(bubble.Width, 33, 35);
            Assert.InRange(third.Width, 117, 119);

            // Two activities: plain dots, no wide capsules. The timer remains a ring.
            island.SetActivitySnapshot(new Activity("calendar.next", "calendar", next, ActivityPriority.High),
            [
                new Activity("clock.timer", "clock", timer, ActivityPriority.Normal),
                new Activity("media.song", "media", mediaPanel, ActivityPriority.Normal),
            ]);
            SnapCompact(pill, (ContentPresenter)island.FindName("PillContent"), service.Current.Theme.CompactHeight);
            island.UpdateLayout();
            Assert.True(bubble.IsVisible);
            Assert.True(second.IsVisible);
            Assert.False(third.IsVisible);
            Assert.False(fourth.IsVisible);
            Assert.InRange(bubble.Width, 33, 35);
            Assert.InRange(second.Width, 33, 35);
            Assert.True(second.Width < 60); // dot, not the 118 wide capsule

            second.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.True(((ModuleTab)((ListBox)island.FindName("Tabs")).SelectedItem).Contains("media"));
            Assert.False(bubble.IsVisible);
            Assert.False(second.IsVisible);
            Assert.False(third.IsVisible);
            Assert.False(fourth.IsVisible);

            // A ⋯ tab joins the end of the row, and the timer tab grows to fit its Pomodoro settings.
            service.Current.Theme.Motion = MotionStyle.Off;
            var clockTab = manager.Tabs.Single(t => t.Contains("clock"));
            island.OpenFromMenu(clockTab);
            var clockPanel = (ClockPanelViewModel)clockTab.Panel;
            clockPanel.Timer.SectionIndex = 1;
            clockPanel.Timer.FocusMinutes = 40;
            island.ApplySettings();
            Drain(); island.UpdateLayout();
            var tabs = (ListBox)island.FindName("Tabs");
            Assert.Same(clockTab, tabs.Items[^1]);
            var modeSwitch = Descendants<ListBox>(island).Single(l => AutomationProperties.GetName(l) == "Timer mode");
            var switchCenter = modeSwitch.TranslatePoint(new Point(modeSwitch.ActualWidth / 2, 0), pill).X;
            Assert.InRange(switchCenter, pill.ActualWidth / 2 - 2, pill.ActualWidth / 2 + 2);
            var start = Descendants<Button>(island).Single(b => AutomationProperties.GetName(b) == "Start Pomodoro");
            Assert.Equal("Start 40 min focus", start.Content);
            Assert.InRange(start.TranslatePoint(new Point(0, start.ActualHeight), pill).Y, 1, pill.ActualHeight - 1);
            Render(pill, "island-timer-pomodoro");

            service.Current.FullscreenMode = FullscreenMode.PopUp;
            island.SetEnvironment(false, ForegroundKind.Fullscreen);
            Assert.False(island.IsVisible);
            var edge = new Point(monitor.Bounds.X + monitor.Bounds.Width / 2.0, monitor.Bounds.Y);
            // Default hold: 1 s. Leaving the edge restarts it.
            island.CheckFullscreenEdge(edge, TimeSpan.Zero);
            island.CheckFullscreenEdge(edge, TimeSpan.FromSeconds(0.9));
            Assert.False(island.IsVisible);
            island.CheckFullscreenEdge(away, TimeSpan.FromSeconds(1.5));
            island.CheckFullscreenEdge(edge, TimeSpan.FromSeconds(2));
            island.CheckFullscreenEdge(edge, TimeSpan.FromSeconds(3));
            Assert.True(island.IsVisible);
            Assert.True(island.IsCursorOverIsland(edge));
            // It opens fully, so it closes after the open island's delay (900 ms) once the pointer leaves.
            for (var i = 0; i < 16; i++) island.OnPointerTick(away);
            island.CheckFullscreenEdge(away, TimeSpan.FromSeconds(8));
            Assert.False(island.IsVisible);
            island.SetEnvironment(false, ForegroundKind.Game);
            island.CheckFullscreenEdge(edge, TimeSpan.FromSeconds(9));
            island.CheckFullscreenEdge(edge, TimeSpan.FromSeconds(15));
            Assert.True(island.IsVisible);
            island.SetEnvironment(true, ForegroundKind.Fullscreen);
            island.CheckFullscreenEdge(edge, TimeSpan.FromSeconds(16));
            island.CheckFullscreenEdge(edge, TimeSpan.FromSeconds(22));
            Assert.False(island.IsVisible);
        }
        finally { island.Close(); }
    }

    private static void SnapCompact(PillBorder pill, ContentPresenter presenter, double height)
    {
        presenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        pill.BeginAnimation(FrameworkElement.WidthProperty, null);
        pill.BeginAnimation(FrameworkElement.HeightProperty, null);
        pill.Width = Math.Max(height * 3.2, presenter.DesiredSize.Width + height);
        pill.Height = height;
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
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var dir = Path.Combine(Path.GetTempPath(), "atoll-calendar-review");
        Directory.CreateDirectory(dir);
        using var output = File.Create(Path.Combine(dir, name + ".png")); encoder.Save(output);
    }

    private static void RenderIsland(params FrameworkElement[] pieces)
    {
        var dpi = VisualTreeHelper.GetDpi(pieces[0]);
        pieces = pieces.OrderBy(piece => piece.PointToScreen(new Point()).X).ToArray(); // left to right on screen
        var positions = pieces.Select(piece => piece.PointToScreen(new Point())).ToArray();
        var width = (positions[^1].X - positions[0].X) / dpi.DpiScaleX + pieces[^1].ActualWidth;
        var strip = new DrawingVisual();
        using (var dc = strip.RenderOpen())
            for (var i = 0; i < pieces.Length; i++)
                dc.DrawRectangle(new VisualBrush(pieces[i]), null,
                    new Rect((positions[i].X - positions[0].X) / dpi.DpiScaleX, 0, pieces[i].ActualWidth, pieces[i].ActualHeight));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(pieces.Max(p => p.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(strip);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(Path.GetTempPath(), "atoll-calendar-review", "island-four-sides.png")); encoder.Save(output);
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
