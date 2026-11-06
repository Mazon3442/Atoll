using Atoll.Activities;
using Atoll.Desktop;
using Atoll.Modules.Clock;
using Atoll.Modules.Weather;
using Atoll.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

[assembly: AvaloniaTestApplication(typeof(Atoll.Desktop.Tests.TestAppBuilder))]

namespace Atoll.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().WithInterFont();
}
public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public sealed class DesktopTests
{
    private static SettingsService NewSettings(string path)
    {
        var settings = new SettingsService(new SettingsStore(path));
        foreach (var (id, _, _) in DesktopModules.Catalog) settings.Current.Modules.Add(new ModuleEntry { Id = id, Enabled = false });
        return settings;
    }

    [AvaloniaFact]
    public void Removing_a_history_image_keeps_the_clipboard_copy_readable()
    {
        var source = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(8, 8));
        var entry = new ClipboardEntry("", _ => { }, _ => { }) { Image = source };
        using var copy = entry.CopyImageForClipboard();
        source.Dispose();
        using var output = new MemoryStream(); copy.Save(output);
        Assert.True(output.Length > 0);
    }

    [AvaloniaFact]
    public void Island_expands_for_alerts_and_collapses_from_the_keyboard()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atoll-desktop-tests", Guid.NewGuid().ToString("N"));
        using var settings = NewSettings(Path.Combine(directory, "settings.json"));
        settings.Current.Modules.Single(e => e.Id == "clock").Enabled = true;
        using var activities = new ActivityHost(); using var modules = new DesktopModules(settings, activities);
        var island = new IslandWindow(settings, activities, modules, _ => { }); island.Show();
        Assert.Equal(300, island.Width);
        activities.Post(new Activity("test.alert", "clock", new IndicatorViewModel("", "Timer finished"), ActivityPriority.Critical, TimeSpan.FromSeconds(8), true));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(settings.Current.Theme.ExpandedWidth, island.Width);
        using (var frame = island.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            var output = Environment.GetEnvironmentVariable("ATOLL_TEST_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "atoll-desktop-review");
            Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "island-expanded.png"));
        }
        island.GetVisualDescendants().OfType<Button>().First().Focus();
        island.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); island.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal(300, island.Width); island.Close();
    }

    [AvaloniaTheory]
    [InlineData(420)]
    [InlineData(520)]
    public void Clock_controls_drive_the_shared_timer(int width)
    {
        var timer = new TimerViewModel(TimeProvider.System);
        var clock = new ClockViewModel(); clock.Update(new DateTime(2026, 11, 6, 12, 30, 0));
        var window = new Window { Width = width, Height = 420, Content = Views.For(new ClockPanelViewModel(clock, timer)) };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "5 min"));
        Assert.NotNull(button.Command); button.Command.Execute(button.CommandParameter);
        Assert.Equal(TimerKind.Countdown, timer.Kind); Assert.True(timer.IsRunning);
        foreach (var control in window.GetVisualDescendants().OfType<Button>())
        {
            var edge = control.TranslatePoint(new Point(control.Bounds.Width, 0), window);
            Assert.NotNull(edge); Assert.InRange(edge.Value.X, 0, window.Bounds.Width + 1);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Forecast_chart_preserves_keyboard_selection_and_missing_readings()
    {
        var path = Path.Combine(Path.GetTempPath(), "atoll-desktop-tests", Guid.NewGuid() + ".json");
        using var settings = NewSettings(path);
        using var weather = new WeatherViewModel(settings);
        weather.ChartPoints.Add(new WeatherChartPoint(DateTime.Now, 3, "3 km/h"));
        weather.ChartPoints.Add(new WeatherChartPoint(DateTime.Now.AddHours(1), null, "Unavailable"));
        var chart = new ForecastChart(weather);
        var window = new Window { Width = 520, Height = 180, Content = chart }; window.Show(); chart.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(1, weather.SelectedChartIndex); Assert.Equal("Unavailable", weather.ChartReadout);
        window.Close();
    }

    [AvaloniaFact]
    public void Every_settings_page_renders_in_light_and_dark_without_starting_modules()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atoll-desktop-tests", Guid.NewGuid().ToString("N"));
        using var settings = NewSettings(Path.Combine(directory, "settings.json"));
        using var activities = new ActivityHost(); using var modules = new DesktopModules(settings, activities);
        var window = new SettingsWindow(settings, modules, () => { });
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            window.RequestedThemeVariant = theme; window.Show();
            for (var page = 0; page < 8; page++)
            {
                window.SelectPage(page); Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                var output = Environment.GetEnvironmentVariable("ATOLL_TEST_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "atoll-desktop-review");
                Directory.CreateDirectory(output); frame.Save(Path.Combine(output, $"settings-{theme}-{page}.png"));
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), box => !double.IsFinite(box.Bounds.Width));
            }
        }
        window.Close();
    }

    [Fact]
    public void Unsupported_session_focus_is_disabled_until_a_window_is_known()
    {
        var session = new Atoll.Modules.Claude.ClaudeSessionViewModel("test");
        var changes = 0; session.FocusCommand.CanExecuteChanged += (_, _) => changes++;
        Assert.False(session.FocusCommand.CanExecute(null));
        session.Hwnd = 42;
        Assert.True(session.FocusCommand.CanExecute(null)); Assert.Equal(1, changes);
    }

    [Fact]
    public void Timer_controls_follow_the_active_timer_mode()
    {
        var timer = new TimerViewModel(TimeProvider.System);
        Assert.False(timer.PauseResumeCommand.CanExecute(null)); Assert.False(timer.ResetCommand.CanExecute(null));
        timer.StartStopwatchCommand.Execute(null);
        Assert.True(timer.PauseResumeCommand.CanExecute(null)); Assert.False(timer.AddMinuteCommand.CanExecute(null));
        timer.StartCountdownCommand.Execute("5"); Assert.True(timer.AddMinuteCommand.CanExecute(null));
        timer.ResetCommand.Execute(null); Assert.False(timer.PauseResumeCommand.CanExecute(null));
    }

    [Fact]
    public void Encrypted_calendar_links_round_trip_and_reject_corruption()
    {
        var encrypted = SecretStore.Protect("https://example.org/private-calendar.ics");
        Assert.DoesNotContain("example.org", encrypted);
        Assert.Equal("https://example.org/private-calendar.ics", SecretStore.Unprotect(encrypted));
        Assert.Null(SecretStore.Unprotect("invalid ciphertext"));
        var prefix = encrypted.StartsWith("aes1:", StringComparison.Ordinal) ? "aes1:" : "";
        var bytes = Convert.FromBase64String(encrypted[prefix.Length..]); bytes[^1] ^= 1;
        Assert.Null(SecretStore.Unprotect(prefix + Convert.ToBase64String(bytes)));
    }

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("CanIncludeInClipboardHistory")]
    [InlineData("org.nspasteboard.ConcealedType")]
    [InlineData("application/x-password-manager")]
    public void Private_clipboard_formats_are_skipped(string format) => Assert.True(ClipboardModule.IsPrivate(["text/plain", format]));

    [Fact]
    public async Task Unix_hook_commands_keep_special_path_characters_literal()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "atoll-command-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Atoll.ClaudeHook ' $dollar `literal`");
        try
        {
            File.WriteAllText(path, "#!/bin/sh\nprintf '%s' \"$1\"\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(Atoll.Modules.Claude.ClaudeHookInstaller.QuoteCommand(path) + " expected");
            using var process = System.Diagnostics.Process.Start(start)!;
            Assert.Equal("expected", await process.StandardOutput.ReadToEndAsync()); await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Debounced_settings_capture_the_ui_snapshot_and_flush_the_latest_value()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atoll-desktop-tests", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        using var service = new SettingsService(store);
        service.Current.Weather.City = "Chicago"; service.NotifyChanged();
        service.Current.Weather.City = "Unsaved edit";
        await Task.Delay(800);
        Assert.Equal("Chicago", store.Load().Weather.City);
        service.Flush(); Assert.Equal("Unsaved edit", store.Load().Weather.City);
    }
}
