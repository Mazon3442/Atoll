using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Atoll.Modules.Weather;

public sealed record Place(string Name, double Latitude, double Longitude);
public sealed record WeatherHour(DateTime Time, int Code, double? Temperature, double? RainChance, double? Precipitation, double? Rain, double? Wind)
{
    public bool IsDay { get; init; } = true;
    public double? Gusts { get; init; }
}
public sealed record ForecastDay(DateTime Date, int Code, double High, double Low, double? RainChance, double? Precipitation, double? Wind);
public sealed record WeatherReport(double Temperature, double FeelsLike, int Code, bool IsDay, double Wind, IReadOnlyList<ForecastDay> Days)
{
    public DateTime LocalTime { get; init; }
    public double? Rain { get; init; }
    public double? Gusts { get; init; }
    public double? WindDirection { get; init; }
    public IReadOnlyList<WeatherHour> Hours { get; init; } = [];
    public double? Humidity { get; init; }
    public DateTime? Sunrise { get; init; }
    public DateTime? Sunset { get; init; }
    public double? UvIndex { get; init; }
}

/// <summary>Open-Meteo requests contain only the search text or chosen coordinates.</summary>
public static class OpenMeteo
{
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ATOLL/1.0");
        return client;
    }

    public static async Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken ct)
    {
        var name = Uri.EscapeDataString(query.Split(',')[0].Trim());
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name={name}", ct));
        return ParsePlaces(doc.RootElement);
    }

    public static async Task<Place?> FindAsync(string city, CancellationToken ct)
    {
        var places = await SearchAsync(city, ct);
        return places.FirstOrDefault(p => p.Name.Equals(city, StringComparison.OrdinalIgnoreCase)) ?? places.FirstOrDefault();
    }

    public static async Task<WeatherReport> ForecastAsync(Place place, bool fahrenheit, CancellationToken ct)
    {
        if (!ValidCoordinates(place.Latitude, place.Longitude)) throw new ArgumentOutOfRangeException(nameof(place));
        var inv = CultureInfo.InvariantCulture;
        var url = "https://api.open-meteo.com/v1/forecast"
            + $"?latitude={place.Latitude.ToString(inv)}&longitude={place.Longitude.ToString(inv)}"
            + "&current=temperature_2m,apparent_temperature,weather_code,is_day,wind_speed_10m,wind_direction_10m,wind_gusts_10m,rain,showers,relative_humidity_2m"
            + "&hourly=temperature_2m,weather_code,is_day,precipitation_probability,precipitation,rain,showers,wind_speed_10m,wind_gusts_10m"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum,wind_speed_10m_max,sunrise,sunset,uv_index_max"
            + "&timezone=auto&forecast_days=7"
            + (fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph&precipitation_unit=inch" : "&wind_speed_unit=kmh");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        return ParseForecast(doc.RootElement);
    }

    public static bool ValidCoordinates(double latitude, double longitude) => double.IsFinite(latitude) && double.IsFinite(longitude)
        && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    internal static Place? ParsePlace(JsonElement root) => ParsePlaces(root).FirstOrDefault();
    internal static IReadOnlyList<Place> ParsePlaces(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        var places = new List<Place>();
        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("latitude", out var lat) || lat.ValueKind != JsonValueKind.Number || !lat.TryGetDouble(out var latitude)
                || !result.TryGetProperty("longitude", out var lon) || lon.ValueKind != JsonValueKind.Number || !lon.TryGetDouble(out var longitude)
                || !ValidCoordinates(latitude, longitude)) continue;
            var parts = new[] { Str(result, "name"), Str(result, "admin1"), Str(result, "country") }
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct();
            var name = string.Join(", ", parts);
            if (name.Length > 0) places.Add(new Place(name, latitude, longitude));
        }
        return places;
    }

    internal static WeatherReport ParseForecast(JsonElement root)
    {
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        var days = new List<ForecastDay>();
        for (var i = 0; i < daily.GetProperty("time").GetArrayLength(); i++)
        {
            if (!TimeAt(daily, i, out var date) || NumberAt(daily, "weather_code", i) is not { } code
                || NumberAt(daily, "temperature_2m_max", i) is not { } high || NumberAt(daily, "temperature_2m_min", i) is not { } low) continue;
            days.Add(new ForecastDay(date, (int)code, high, low, NumberAt(daily, "precipitation_probability_max", i),
                NumberAt(daily, "precipitation_sum", i), NumberAt(daily, "wind_speed_10m_max", i)));
        }
        var hours = new List<WeatherHour>();
        if (root.TryGetProperty("hourly", out var hourly) && hourly.TryGetProperty("time", out var times) && times.ValueKind == JsonValueKind.Array)
        {
            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                if (!TimeAt(hourly, i, out var time)) continue;
                var code = NumberAt(hourly, "weather_code", i);
                var temp = NumberAt(hourly, "temperature_2m", i);
                var rain = NumberAt(hourly, "rain", i);
                var showers = NumberAt(hourly, "showers", i);
                hours.Add(new WeatherHour(time, code is { } knownCode ? (int)knownCode : -1, temp, NumberAt(hourly, "precipitation_probability", i),
                    NumberAt(hourly, "precipitation", i), rain is null ? null : rain + (showers ?? 0), NumberAt(hourly, "wind_speed_10m", i))
                { IsDay = NumberAt(hourly, "is_day", i) != 0, Gusts = NumberAt(hourly, "wind_gusts_10m", i) });
            }
        }
        DateTime.TryParse(Str(current, "time"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var localTime);
        var report = new WeatherReport(RequiredNumber(current, "temperature_2m"), RequiredNumber(current, "apparent_temperature"),
            current.GetProperty("weather_code").GetInt32(), current.GetProperty("is_day").GetInt32() == 1,
            RequiredNumber(current, "wind_speed_10m"), days)
        {
            LocalTime = localTime,
            Rain = Number(current, "rain") is { } currentRain ? currentRain + (Number(current, "showers") ?? 0) : null,
            Gusts = Number(current, "wind_gusts_10m"), WindDirection = Number(current, "wind_direction_10m"), Hours = hours,
            Humidity = Number(current, "relative_humidity_2m"),
            Sunrise = DayValue<DateTime>(daily, "sunrise", localTime, value => DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null),
            Sunset = DayValue<DateTime>(daily, "sunset", localTime, value => DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null),
            UvIndex = DayValue<double>(daily, "uv_index_max", localTime, value => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var uv) ? uv : null),
        };
        return report;
    }

    private static bool TimeAt(JsonElement parent, int i, out DateTime time) =>
        DateTime.TryParse(parent.GetProperty("time")[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    private static double? NumberAt(JsonElement parent, string property, int i) => parent.TryGetProperty(property, out var values)
        && values.ValueKind == JsonValueKind.Array && i < values.GetArrayLength() && values[i].ValueKind == JsonValueKind.Number
        && values[i].TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static double? Number(JsonElement parent, string property) => parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static double RequiredNumber(JsonElement parent, string property) => Number(parent, property) ?? throw new JsonException($"Missing {property}");
    /// <summary>One value from a daily series, for the day the place is in now.</summary>
    private static T? DayValue<T>(JsonElement daily, string property, DateTime local, Func<JsonElement, T?> read) where T : struct
    {
        if (!daily.TryGetProperty("time", out var times) || times.ValueKind != JsonValueKind.Array
            || !daily.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return null;
        for (var i = 0; i < times.GetArrayLength() && i < values.GetArrayLength(); i++)
            if (DateTime.TryParse(times[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day.Date == local.Date)
                return read(values[i]);
        return null;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public static class WeatherCodes
{
    public static (string Text, string Symbol) Describe(int code, bool isDay = true) => code switch
    {
        0 => (isDay ? "Clear" : "Clear night", isDay ? "☀" : "☾"),
        1 => ("Mostly clear", isDay ? "☀" : "☾"), 2 => ("Partly cloudy", "⛅"), 3 => ("Overcast", "☁"),
        45 or 48 => ("Fog", "☁"), 51 or 53 or 55 => ("Drizzle", "☂"), 56 or 57 => ("Freezing drizzle", "☂"),
        61 or 63 => ("Rain", "☂"), 65 => ("Heavy rain", "☂"), 66 or 67 => ("Freezing rain", "☂"),
        71 or 73 or 75 or 77 => ("Snow", "❄"), 80 or 81 or 82 => ("Showers", "☂"),
        85 or 86 => ("Snow showers", "❄"), 95 => ("Thunderstorm", "⚡"), 96 or 99 => ("Thunderstorm, hail", "⚡"),
        _ => ("Unknown", "☁"),
    };
}
