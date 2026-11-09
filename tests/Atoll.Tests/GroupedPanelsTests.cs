using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Atoll.Modules;
using Atoll.Modules.Actions;
using Atoll.Modules.Calendar;
using Atoll.Modules.Claude;
using Atoll.Modules.Clipboard;
using Atoll.Modules.Clock;
using Atoll.Modules.Indicators;
using Atoll.Modules.Media;
using Atoll.Modules.Shelf;
using Atoll.Modules.Stats;
using Atoll.Modules.Notifications;
using Atoll.Modules.Providers;

namespace Atoll.Tests;

public class GroupedPanelsTests
{
    internal static List<ModuleTab> Modules()
    {
        var clock = new ClockViewModel();
        clock.Update(new DateTime(2026, 11, 6, 10, 30, 0));
        var player = new MediaViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask)
        { HasSession = true, HasTimeline = true, CanSeek = true, Title = "A song with a longer title", Artist = "Artist name", AppName = "Music player", PositionText = "1:23", DurationText = "4:30", Progress = 0.3 };
        player.SetSources([new("media:spotify", "Spotify"), new("audio:42", "Discord (audio)")]);
        player.Follow("media:spotify");
        var sound = new SystemPanelViewModel(_ => { }, () => { })
        { Volume = 50, Network = "Connected", Bluetooth = "Headphones", Battery = "85%" };
        var panels = new Dictionary<string, object>
        {
            ["claude"] = new ClaudeViewModel(new ClaudeSessionTracker(TimeProvider.System)),
            ["providers"] = new ProviderViewModel(),
            ["media"] = player,
            ["notifications"] = new object(),
            ["clock"] = new ClockPanelViewModel(clock, new TimerViewModel(TimeProvider.System)),
            ["calendar"] = new CalendarViewModel(() => { }),
            ["system"] = sound,
            ["shelf"] = new ShelfViewModel(_ => { }, () => { }),
            ["clipboard"] = new ClipboardViewModel(new ClipboardHistory()),
            ["stats"] = new StatsViewModel(),
            ["weather"] = new object(),
            ["actions"] = new QuickActionsViewModel(new QuickActionsModule()),
            ["privacy"] = new object(), // no panel of its own; it belongs to the System tab
        };
        return ModuleCatalog.All.Select(d => new ModuleTab(d, panels[d.Id])).ToList();
    }

    [Fact]
    public void Twelve_modules_become_eight_tabs_with_stable_identity_and_legacy_routes()
    {
        var modules = Modules();
        var tabs = PanelGroups.Combine(modules);
        Assert.Equal(["claude", "providers", "media", "notifications", "clock", "system", "shelf", "weather"], tabs.Select(t => t.Descriptor.Id));
        Assert.Equal(tabs, PanelGroups.Combine(modules));
        foreach (var module in modules)
            Assert.Single(tabs, t => t.Contains(module.Descriptor.Id));
        Assert.True(tabs.Single(t => t.Descriptor.Name == "Agents").Contains("agents"));
        Assert.True(tabs.Single(t => t.Descriptor.Name == "Files").Contains("files"));
        Assert.Same(modules.Single(t => t.Descriptor.Id == "system").Panel,
            Assert.IsType<MediaPanelViewModel>(tabs.Single(t => t.Contains("media")).Panel).Sound);
    }

    [Theory]
    [InlineData("clock", 5)]
    [InlineData("claude", 4)]
    [InlineData("providers", 4)]
    [InlineData("notifications", 3)]
    [InlineData("weather", 6)]
    [InlineData("media", 0)]
    [InlineData(null, 0)]
    public void The_header_settings_button_opens_the_page_for_the_current_tab(string? tab, int page) =>
        Assert.Equal(page, Atoll.Shell.IslandWindow.SettingsPageFor(tab));

    [Fact]
    public void Disabled_modules_leave_their_partners_and_volume_accessible()
    {
        var modules = Modules().Where(t => t.Descriptor.Id is not ("media" or "clock" or "shelf")).ToList();
        var tabs = PanelGroups.Combine(modules);
        Assert.NotNull(Assert.IsType<SystemGroupViewModel>(tabs.Single(t => t.Contains("system")).Panel).Sound);
        var time = Assert.IsType<TimePanelViewModel>(tabs.Single(t => t.Contains("calendar")).Panel);
        Assert.Null(time.Clock);
        Assert.NotNull(time.Calendar);
        var files = Assert.IsType<FilesPanelViewModel>(tabs.Single(t => t.Contains("clipboard")).Panel);
        Assert.Null(files.Shelf);
        Assert.NotNull(files.Clipboard);
    }

    // Runs on the existing WPF test's STA thread; WPF permits one Application per test process.
    internal static void VerifyTemplates()
    {
        var resources = new ResourceDictionary();
        Application.Current.Resources = resources;
        foreach (var file in new[] { "Theme/Theme.xaml", "Theme/Indicators.xaml", "Modules/Claude/ClaudeTemplates.xaml", "Modules/Providers/ProviderTemplates.xaml",
            "Modules/Clock/ClockTemplates.xaml", "Modules/Media/MediaTemplates.xaml", "Modules/Notifications/NotificationTemplates.xaml",
            "Modules/Shelf/ShelfTemplates.xaml", "Modules/Clipboard/ClipboardTemplates.xaml", "Modules/Stats/StatsTemplates.xaml",
            "Modules/Weather/WeatherTemplates.xaml", "Modules/Indicators/IndicatorsTemplates.xaml", "Modules/Calendar/CalendarTemplates.xaml",
            "Modules/GroupedTemplates.xaml" })
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Atoll;component/" + file, UriKind.Relative) });
        var modules = Modules();
        var panels = PanelGroups.Combine(modules).Where(t => t.Panel.GetType() != typeof(object))
            .Select(t => (Name: t.Descriptor.Id, Panel: t.Panel)).ToList();
        panels.Add(("calendar-only", new TimePanelViewModel(null, new CalendarViewModel(() => { }))));
        panels.Add(("clock-only", new TimePanelViewModel((ClockPanelViewModel)modules.Single(t => t.Descriptor.Id == "clock").Panel, null)));
        var idleClock = (ClockPanelViewModel)modules.Single(t => t.Descriptor.Id == "clock").Panel;
        var agenda = new CalendarViewModel(() => { }) { HasSources = true, HasEvents = true, Updated = "Updated 10:30 AM" };
        var nine = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
        CalendarEventViewModel Event(string title, string? place, bool allDay, int hour, string color, bool pinned = false) =>
            new(new CalendarEventItem(title, nine.AddHours(hour - 9), nine.AddHours(hour - 8), allDay, title, place, "Work", color), _ => { }, _ => { }) { IsPinned = pinned };
        agenda.Days.Add(new CalendarDay { Label = "Today", Events = [Event("Design review", "Room 4", false, 11, "#0A84FF", pinned: true), Event("Release day", null, true, 0, "#30D158")] });
        agenda.Days.Add(new CalendarDay { Label = "Tomorrow", Events = [Event("Dentist appointment with a very long title", "Main Street", false, 14, "#FF9F0A")] });
        var runningTimer = new TimerViewModel(TimeProvider.System);
        runningTimer.StartCountdownCommand.Execute(5);
        panels.Add(("time-timer-idle", new TimePanelViewModel(idleClock, agenda)));
        panels.Add(("time-timer-running", new TimePanelViewModel(new ClockPanelViewModel(idleClock.Clock, runningTimer), agenda)));
        panels.Add(("time-no-calendars", new TimePanelViewModel(idleClock, new CalendarViewModel(() => { }))));
        panels.Add(("clipboard-only", new FilesPanelViewModel(null, new ClipboardViewModel(new ClipboardHistory()))));
        panels.Add(("shelf-only", new FilesPanelViewModel(new ShelfViewModel(_ => { }, () => { }), null)));
        panels.Add(("system-with-volume", PanelGroups.Combine(modules.Where(t => t.Descriptor.Id != "media").ToList())
            .Single(t => t.Contains("system")).Panel));
        var agents = new ClaudeViewModel(new ClaudeSessionTracker(TimeProvider.System));
        foreach (var agent in Atoll.Claude.HookProtocol.Agents)
            agents.Tracker.Apply(new Atoll.Claude.HookMessage { Agent = agent, SessionId = "s1", Event = "PreToolUse", Cwd = "C:/project", Detail = "Running tests" });
        agents.Refresh();
        panels.Add(("agents-active", agents));
        var notifications = new NotificationsViewModel(() => { }) { Access = NotificationAccess.Allowed };
        foreach (var claude in new[] { false, true })
            notifications.Items.Add(new NotificationItemViewModel(_ => { }, _ => { })
            { Id = claude ? 2u : 1u, AppName = claude ? "Claude" : "Test app", Title = "Notification rendering test", Body = "Sender icon fallback", Time = DateTimeOffset.Now, IsClaude = claude });
        notifications.ItemsChanged();
        panels.Add(("notifications-populated", notifications));
        foreach (var mode in new[] { "Compact", "Peek", "Mini" })
        {
            var icon = (FrameworkElement)((DataTemplate)resources["NotificationItemViewModel." + mode]).LoadContent();
            icon.DataContext = notifications.Items[0];
            icon.Measure(new Size(400, 120)); icon.Arrange(new Rect(0, 0, 400, 120)); icon.UpdateLayout();
        }
        foreach (var panel in panels)
        {
            var presenter = new ContentControl
            {
                Width = 476, Height = 170, Content = panel.Panel,
                ContentTemplate = (DataTemplate)resources[panel.Panel.GetType().Name + ".Panel"],
                Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"),
                Background = Brushes.Black, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            var background = new Border { Background = Brushes.Black, Child = presenter };
            background.Measure(new Size(476, 170));
            background.Arrange(new Rect(0, 0, 476, 170));
            background.UpdateLayout();
            Assert.True(presenter.ActualWidth > 0);
            if (panel.Name == "media")
            {
                // The source picker is a small label showing the followed player; playback controls stay in view.
                var picker = Descendants<ComboBox>(background).Single(c => AutomationProperties.GetName(c) == "Audio source");
                Assert.Equal("Spotify", Assert.IsType<MediaSourceChoice>(picker.SelectedItem).Name);
                Assert.InRange(picker.ActualWidth, 1, 200);
                foreach (var name in new[] { "Previous track", "Play or pause", "Next track" })
                {
                    var button = Descendants<Button>(background).Single(b => AutomationProperties.GetName(b) == name);
                    Assert.InRange(button.TranslatePoint(new Point(0, button.ActualHeight), background).Y, 1, 170);
                }
            }
            var bitmap = new RenderTargetBitmap(476, 170, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(background);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var dir = Path.Combine(Path.GetTempPath(), "atoll-panel-review");
            Directory.CreateDirectory(dir);
            using var output = File.Create(Path.Combine(dir, panel.Name + ".png"));
            encoder.Save(output);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
