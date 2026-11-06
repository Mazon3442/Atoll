using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Weather;

public sealed record WeatherDay(string Name, string Symbol, string High, string Low);

public sealed record WeatherReport(
    double Temperature, double FeelsLike, int Code, bool IsDay, double Wind,
    IReadOnlyList<(DateTime Date, int Code, double High, double Low)> Days);

public sealed record Place(string Name, double Latitude, double Longitude);

/// <summary>WMO weather codes (as used by Open-Meteo) to text and a Segoe UI Symbol character.</summary>
public static class WeatherCodes
{
    public static (string Text, string Symbol) Describe(int code, bool isDay = true) => code switch
    {
        0 => (isDay ? "Clear" : "Clear night", isDay ? "☀" : "☾"),
        1 => ("Mostly clear", isDay ? "☀" : "☾"),
        2 => ("Partly cloudy", "⛅"),
        3 => ("Overcast", "☁"),
        45 or 48 => ("Fog", "☁"),
        51 or 53 or 55 => ("Drizzle", "☂"),
        56 or 57 => ("Freezing drizzle", "☂"),
        61 or 63 => ("Rain", "☂"),
        65 => ("Heavy rain", "☂"),
        66 or 67 => ("Freezing rain", "☂"),
        71 or 73 or 75 or 77 => ("Snow", "❄"),
        80 or 81 or 82 => ("Showers", "☂"),
        85 or 86 => ("Snow showers", "❄"),
        95 => ("Thunderstorm", "⚡"),
        96 or 99 => ("Thunderstorm, hail", "⚡"),
        _ => ("Unknown", "☁"),
    };
}

/// <summary>Open-Meteo (free, no key). Only the place name and its coordinates leave the PC.</summary>
public static class OpenMeteo
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ATOLL/1.0");
        return http;
    }

    public static async Task<Place?> FindAsync(string city, CancellationToken ct)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name={Uri.EscapeDataString(city.Split(',')[0].Trim())}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        return ParsePlace(doc.RootElement);
    }

    public static async Task<WeatherReport> ForecastAsync(Place place, bool fahrenheit, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var url = "https://api.open-meteo.com/v1/forecast"
                  + $"?latitude={place.Latitude.ToString(inv)}&longitude={place.Longitude.ToString(inv)}"
                  + "&current=temperature_2m,apparent_temperature,weather_code,is_day,wind_speed_10m"
                  + "&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=5"
                  + (fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "&wind_speed_unit=kmh");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        return ParseForecast(doc.RootElement);
    }

    internal static Place? ParsePlace(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        var parts = new[] { Str(r, "name"), Str(r, "admin1"), Str(r, "country") }
            .Where(s => !string.IsNullOrEmpty(s)).Distinct();
        return new Place(string.Join(", ", parts), r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
    }

    internal static WeatherReport ParseForecast(JsonElement root)
    {
        var c = root.GetProperty("current");
        var d = root.GetProperty("daily");
        var dates = d.GetProperty("time").EnumerateArray().Select(t => DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture)).ToList();
        var codes = d.GetProperty("weather_code").EnumerateArray().Select(v => v.GetInt32()).ToList();
        var highs = d.GetProperty("temperature_2m_max").EnumerateArray().Select(v => v.GetDouble()).ToList();
        var lows = d.GetProperty("temperature_2m_min").EnumerateArray().Select(v => v.GetDouble()).ToList();
        var days = Enumerable.Range(0, new[] { dates.Count, codes.Count, highs.Count, lows.Count }.Min())
            .Select(i => (dates[i], codes[i], highs[i], lows[i])).ToList();
        return new WeatherReport(
            c.GetProperty("temperature_2m").GetDouble(),
            c.GetProperty("apparent_temperature").GetDouble(),
            c.GetProperty("weather_code").GetInt32(),
            c.GetProperty("is_day").GetInt32() == 1,
            c.GetProperty("wind_speed_10m").GetDouble(),
            days);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;
}

public sealed partial class WeatherViewModel(Action refresh) : ObservableObject
{
    [ObservableProperty] private bool _hasCity;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _place = "";
    [ObservableProperty] private string _temperature = "";
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _updated = "";

    public ObservableCollection<WeatherDay> Days { get; } = [];

    [RelayCommand]
    private void Refresh() => refresh();
}

/// <summary>Current weather and a 5-day forecast for the city set in Settings › Modules.</summary>
public sealed class WeatherModule : IIslandModule
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(20);

    private readonly WeatherViewModel _vm;
    private ModuleContext? _context;
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _cts;
    private (string City, TemperatureUnits Units) _shown = ("\0", TemperatureUnits.Auto);
    private Place? _place;

    public WeatherModule() => _vm = new WeatherViewModel(() => _ = RefreshAsync());

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        context.Settings.Changed += OnSettingsChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background, context.Dispatcher) { Interval = RefreshEvery };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private void OnSettingsChanged()
    {
        var s = _context!.Settings.Current.Weather;
        if ((s.City.Trim(), s.Units) != _shown) _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_context is null) return;
        var settings = _context.Settings.Current.Weather;
        var city = settings.City.Trim();
        if (city != _shown.City) _place = null;
        _shown = (city, settings.Units);

        _vm.HasCity = city.Length > 0;
        if (!_vm.HasCity)
        {
            _vm.HasReport = false;
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _vm.Status = "Loading…";
        try
        {
            _place ??= await OpenMeteo.FindAsync(city, cts.Token);
            if (_place is null)
            {
                _vm.HasReport = false;
                _vm.Status = $"Couldn't find \"{city}\". Check the city in Settings › Modules.";
                return;
            }
            var fahrenheit = settings.Units == TemperatureUnits.Fahrenheit
                             || (settings.Units == TemperatureUnits.Auto && !RegionInfo.CurrentRegion.IsMetric);
            var report = await OpenMeteo.ForecastAsync(_place, fahrenheit, cts.Token);
            if (cts.IsCancellationRequested) return;
            Show(report, _place, fahrenheit);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _vm.Status = _vm.HasReport ? "Couldn't refresh; showing the last forecast." : "Couldn't load the weather. Check your connection.";
            Log.Info($"Weather refresh failed: {ex.Message}");
        }
    }

    private void Show(WeatherReport r, Place place, bool fahrenheit)
    {
        var (text, symbol) = WeatherCodes.Describe(r.Code, r.IsDay);
        _vm.Place = place.Name;
        _vm.Temperature = $"{Math.Round(r.Temperature)}°";
        _vm.Symbol = symbol;
        _vm.Description = text;
        _vm.Details = $"Feels like {Math.Round(r.FeelsLike)}° · Wind {Math.Round(r.Wind)} {(fahrenheit ? "mph" : "km/h")}";
        _vm.Updated = $"Updated {DateTime.Now:t}";
        _vm.Days.Clear();
        foreach (var (date, code, high, low) in r.Days)
        {
            var name = date.Date == DateTime.Today ? "Today" : date.ToString("ddd", CultureInfo.CurrentCulture);
            _vm.Days.Add(new WeatherDay(name, WeatherCodes.Describe(code).Symbol, $"{Math.Round(high)}°", $"{Math.Round(low)}°"));
        }
        _vm.Status = "";
        _vm.HasReport = true;
    }

    public void Dispose()
    {
        _timer?.Stop();
        _cts?.Cancel();
        if (_context is not null) _context.Settings.Changed -= OnSettingsChanged;
    }
}
