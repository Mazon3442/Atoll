using System.Text.Json;
using Atoll.Modules.Weather;
using Atoll.Settings;

namespace Atoll.Tests;

public class WeatherForecastTests
{
    [Fact]
    public async Task Repeated_refresh_without_a_location_can_be_canceled_safely()
    {
        using var vm = new WeatherViewModel(new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsLoading);
        Assert.False(vm.HasReport);
        Assert.False(vm.HasCity);
    }

    internal static WeatherReport Example()
    {
        var time = new DateTime(2026, 11, 6, 10, 30, 0);
        return new WeatherReport(17, 15, 2, true, 18,
            Enumerable.Range(0, 7).Select(i => new ForecastDay(time.Date.AddDays(i), i == 0 ? 61 : 2, 20 - i, 10 - i, 65, 2.5, 24)).ToList())
        {
            LocalTime = time, Rain = 0, Gusts = 28, WindDirection = 225,
            Hours = Enumerable.Range(0, 168).Select(i => new WeatherHour(time.Date.AddHours(i), i is 13 or 14 ? 61 : 2, 17,
                i is 13 or 14 ? 70 : 0, i is 13 or 14 ? 1.2 : 0, i is 13 or 14 ? 1.2 : 0, 18)).ToList(),
        };
    }

    [Fact]
    public void Parses_hourly_precipitation_wind_and_null_values_without_inventing_zeroes()
    {
        using var json = JsonDocument.Parse("""
            {"current":{"time":"2026-11-06T10:30","temperature_2m":17,"apparent_temperature":15,"weather_code":61,"is_day":1,"wind_speed_10m":18,"rain":0.2,"showers":0.1,"wind_gusts_10m":28,"wind_direction_10m":225},
             "daily":{"time":["2026-11-06","2026-11-07"],"weather_code":[61,2],"temperature_2m_max":[20,18],"temperature_2m_min":[10,8],"precipitation_probability_max":[70,null]},
             "hourly":{"time":["2026-11-06T11:00","2026-11-06T12:00","2026-11-06T13:00"],"temperature_2m":[17,18,null],"weather_code":[61,2,2],"precipitation_probability":[70,null],"rain":[0.2,0,null],"showers":[0.1,0,0],"precipitation":[0.3,0,null],"wind_speed_10m":[18,null]}}
            """);
        var forecast = OpenMeteo.ParseForecast(json.RootElement);
        Assert.Equal(new DateTime(2026, 11, 6, 10, 30, 0), forecast.LocalTime);
        Assert.Equal(0.3, forecast.Rain!.Value, 5);
        Assert.Equal(225, forecast.WindDirection);
        Assert.Equal(3, forecast.Hours.Count);
        Assert.Null(forecast.Hours[1].RainChance);
        Assert.Null(forecast.Hours[1].Wind);
        Assert.Null(forecast.Hours[2].Rain);
        Assert.Null(forecast.Hours[2].Temperature);
        Assert.Null(forecast.Days[1].RainChance);
    }

    [Fact]
    public void Rain_timing_uses_the_forecast_locations_clock_and_handles_missing_data()
    {
        var report = Example();
        Assert.Contains("Rain possible around", WeatherViewModel.DescribeRain(report));
        Assert.Contains("70%", WeatherViewModel.DescribeRain(report));
        Assert.Contains("May ease", WeatherViewModel.DescribeRain(report with { Rain = 1 }));
        Assert.Equal("Rain timing unavailable.", WeatherViewModel.DescribeRain(report with { Hours = [] }));
        Assert.Contains("unavailable", WeatherViewModel.DescribeRain(report with { Rain = 1, Hours = [] }));
        Assert.Equal("No rain in the next 24-hour forecast.", WeatherViewModel.DescribeRain(report with { Hours = report.Hours.Select(h => h with { Rain = 0 }).ToList() }));
        Assert.Contains("available hourly", WeatherViewModel.DescribeRain(report with { Hours = report.Hours.Skip(20).Take(2).ToList() }));
    }

    [Fact]
    public void Selecting_a_day_changes_hourly_conditions_and_keeps_wind_units()
    {
        using var vm = new WeatherViewModel(new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
        vm.Show(Example(), new Place("Chicago", 41.85, -87.65), false);
        Assert.Equal(7, vm.Days.Count);
        Assert.Equal("Today", vm.SelectedDay?.Name);
        Assert.Equal(24, vm.Hours.Count); // the whole day, not just the hours left
        Assert.Contains("18 km/h SW", vm.Wind);
        vm.SelectedDay = vm.Days[1];
        Assert.Equal(24, vm.Hours.Count);
        Assert.Contains("Nov 7", vm.DaySummary);
        vm.Show(Example(), new Place("Chicago", 41.85, -87.65), true);
        Assert.All(vm.ChartPoints, h => Assert.Contains("mph", h.Detail));
        Assert.Contains("in total", vm.DaySummary);
    }

    [Theory]
    [InlineData(90, 180, true)]
    [InlineData(-90, -180, true)]
    [InlineData(91, 0, false)]
    [InlineData(0, -181, false)]
    [InlineData(double.NaN, 0, false)]
    public void Coordinates_are_validated(double lat, double lon, bool valid) => Assert.Equal(valid, OpenMeteo.ValidCoordinates(lat, lon));

    [Fact]
    public void Search_offers_distinct_places_and_skips_malformed_coordinates()
    {
        using var json = JsonDocument.Parse("""{"results":[{"name":"Paris","country":"France","latitude":48.85,"longitude":2.35},{"name":"Paris","country":"United States","latitude":33.66,"longitude":-95.55},{"name":"Bad","latitude":null,"longitude":0}]}""");
        var results = OpenMeteo.ParsePlaces(json.RootElement);
        Assert.Equal(2, results.Count);
        Assert.NotEqual(results[0].Name, results[1].Name);
    }
}
