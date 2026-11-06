using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Modules.Calendar;
using Atoll.Modules.Clock;
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
        vm.Days.Add(new CalendarDay { Label = "Today", Events = [allDay,
            Row(new CalendarEventItem("timed", start, start.AddHours(1), false, "Meeting with a long title that should truncate", "Conference room", "Test fixture", "#007AFF"))] });
        foreach (var light in new[] { false, true })
        {
            ThemeService.Apply(new ThemeSettings { Background = light ? "#F5F5F7" : "#000000", Foreground = light ? "#1C1C1E" : "#FFFFFF" });
            foreach (var width in new[] { 210, 300 })
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
    }

    private static void VerifyIsland()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        ThemeService.Apply(service.Current.Theme);
        using var host = new ActivityHost();
        using var manager = new ModuleManager([], host, Application.Current.Dispatcher, service);
        foreach (var tab in PanelGroups.Combine(GroupedPanelsTests.Modules())) manager.Tabs.Add(tab);
        var monitor = Monitors.GetAll()[0];
        var island = new IslandWindow(monitor, service, manager, _ => { });
        try
        {
            var next = new NextEventViewModel(() => { }, () => { }) { Title = "Calendar interaction fixture", When = "in 10 min", Subtitle = "Test fixture" };
            var timer = new TimerViewModel(TimeProvider.System);
            timer.StartCountdownCommand.Execute(5);
            island.SetActivities(new Activity("calendar.next", "calendar", next, ActivityPriority.Ambient),
                new Activity("clock.timer", "clock", timer, ActivityPriority.Normal),
                new Activity("media.song", "media", GroupedPanelsTests.Modules().Single(m => m.Descriptor.Id == "media").Panel, ActivityPriority.Normal));
            island.Show();
            island.UpdateLayout();
            var pill = (PillBorder)island.FindName("Pill");
            var bubble = (PillBorder)island.FindName("Bubble");
            var second = (PillBorder)island.FindName("Bubble2");
            SnapCompact(pill, (ContentPresenter)island.FindName("PillContent"), service.Current.Theme.CompactHeight);
            island.UpdateLayout();
            Assert.True(bubble.IsVisible);
            Assert.True(second.IsVisible);
            // One bubble on each side of the pill.
            Assert.True(bubble.PointToScreen(new Point()).X >= pill.PointToScreen(new Point(pill.ActualWidth, 0)).X);
            Assert.True(second.PointToScreen(new Point(second.ActualWidth, 0)).X <= pill.PointToScreen(new Point()).X);
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
            RenderIsland(pill, bubble, second);

            second.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.True(((ModuleTab)((ListBox)island.FindName("Tabs")).SelectedItem).Contains("media"));
            Assert.False(bubble.IsVisible);
            Assert.False(second.IsVisible);

            service.Current.FullscreenMode = FullscreenMode.PopUp;
            island.SetEnvironment(false, ForegroundKind.Fullscreen);
            Assert.False(island.IsVisible);
            var edge = new Point(monitor.Bounds.X + monitor.Bounds.Width / 2.0, monitor.Bounds.Y);
            island.CheckFullscreenEdge(edge, TimeSpan.Zero);
            island.CheckFullscreenEdge(edge, TimeSpan.FromSeconds(2.9));
            Assert.False(island.IsVisible);
            island.CheckFullscreenEdge(away, TimeSpan.FromSeconds(3));
            island.CheckFullscreenEdge(edge, TimeSpan.FromSeconds(4));
            island.CheckFullscreenEdge(edge, TimeSpan.FromSeconds(7));
            Assert.True(island.IsVisible);
            Assert.True(island.IsCursorOverIsland(edge));
            for (var i = 0; i < 9; i++) island.OnPointerTick(away);
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
        using var output = File.Create(Path.Combine(Path.GetTempPath(), "atoll-calendar-review", "island-two-sides.png")); encoder.Save(output);
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
