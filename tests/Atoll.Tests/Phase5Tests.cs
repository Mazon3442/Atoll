using System.Text.Json;
using Atoll.Modules.Clipboard;
using Atoll.Modules.Shelf;
using Atoll.Modules.Stats;
using Atoll.Modules.Weather;

namespace Atoll.Tests;

public class ShelfTests
{
    [Fact]
    public void Merge_puts_new_first_dedupes_case_insensitively_and_caps()
    {
        var merged = ShelfStore.Merge([@"C:\a.txt", @"C:\b.txt"], [@"C:\B.TXT", @"C:\c.txt"]);
        Assert.Equal([@"C:\B.TXT", @"C:\c.txt", @"C:\a.txt"], merged);

        var many = Enumerable.Range(0, 80).Select(i => $@"C:\f{i}").ToList();
        Assert.Equal(ShelfStore.MaxItems, ShelfStore.Merge([], many).Count);
    }

    [Fact]
    public void Store_round_trips_and_survives_a_corrupt_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atoll-tests-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "shelf.json");
        try
        {
            var store = new ShelfStore(file);
            store.Save([@"C:\x.png"]);
            Assert.Equal([@"C:\x.png"], store.Load());

            File.WriteAllText(file, "{ nope");
            Assert.Empty(store.Load());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

public class ClipboardHistoryTests
{
    private static ClipItem Text(string s) => new(ClipKind.Text, _ => { }, _ => { }) { Text = s };

    [Fact]
    public void Copying_the_same_text_again_moves_it_to_the_top()
    {
        var h = new ClipboardHistory();
        h.Add(Text("one"));
        h.Add(Text("two"));
        h.Add(Text("one"));
        Assert.Equal(["one", "two"], h.Items.Select(i => i.Text));
    }

    [Fact]
    public void History_is_capped()
    {
        var h = new ClipboardHistory();
        for (var i = 0; i < 40; i++) h.Add(Text($"item {i}"));
        Assert.Equal(ClipboardHistory.MaxItems, h.Items.Count);
        Assert.Equal("item 39", h.Items[0].Text);
    }

    [Fact]
    public void Preview_is_a_single_trimmed_line()
    {
        Assert.Equal("first line second line", Text("  first line\r\n\tsecond line \n").Preview);
        Assert.Equal(200, Text(new string('x', 500)).Preview.Length);
    }
}

public class StatsTests
{
    [Fact]
    public void Cpu_usage_from_system_times()
    {
        // 1000 ticks elapsed (kernel includes idle): 250 idle → 75% busy.
        Assert.Equal(0.75, CpuSampler.Usage((0, 0, 0), (250, 600, 400)), 3);
        Assert.Equal(0, CpuSampler.Usage((5, 5, 5), (5, 5, 5)));
    }

    [Fact]
    public void Sparkline_ends_at_the_right_edge_and_closes_to_the_baseline()
    {
        var points = StatViewModel.BuildSparkline([0.0, 0.5, 1.0]);
        Assert.Equal(5, points.Count);
        Assert.Equal(StatViewModel.ChartWidth, points[^2].X, 3);
        Assert.Equal(0, points[^2].Y, 3); // 100% is the top
        Assert.Equal(StatViewModel.ChartHeight, points[0].Y, 3);
    }

    [Theory]
    [InlineData(512, "512 B/s")]
    [InlineData(2_500, "3 KB/s")]
    [InlineData(12_300_000, "12.3 MB/s")]
    public void Network_rates_are_human_readable(double rate, string expected) =>
        Assert.Equal(expected, StatsModule.Rate(rate));
}

public class WeatherTests
{
    [Fact]
    public void Parses_open_meteo_forecast()
    {
        var json = """
            {"current":{"temperature_2m":21.4,"apparent_temperature":20.1,"weather_code":2,"is_day":1,"wind_speed_10m":12.0},
             "daily":{"time":["2026-11-06","2026-11-07"],"weather_code":[2,61],
                      "temperature_2m_max":[22.0,18.5],"temperature_2m_min":[12.0,10.2]}}
            """;
        var r = OpenMeteo.ParseForecast(JsonDocument.Parse(json).RootElement);
        Assert.Equal(21.4, r.Temperature);
        Assert.Equal(2, r.Code);
        Assert.True(r.IsDay);
        Assert.Equal(2, r.Days.Count);
        Assert.Equal(61, r.Days[1].Code);
    }

    [Fact]
    public void Parses_geocoding_and_handles_no_match()
    {
        var hit = OpenMeteo.ParsePlace(JsonDocument.Parse("""
            {"results":[{"name":"Chicago","admin1":"Illinois","country":"United States","latitude":41.85,"longitude":-87.65}]}
            """).RootElement);
        Assert.Equal("Chicago, Illinois, United States", hit?.Name);
        Assert.Null(OpenMeteo.ParsePlace(JsonDocument.Parse("{}").RootElement));
    }

    [Theory]
    [InlineData(0, true, "Clear")]
    [InlineData(0, false, "Clear night")]
    [InlineData(63, true, "Rain")]
    [InlineData(75, true, "Snow")]
    [InlineData(95, true, "Thunderstorm")]
    public void Weather_codes_have_names(int code, bool isDay, string expected) =>
        Assert.Equal(expected, WeatherCodes.Describe(code, isDay).Text);
}
