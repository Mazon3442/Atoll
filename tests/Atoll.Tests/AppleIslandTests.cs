using Atoll.Modules.Claude;
using Atoll.Modules.Weather;
using Atoll.Settings;
using Atoll.Shell;
using System.Windows;

namespace Atoll.Tests;

public class AppleIslandTests
{
    [Fact]
    public async Task Agent_action_tracks_install_update_and_current_adapter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atoll-agent-actions-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var executable = Path.Combine(dir, "Atoll.ClaudeHook.exe");
            File.WriteAllText(executable, "test bridge");
            var installer = new AgentIntegrationInstaller(dir, executable);
            var row = new AgentIntegrationRow("opencode", installer);
            Assert.Equal("Install", row.ActionLabel);
            await row.InstallCommand.ExecuteAsync(null);
            Assert.Equal("Installed", row.ActionLabel);
            Assert.False(row.InstallCommand.CanExecute(null));
            File.AppendAllText(installer.PathFor("opencode"), "\n// older adapter\n");
            row.Refresh();
            Assert.Equal("Update", row.ActionLabel);
            Assert.True(row.InstallCommand.CanExecute(null));
            await row.InstallCommand.ExecuteAsync(null);
            Assert.Equal("Installed", row.ActionLabel);
            await row.RemoveCommand.ExecuteAsync(null);
            Assert.Equal("Install", row.ActionLabel);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Closing_settings_keeps_the_running_modules_forecast_alive()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        using var weather = new WeatherViewModel(service);
        var settings = new SettingsViewModel(service, [], weather);
        settings.Dispose();
        await weather.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("Settings", weather.Status);
    }

    [Fact]
    public async Task Disabling_the_weather_module_leaves_settings_with_a_working_forecast()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        using var weather = new WeatherViewModel(service);
        using var settings = new SettingsViewModel(service, [], weather);
        weather.Dispose();
        Assert.NotSame(weather, settings.Weather);
        await settings.Weather.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("Settings", settings.Weather.Status);
    }

    [Fact]
    public void Chart_metrics_preserve_missing_readings_and_use_the_selected_day()
    {
        using var vm = new WeatherViewModel(new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
        var report = WeatherForecastTests.Example();
        var hours = report.Hours.ToList();
        hours[10] = hours[10] with { Wind = null, RainChance = null, Precipitation = null };
        vm.Show(report with { Hours = hours }, new Place("Chicago", 41.85, -87.65), false);
        Assert.Null(vm.ChartPoints[0].Value);
        Assert.Contains("Unavailable", vm.ChartReadout);
        vm.SelectedMetric = WeatherMetric.Precipitation;
        Assert.Null(vm.ChartPoints[0].Value);
        vm.SelectedDay = vm.Days[1];
        Assert.Equal(24, vm.ChartPoints.Count);
        Assert.All(vm.ChartPoints, p => Assert.Equal(report.LocalTime.Date.AddDays(1), p.Time.Date));
        vm.SelectedMetric = WeatherMetric.Temperature;
        Assert.Equal(17, vm.ChartPoints[0].Value);
    }

    [Theory]
    [InlineData(1.4, 2.7, 130.2, 34.1, 1, 2, 131, 35)]
    [InlineData(-10.5, -20.5, 50, 25, -11, -21, 51, 26)]
    public void Glass_capture_bounds_round_outward_without_capturing_the_whole_canvas(double x, double y, double width, double height, int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new Int32Rect(expectedX, expectedY, expectedWidth, expectedHeight), GlassSampler.CaptureBounds(new Rect(x, y, width, height)));
    }
}
