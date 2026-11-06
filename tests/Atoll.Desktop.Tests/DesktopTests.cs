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
    public void Clock_controls_drive_the_shared_timer()
    {
        var timer = new TimerViewModel(TimeProvider.System);
        var clock = new ClockViewModel(); clock.Update(new DateTime(2026, 11, 6, 12, 30, 0));
        var window = new Window { Width = 520, Height = 420, Content = Views.For(new ClockPanelViewModel(clock, timer)) };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "5 min"));
        Assert.NotNull(button.Command); button.Command.Execute(button.CommandParameter);
        Assert.Equal(TimerKind.Countdown, timer.Kind); Assert.True(timer.IsRunning);
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
    public void Encrypted_calendar_links_round_trip_and_reject_corruption()
    {
        var encrypted = SecretStore.Protect("https://example.org/private-calendar.ics");
        Assert.DoesNotContain("example.org", encrypted);
        Assert.Equal("https://example.org/private-calendar.ics", SecretStore.Unprotect(encrypted));
        Assert.Null(SecretStore.Unprotect("invalid ciphertext"));
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
