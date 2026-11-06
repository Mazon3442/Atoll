using System.Windows.Input;
using Atoll.Activities;
using Atoll.Modules.Calendar;
using Atoll.Modules.Claude;
using Atoll.Modules.Clock;
using Atoll.Modules.Weather;
using Atoll.Modules.Shelf;
using Atoll.Modules.Media;
using Atoll.Modules.Indicators;
using Atoll.Modules.Actions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Atoll.Desktop;

internal static class Views
{
    internal static StackPanel Stack(params Control[] children) => new StackPanel { Spacing = 12 }.WithChildren(children);
    private static StackPanel WithChildren(this StackPanel panel, IEnumerable<Control> children) { foreach (var c in children) panel.Children.Add(c); return panel; }
    internal static StackPanel Row(params Control[] children) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }.WithChildren(children);
    internal static TextBlock Label(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    internal static TextBlock Text(string path, double size = 14)
    {
        var text = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.TextProperty, new Binding(path)); return text;
    }
    internal static Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Cursor = new Cursor(StandardCursorType.Hand), MinHeight = 32, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(8) };
        button.Click += (_, _) => action(); return button;
    }
    internal static Button Command(string title, string path, object? parameter = null)
    {
        var button = new Button { Content = title, CommandParameter = parameter, Cursor = new Cursor(StandardCursorType.Hand), MinHeight = 32, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(8) };
        button.Bind(Avalonia.Controls.Button.CommandProperty, new Binding(path)); return button;
    }
    private static Control Context(Control control, object model) { control.DataContext = model; return control; }
    internal static Control Compact(object? content)
    {
        var path = content switch
        {
            ClockViewModel => "Time", TimerViewModel => "Display", ClaudeViewModel => "Current.Project",
            ClaudeAlertViewModel => "Title", MediaViewModel => "Title", NextEventViewModel => "Title",
            CalendarAlertViewModel => "Title", IndicatorViewModel => "Title",
#if WINDOWS
            Atoll.Modules.Notifications.NotificationItemViewModel => "Title",
#endif
            _ => null,
        };
        var label = path is null ? Label("ATOLL", 14) : Text(path, 14);
        label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.MaxWidth = 480; label.DataContext = content; return label;
    }

    internal static Control For(object model)
    {
        Control pane = model switch
        {
            PanelGroup group => Stack(group.Modules.Select(m => Views.Stack(Label(m.Name, 18), For(m.Panel))).ToArray()),
            ClockPanelViewModel clock => Clock(clock), CalendarViewModel calendar => Calendar(calendar),
            WeatherViewModel weather => Weather(weather), ClaudeViewModel agents => Agents(agents),
            ShelfViewModel shelf => Shelf(shelf), ClipboardModule clipboard => Clipboard(clipboard),
            MediaViewModel media => Media(media), SystemPanelViewModel devices => Devices(devices),
            StatisticsModule => Stack(Label("System stats", 18), Text("Cpu", 32), Label("CPU"), Text("Memory", 24), Label("Memory in use"), Text("Network", 24), Label("Network traffic"), Text("Status")),
            QuickActionsViewModel => Stack(Label("Quick actions", 18), Row(Command("Screenshot", "ScreenshotCommand"), Command("Mute microphone", "ToggleMicCommand")), Row(Command("Lock screen", "LockCommand"), Command("Display off", "ScreenOffCommand"), Command("Downloads", "DownloadsCommand"))),
#if WINDOWS
            Atoll.Modules.Notifications.NotificationsViewModel notifications => Notifications(notifications),
#endif
            ModuleError error => Label(error.Message),
            _ => Label("This panel is unavailable."),
        };
        pane.DataContext = model; pane.Margin = new Thickness(4, 16, 4, 4); return pane;
    }

    private static Control Clock(ClockPanelViewModel vm) => Stack(
        Context(Stack(Text("Time", 38), Text("Date")), vm.Clock),
        Context(Stack(Text("Display", 36), Text("Label"),
            Row(Command("5 min", "StartCountdownCommand", "5"), Command("15 min", "StartCountdownCommand", "15"), Command("25 min", "StartCountdownCommand", "25")),
            Row(Command("Stopwatch", "StartStopwatchCommand"), Command("Pomodoro", "StartPomodoroCommand")),
            Row(Command("Pause / resume", "PauseResumeCommand"), Command("+1 min", "AddMinuteCommand"), Command("Reset", "ResetCommand"))), vm.Timer));

    private static Control Calendar(CalendarViewModel vm)
    {
        var days = new ItemsControl { ItemsSource = vm.Days, ItemTemplate = new FuncDataTemplate<CalendarDay>((day, _) => day is null ? null : Stack(Label(day.Label, 18), new ItemsControl
        {
            ItemsSource = day.Events, ItemTemplate = new FuncDataTemplate<CalendarEventViewModel>((item, _) => item is null ? null : Context(Stack(
                Text("Event.DisplayTitle", 16), Text("Event.Detail", 12), Text("Badge"), Row(Command("Pin / unpin", "TogglePinCommand"), Command("Hide from island", "DismissCommand"))), item)),
        })) };
        var empty = Text("EmptyMessage"); empty.Bind(Control.IsVisibleProperty, new Binding("ShowEmpty"));
        return Stack(Row(Command("Refresh", "RefreshCommand"), Command("Add calendars", "OpenSettingsCommand")), empty, days);
    }

    private static Control Weather(WeatherViewModel vm)
    {
        var days = new ComboBox { ItemsSource = vm.Days, HorizontalAlignment = HorizontalAlignment.Stretch, ItemTemplate = new FuncDataTemplate<WeatherDay>((day, _) => Label(day is null ? "" : $"{day.Name}    {day.Condition}    {day.Range}")) };
        days.Bind(ComboBox.SelectedItemProperty, new Binding("SelectedDay", BindingMode.TwoWay));
        var metrics = new ComboBox { ItemsSource = vm.Metrics, DisplayMemberBinding = new Binding("Label"), HorizontalAlignment = HorizontalAlignment.Stretch };
        metrics.SelectedItem = vm.Metrics.FirstOrDefault(m => m.Value == vm.SelectedMetric);
        metrics.SelectionChanged += (_, _) => { if (metrics.SelectedItem is Atoll.Settings.Choice<WeatherMetric> choice) vm.SelectedMetric = choice.Value; };
        var chart = new ForecastChart(vm) { Height = 138, HorizontalAlignment = HorizontalAlignment.Stretch };
        var readings = Stack(Text("PlaceLabel", 16), Text("TemperatureReading", 62), Text("ConditionSummary"), Text("Details"), Text("Wind"), Text("RainSummary"), days, metrics, Text("ChartReadout"), chart, Text("Updated", 11));
        readings.Bind(Control.IsVisibleProperty, new Binding("HasReport"));
        return Stack(Text("Status"), readings, Row(Command("Refresh", "RefreshCommand"), Command("Choose location", "OpenSettingsCommand")));
    }

    private static Control Agents(ClaudeViewModel vm)
    {
        var list = new ItemsControl { ItemsSource = vm.Tracker.Sessions, ItemTemplate = new FuncDataTemplate<ClaudeSessionViewModel>((session, _) => session is null ? null : Context(Stack(Text("Project", 18), Text("AgentName", 12), Text("Detail"), Text("Elapsed", 12), Command("Focus session", "FocusCommand")), session)) };
        var empty = Label("No agent sessions yet. Install an adapter in Settings > Agents, then start a session.");
        empty.Bind(Control.IsVisibleProperty, new Binding("!HasSessions"));
        return Stack(empty, list, Label("Claude usage today (API equivalent estimate)", 12), Context(Stack(Text("TodayText"), Text("BlockText"), Text("BlockResets")), vm.Usage), Button("Manage agent adapters", () => Navigation.OpenSettings?.Invoke(4)));
    }

    private static Control Media(MediaViewModel vm)
    {
        var art = new Image { Width = 100, Height = 100, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.UniformToFill };
        art.Bind(Image.SourceProperty, new Binding("Art"));
        var progress = new Slider { Minimum = 0, Maximum = 1 };
        progress.Bind(Slider.ValueProperty, new Binding("Progress", BindingMode.OneWay)); progress.Bind(Control.IsEnabledProperty, new Binding("CanSeek"));
        progress.PointerPressed += (_, _) => vm.IsScrubbing = true;
        progress.PointerReleased += async (_, _) => { vm.IsScrubbing = false; if (vm.CanSeek) await vm.SeekCommand.ExecuteAsync(progress.Value); };
        AutomationProperties.SetName(progress, "Playback position");
        return Stack(art, Text("Title", 24), Text("Artist"), Text("Status", 12), progress, Row(Text("PositionText", 12), Text("DurationText", 12)), Row(Command("Previous", "PreviousCommand"), Command("Play / pause", "PlayPauseCommand"), Command("Next", "NextCommand")));
    }

    private static Control Shelf(ShelfViewModel vm)
    {
        var add = Button("Add files", () => { });
        add.Click += async (_, _) =>
        {
            var storage = TopLevel.GetTopLevel(add)?.StorageProvider;
            if (storage is null) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add files to ATOLL", AllowMultiple = true });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray(); if (paths.Length > 0) vm.AddFilesCommand.Execute(paths);
        };
        var list = new ItemsControl { ItemsSource = vm.Items, ItemTemplate = new FuncDataTemplate<ShelfItem>((item, _) => item is null ? null : Context(Stack(Text("Name", 16), Text("Detail", 12), Row(Command("Open", "OpenCommand"), Command("Show folder", "RevealCommand"), Command("Remove", "RemoveCommand"))), item)) };
        var pane = Stack(Row(add, Command("Clear shelf", "ClearCommand")), Label("Drop files here to keep their paths handy. Removing an entry keeps the file."), list);
        DragDrop.SetAllowDrop(pane, true);
        pane.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        pane.AddHandler(DragDrop.DropEvent, (_, e) => { var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray(); if (files is { Length: > 0 }) vm.AddFilesCommand.Execute(files); e.Handled = true; });
        return pane;
    }

    private static Control Clipboard(ClipboardModule vm) => Stack(Text("Status", 12), Command("Clear history", "ClearCommand"), new ItemsControl
    {
        ItemsSource = vm.Items, ItemTemplate = new FuncDataTemplate<ClipboardEntry>((entry, _) => entry is null ? null : Context(Stack(Text("Preview"), Row(Command("Copy", "CopyCommand"), Command("Remove", "RemoveCommand"))), entry)),
    });

    private static Control Devices(SystemPanelViewModel vm)
    {
        var volume = new Slider { Minimum = 0, Maximum = 100 }; volume.Bind(Slider.ValueProperty, new Binding("Volume", BindingMode.TwoWay));
        AutomationProperties.SetName(volume, "Output volume");
        return Stack(Label("Output volume", 18), volume, Command("Mute / unmute", "ToggleMuteCommand"), Text("Battery"), Text("Network"), Text("Bluetooth"));
    }
#if WINDOWS
    private static Control Notifications(Atoll.Modules.Notifications.NotificationsViewModel vm) => Stack(Text("StatusText"), Row(Command("Allow access", "AllowAccessCommand"), Command("Set up", "OpenSetupCommand"), Command("Clear history", "ClearCommand")), new ItemsControl
    {
        ItemsSource = vm.Items, ItemTemplate = new FuncDataTemplate<Atoll.Modules.Notifications.NotificationItemViewModel>((item, _) => item is null ? null : Context(Stack(Text("AppName", 12), Text("Title", 16), Text("Body"), Row(Command("Mute app", "MuteCommand"), Command("Dismiss", "DismissCommand"))), item)),
    });
#endif
}
