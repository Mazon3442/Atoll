using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atoll.Activities;
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
        var launcher = new Atoll.Modules.Launcher.LauncherViewModel(Dispatcher.CurrentDispatcher);
        launcher.SetAppsForTest([
            new("Visual Studio Code", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Visual Studio Code.lnk", "Development"),
            new("Spotify", @"C:\Users\mason\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Spotify.lnk", "Music"),
            new("Steam", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Steam.lnk", "Games"),
        ]);
        var panels = new Dictionary<string, object>
        {
            ["claude"] = new ClaudeViewModel(new ClaudeSessionTracker(TimeProvider.System)),
            ["providers"] = new ProviderViewModel(),
            ["media"] = player,
            ["notifications"] = new object(),
            ["clock"] = new ClockPanelViewModel(clock, new TimerViewModel(TimeProvider.System)),
            ["calendar"] = new CalendarViewModel(() => { }),
            ["notes"] = new Atoll.Modules.Notes.NotesViewModel(new Atoll.Settings.NotesSettings(), () => { }),
            ["tide"] = new Atoll.Modules.Tide.TideViewModel(new Atoll.Settings.TideSettings(), () => { }),
            ["system"] = sound,
            ["shelf"] = new ShelfViewModel(_ => { }, () => { }),
            ["clipboard"] = new ClipboardViewModel(new ClipboardHistory()),
            ["stats"] = new StatsViewModel(),
            ["weather"] = new object(),
            ["actions"] = new QuickActionsViewModel(new QuickActionsModule()),
            ["privacy"] = new object(), // no panel of its own; it belongs to the System tab
            ["downloads"] = new object(), // no panel of its own; it only posts activities
            ["launcher"] = launcher,
            ["games"] = new Atoll.Modules.Games.GameViewModel(),
        };
        return ModuleCatalog.All.Select(d => new ModuleTab(d, panels[d.Id])).ToList();
    }

    [Fact]
    public void Modules_become_tabs_with_stable_identity_and_legacy_routes()
    {
        var modules = Modules();
        var tabs = PanelGroups.Combine(modules);
        Assert.Equal(["claude", "providers", "media", "notifications", "clock", "calendar", "notes", "tide", "system", "shelf", "downloads", "clipboard", "weather", "launcher"],
            tabs.Select(t => t.Descriptor.Id));
        Assert.Equal(tabs, PanelGroups.Combine(modules));
        foreach (var module in modules)
            Assert.Single(tabs, t => t.Contains(module.Descriptor.Id));
        Assert.True(tabs.Single(t => t.Descriptor.Name == "Agents").Contains("agents"));
        Assert.True(tabs.Single(t => t.Descriptor.Name == "Files").Contains("files"));
        Assert.Same(modules.Single(t => t.Descriptor.Id == "system").Panel,
            Assert.IsType<MediaPanelViewModel>(tabs.Single(t => t.Contains("media")).Panel).Sound);
    }

    [Theory]
    [InlineData("clock", 2)]
    [InlineData("calendar", 5)]
    [InlineData("notes", 2)]
    [InlineData("tide", 2)]
    [InlineData("launcher", 2)]
    [InlineData("shelf", 7)]
    [InlineData("clipboard", 7)]
    [InlineData("claude", 4)]
    [InlineData("providers", 4)]
    [InlineData("notifications", 3)]
    [InlineData("weather", 6)]
    [InlineData("media", 7)]
    [InlineData("system", 2)]
    [InlineData("downloads", 2)]
    [InlineData("home", 2)]
    [InlineData("games", 8)]
    [InlineData(null, 0)]
    public void The_header_settings_button_opens_the_page_for_the_current_tab(string? tab, int page) =>
        Assert.Equal(page, Atoll.Shell.IslandWindow.SettingsPageFor(tab));

    [Fact]
    public void The_idle_clock_opens_home_not_the_timer()
    {
        var clock = new Activity("time", "clock", new IndicatorViewModel("", "14:20", "Wednesday, 10 November"), ActivityPriority.Idle);
        Assert.Equal("home", Atoll.Shell.IslandWindow.ActivityTabFor(clock, hasMediaTab: true));

        // A running countdown is a real timer, so clicking it belongs on the Timer tab.
        var countdown = new Activity("timer", "clock", new IndicatorViewModel("", "04:12", "Focus"), ActivityPriority.Normal);
        Assert.Equal("clock", Atoll.Shell.IslandWindow.ActivityTabFor(countdown, hasMediaTab: true));
    }

    [Fact]
    public void Volume_opens_media_and_other_activities_open_their_own_tab()
    {
        var volume = new Activity("volume", "system", new IndicatorViewModel("", "40", "Volume"), ActivityPriority.Normal);
        Assert.Equal("media", Atoll.Shell.IslandWindow.ActivityTabFor(volume, hasMediaTab: true));
        Assert.Equal("system", Atoll.Shell.IslandWindow.ActivityTabFor(volume, hasMediaTab: false));

        var media = new Activity("media", "media", new IndicatorViewModel("", "Song", "Artist"), ActivityPriority.Normal);
        Assert.Equal("media", Atoll.Shell.IslandWindow.ActivityTabFor(media, hasMediaTab: true));
        Assert.Null(Atoll.Shell.IslandWindow.ActivityTabFor(null, hasMediaTab: true));
    }

    [Fact]
    public void Disabled_modules_leave_their_partners_and_volume_accessible()
    {
        var modules = Modules().Where(t => t.Descriptor.Id is not ("media" or "clock" or "shelf")).ToList();
        var tabs = PanelGroups.Combine(modules);
        Assert.NotNull(Assert.IsType<SystemGroupViewModel>(tabs.Single(t => t.Contains("system")).Panel).Sound);
        Assert.IsType<CalendarViewModel>(tabs.Single(t => t.Contains("calendar")).Panel);
        Assert.NotNull(Assert.IsType<ClipboardViewModel>(tabs.Single(t => t.Contains("clipboard")).Panel));
        Assert.DoesNotContain(tabs, t => t.Contains("shelf"));
    }

    // Runs on the existing WPF test's STA thread; WPF permits one Application per test process.
    internal static void VerifyTemplates()
    {
        var resources = new ResourceDictionary();
        Application.Current.Resources = resources;
        foreach (var file in new[] { "Theme/Theme.xaml", "Theme/Indicators.xaml", "Modules/Claude/ClaudeTemplates.xaml", "Modules/Providers/ProviderTemplates.xaml",
            "Modules/Clock/ClockTemplates.xaml", "Modules/Media/MediaTemplates.xaml", "Modules/Notifications/NotificationTemplates.xaml",
            "Modules/Shelf/ShelfTemplates.xaml", "Modules/Downloads/DownloadsTemplates.xaml", "Modules/Clipboard/ClipboardTemplates.xaml", "Modules/Stats/StatsTemplates.xaml",
            "Modules/Weather/WeatherTemplates.xaml", "Modules/Indicators/IndicatorsTemplates.xaml", "Modules/Calendar/CalendarTemplates.xaml",
            "Modules/Games/GamesTemplates.xaml", "Modules/Launcher/LauncherTemplates.xaml", "Modules/Notes/NotesTemplates.xaml",
            "Modules/Tide/TideTemplates.xaml",
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
        agenda.SelectedDate = new DateTime(2026, 11, 6);
        agenda.VisibleMonth = new DateTime(2026, 11, 1);
        agenda.SelectedDayTitle = "Today";
        agenda.SelectedDayDate = "Nov 6";
        agenda.SelectedDayCount = "2 events";
        agenda.HasSelectedDayEvents = true;
        agenda.SelectedDayRows =
        [
            Event("Design review", "Room 4", false, 11, "#0A84FF", pinned: true),
            Event("Release day", null, true, 0, "#30D158"),
        ];
        agenda.MonthCells = CalendarViewModel.BuildMonth(new DateTime(2026, 11, 1), new DateTime(2026, 11, 6), new DateTime(2026, 11, 6),
            new Dictionary<DateTime, CalendarDayMark> { [new DateTime(2026, 11, 6)] = new(2, ["#0A84FF", "#30D158"]) });
        var runningTimer = new TimerViewModel(TimeProvider.System);
        runningTimer.StartCountdownCommand.Execute(5);
        var pomodoroTimer = new TimerViewModel(TimeProvider.System) { SectionIndex = 1, FocusMinutes = 40 };
        var narrowPomodoro = new TimerViewModel(TimeProvider.System) { SectionIndex = 1, FocusMinutes = 40 };
        panels.Add(("time-timer-idle", new TimePanelViewModel(idleClock, agenda)));
        panels.Add(("time-pomodoro", new ClockPanelViewModel(idleClock.Clock, pomodoroTimer)));
        panels.Add(("time-pomodoro-narrow", new ClockPanelViewModel(idleClock.Clock, narrowPomodoro)));
        panels.Add(("time-timer-running", new TimePanelViewModel(new ClockPanelViewModel(idleClock.Clock, runningTimer), agenda)));
        panels.Add(("time-no-calendars", new TimePanelViewModel(idleClock, new CalendarViewModel(() => { }))));
        panels.Add(("clipboard-only", new FilesPanelViewModel(null, new ClipboardViewModel(new ClipboardHistory()))));
        var clipboardHistory = new ClipboardHistory();
        for (var i = 0; i < 3; i++)
            clipboardHistory.Add(new ClipItem(ClipKind.Text, _ => { }, _ => { }) { Text = $"Copied note {i}" });
        var clipboardPanel = new ClipboardViewModel(clipboardHistory);
        clipboardPanel.Configure(enabled: true);
        panels.Add(("clipboard-populated", clipboardPanel));
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
        var demoScripts = new Atoll.Settings.TideSettings
        {
            HideFromCapture = false,
            Scripts =
            [
                new() { Title = "Q3 launch kickoff", Body = "Good morning, everyone, and thanks for joining.\n\nThree months ago we set out to make the weekly planning call shorter.\n\nToday the median team spends eleven minutes on it — down from thirty-four.\n\nSo let's talk about what changed, and what comes next." },
                new() { Title = "Podcast intro", Body = "Welcome back." },
                new() { Title = "Standup notes", Body = "Yesterday, today, blockers." },
            ],
        };
        demoScripts.SelectedScriptId = demoScripts.Scripts[0].Id;
        panels.Add(("tide-scripts", new Atoll.Modules.Tide.TideViewModel(demoScripts, () => { })));
        panels.Add(("tide-prompt", new Atoll.Modules.Tide.TideViewModel(demoScripts, () => { })
        {
            SectionIndex = 1,
            Offset = 60,
        }));
        panels.Add(("tide-listen", new Atoll.Modules.Tide.TideViewModel(new Atoll.Settings.TideSettings(), () => { })
        {
            SectionIndex = 2,
        }));
        foreach (var mode in new[] { "Compact", "Peek", "Mini" })
        {
            var icon = (FrameworkElement)((DataTemplate)resources["NotificationItemViewModel." + mode]).LoadContent();
            icon.DataContext = notifications.Items[0];
            icon.Measure(new Size(400, 120)); icon.Arrange(new Rect(0, 0, 400, 120)); icon.UpdateLayout();
        }

        // Downloads: the live line, the side dot, and the finished alert with its two actions.
        var downloading = new Atoll.Modules.Downloads.DownloadsViewModel();
        downloading.Update("report.pdf", 1, 45L * 1024 * 1024);
        var finished = new Atoll.Modules.Downloads.DownloadCompleteViewModel(
            Path.Combine(Path.GetTempPath(), "report.pdf"), 45L * 1024 * 1024);
        foreach (var (model, type) in new (object, string)[] { (downloading, "DownloadsViewModel"), (finished, "DownloadCompleteViewModel") })
        foreach (var mode in new[] { "Compact", "Peek", "Mini" })
        {
            var view = (FrameworkElement)((DataTemplate)resources[type + "." + mode]).LoadContent();
            view.DataContext = model;
            view.Measure(new Size(400, 120)); view.Arrange(new Rect(0, 0, 400, 120)); view.UpdateLayout();
            Assert.True(view.ActualWidth > 0, $"{type}.{mode} renders");
        }
        var finishedPeek = (FrameworkElement)((DataTemplate)resources["DownloadCompleteViewModel.Peek"]).LoadContent();
        finishedPeek.DataContext = finished;
        finishedPeek.Measure(new Size(400, 120)); finishedPeek.Arrange(new Rect(0, 0, 400, 120)); finishedPeek.UpdateLayout();
        Assert.Equal(["Open report.pdf", "Show report.pdf in its folder"],
            Descendants<Button>(finishedPeek).Select(AutomationProperties.GetName));

        foreach (var panel in panels)
        {
            var narrow = panel.Name == "time-pomodoro-narrow";
            var tide = panel.Name.StartsWith("tide", StringComparison.Ordinal);
            var frameWidth = tide ? 820 : narrow ? 330 : 476;
            var frameHeight = tide ? 420 : panel.Name.StartsWith("time-pomodoro", StringComparison.Ordinal) ? 330 : 170;
            var presenter = new ContentControl
            {
                Width = frameWidth, Height = frameHeight, Content = panel.Panel,
                ContentTemplate = (DataTemplate)resources[panel.Panel.GetType().Name + ".Panel"],
                Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"),
                Background = Brushes.Black, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            var background = new Border { Background = Brushes.Black, Child = presenter };
            background.Measure(new Size(frameWidth, frameHeight));
            background.Arrange(new Rect(0, 0, frameWidth, frameHeight));
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
                    Assert.InRange(button.TranslatePoint(new Point(0, button.ActualHeight), background).Y, 1, frameHeight);
                }
            }
            if (panel.Name == "tide-listen")
            {
                // Listen keeps the same Start/Stop button shape and renders the transcript list and notes pane.
                var start = Descendants<Button>(background).Single(b => AutomationProperties.GetName(b) == "Start listening");
                Assert.Equal("Listen", start.Content);
                Assert.NotNull(Descendants<ListBox>(background).Single(l => AutomationProperties.GetName(l) == "Meeting transcript"));
                Assert.NotNull(Descendants<ComboBox>(background).Single(c => AutomationProperties.GetName(c) == "Notes source"));
            }
            if (panel.Name == "time-pomodoro")
            {
                // The Pomodoro section replaces the timer presets and starts with the configured length.
                Assert.Equal(Visibility.Collapsed, Descendants<StackPanel>(background).Single(p => p.Name == "TimerSection").Visibility);
                Assert.Equal(Visibility.Visible, Descendants<StackPanel>(background).Single(p => p.Name == "PomodoroSection").Visibility);
                var start = Descendants<Button>(background).Single(b => AutomationProperties.GetName(b) == "Start Pomodoro");
                Assert.Equal("Start 40 min focus", start.ToolTip);
            }
            if (narrow)
            {
                // Each row keeps its steppers on screen at a narrow width, and the stats row stays visible.
                var sessions = Descendants<Button>(background).Single(b => AutomationProperties.GetName(b) == "Decrease sessions before long break");
                Assert.InRange(sessions.TranslatePoint(new Point(0, sessions.ActualHeight), background).Y, 1, frameHeight);
                var start = Descendants<Button>(background).Single(b => AutomationProperties.GetName(b) == "Start Pomodoro");
                Assert.InRange(start.TranslatePoint(new Point(0, start.ActualHeight), background).Y, 1, frameHeight);
            }
            if (panel.Name == "tide-prompt")
            {
                Assert.Equal(Visibility.Visible, Descendants<ScrollViewer>(background).Single(s => AutomationProperties.GetName(s) == "Teleprompter").Visibility);
                Assert.Equal(Visibility.Visible, Descendants<Button>(background).Single(b => AutomationProperties.GetName(b) == "Start or pause the prompter").Visibility);
            }
            var bitmap = new RenderTargetBitmap((int)frameWidth, (int)frameHeight, 96, 96, PixelFormats.Pbgra32);
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
