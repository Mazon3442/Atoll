using System.Windows;
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

namespace Atoll.Tests;

public class GroupedPanelsTests
{
    internal static List<ModuleTab> Modules()
    {
        var clock = new ClockViewModel();
        clock.Update(new DateTime(2026, 11, 6, 10, 30, 0));
        var player = new MediaViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask)
        { HasSession = true, HasTimeline = true, CanSeek = true, Title = "A song with a longer title", Artist = "Artist name", AppName = "Music player", PositionText = "1:23", DurationText = "4:30", Progress = 0.3 };
        var sound = new SystemPanelViewModel(_ => { }, () => { })
        { Volume = 50, Network = "Connected", Bluetooth = "Headphones", Battery = "85%" };
        var panels = new Dictionary<string, object>
        {
            ["claude"] = new ClaudeViewModel(new ClaudeSessionTracker(TimeProvider.System)),
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
        };
        return ModuleCatalog.All.Select(d => new ModuleTab(d, panels[d.Id])).ToList();
    }

    [Fact]
    public void Eleven_modules_become_seven_tabs_with_stable_identity_and_legacy_routes()
    {
        var modules = Modules();
        var tabs = PanelGroups.Combine(modules);
        Assert.Equal(["claude", "media", "notifications", "clock", "system", "shelf", "weather"], tabs.Select(t => t.Descriptor.Id));
        Assert.Equal(tabs, PanelGroups.Combine(modules));
        foreach (var module in modules)
            Assert.Single(tabs, t => t.Contains(module.Descriptor.Id));
        Assert.True(tabs.Single(t => t.Descriptor.Name == "Agents").Contains("agents"));
        Assert.True(tabs.Single(t => t.Descriptor.Name == "Files").Contains("files"));
        Assert.Same(modules.Single(t => t.Descriptor.Id == "system").Panel,
            Assert.IsType<MediaPanelViewModel>(tabs.Single(t => t.Contains("media")).Panel).Sound);
    }

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
        foreach (var file in new[] { "Theme/Theme.xaml", "Theme/Indicators.xaml", "Modules/Claude/ClaudeTemplates.xaml",
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
        panels.Add(("clipboard-only", new FilesPanelViewModel(null, new ClipboardViewModel(new ClipboardHistory()))));
        panels.Add(("shelf-only", new FilesPanelViewModel(new ShelfViewModel(_ => { }, () => { }), null)));
        panels.Add(("system-with-volume", PanelGroups.Combine(modules.Where(t => t.Descriptor.Id != "media").ToList())
            .Single(t => t.Contains("system")).Panel));
        var agents = new ClaudeViewModel(new ClaudeSessionTracker(TimeProvider.System));
        foreach (var agent in Atoll.Claude.HookProtocol.Agents)
            agents.Tracker.Apply(new Atoll.Claude.HookMessage { Agent = agent, SessionId = "s1", Event = "PreToolUse", Cwd = "C:/project", Detail = "Running tests" });
        agents.Refresh();
        panels.Add(("agents-active", agents));
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
}
