using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Weather;

public sealed record WeatherDay(ForecastDay Forecast, string Name, string Symbol, string High, string Low);
public sealed record WeatherHourRow(string Time, string Symbol, string Condition, string Temperature, string Chance, string Precipitation, string Wind);

public sealed partial class WeatherViewModel(SettingsService settings) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private (string, double?, double?, TemperatureUnits)? _shown;
    private WeatherReport? _report;
    private bool _fahrenheit;
    private bool _started;
    private bool _disposed;

    [ObservableProperty] private bool _hasCity;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "Choose a location to see its forecast.";
    [ObservableProperty] private string _place = "";
    [ObservableProperty] private string _temperature = "";
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _wind = "";
    [ObservableProperty] private string _rainSummary = "";
    [ObservableProperty] private string _updated = "";
    [ObservableProperty] private string _daySummary = "";
    [ObservableProperty] private WeatherDay? _selectedDay;
    public ObservableCollection<WeatherDay> Days { get; } = [];
    public ObservableCollection<WeatherHourRow> Hours { get; } = [];
    public bool HasHours => Hours.Count > 0;

    [RelayCommand]
    private void OpenSettings()
    {
        if (System.Windows.Application.Current is App app) app.OpenSettings(6);
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        settings.Changed += OnSettingsChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        _timer.Tick += OnTick;
        _timer.Start();
        _ = RefreshAsync();
    }

    private void OnTick(object? sender, EventArgs e) => _ = RefreshAsync();
    private void OnSettingsChanged()
    {
        var s = settings.Current.Weather;
        if (_shown != (s.City.Trim(), s.Latitude, s.Longitude, s.Units)) _ = RefreshAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAsync()
    {
        if (_disposed) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var s = settings.Current.Weather;
        var snapshot = (s.City.Trim(), s.Latitude, s.Longitude, s.Units);
        var locationChanged = _shown is not { } old || old.Item1 != snapshot.Item1 || old.Item2 != snapshot.Latitude || old.Item3 != snapshot.Longitude || old.Item4 != snapshot.Units;
        _shown = snapshot;
        HasCity = snapshot.Item1.Length > 0;
        if (locationChanged)
        {
            HasReport = false; Place = snapshot.Item1; SelectedDay = null;
            Days.Clear(); Hours.Clear(); _report = null;
        }
        if (!HasCity) { Status = "Choose a location in Settings › Weather."; IsLoading = false; _cts = null; cts.Dispose(); return; }
        Status = "Updating forecast…";
        IsLoading = true;
        try
        {
            var place = snapshot.Latitude is { } lat && snapshot.Longitude is { } lon && OpenMeteo.ValidCoordinates(lat, lon)
                ? new Place(snapshot.Item1, lat, lon) : await OpenMeteo.FindAsync(snapshot.Item1, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (place is null) { Status = "Location not found. Search for a city in Settings › Weather."; HasReport = false; return; }
            var fahrenheit = snapshot.Units == TemperatureUnits.Fahrenheit || snapshot.Units == TemperatureUnits.Auto && !RegionInfo.CurrentRegion.IsMetric;
            var report = await OpenMeteo.ForecastAsync(place, fahrenheit, cts.Token);
            if (!cts.IsCancellationRequested) Show(report, place, fahrenheit);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            if (cts.IsCancellationRequested) return;
            Status = HasReport ? "Could not refresh. Showing the previous forecast; try Refresh." : "Could not load the forecast. Check your connection and try Refresh.";
            Log.Info($"Weather refresh failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) { IsLoading = false; _cts = null; }
            cts.Dispose();
        }
    }

    internal void Show(WeatherReport report, Place place, bool fahrenheit)
    {
        var chosenDate = SelectedDay?.Forecast.Date.Date;
        HasCity = true;
        _report = report;
        _fahrenheit = fahrenheit;
        var (text, symbol) = WeatherCodes.Describe(report.Code, report.IsDay);
        Place = place.Name;
        Temperature = $"{Math.Round(report.Temperature)}°{(fahrenheit ? "F" : "C")}";
        Symbol = symbol; Description = text;
        Details = $"Feels like {Math.Round(report.FeelsLike)}°";
        Wind = $"{Math.Round(report.Wind)} {WindUnit}" + (report.WindDirection is { } direction ? $" {Compass(direction)}" : "")
            + (report.Gusts is { } gusts ? $" · gusts {Math.Round(gusts)} {WindUnit}" : "");
        RainSummary = DescribeRain(report);
        Updated = $"Updated {DateTime.Now:t} · Open-Meteo";
        Days.Clear();
        foreach (var day in report.Days)
            Days.Add(new WeatherDay(day, day.Date.Date == report.LocalTime.Date ? "Today" : day.Date.ToString("ddd", CultureInfo.CurrentCulture),
                WeatherCodes.Describe(day.Code).Symbol, $"{Math.Round(day.High)}°", $"{Math.Round(day.Low)}°"));
        SelectedDay = Days.FirstOrDefault(d => d.Forecast.Date.Date == chosenDate) ?? Days.FirstOrDefault();
        UpdateHours();
        Status = ""; HasReport = true;
    }

    partial void OnSelectedDayChanged(WeatherDay? value) => UpdateHours();
    private void UpdateHours()
    {
        Hours.Clear();
        if (SelectedDay is not { } day || _report is null) return;
        var forecast = day.Forecast;
        var metrics = new List<string>();
        if (forecast.RainChance is { } chance) metrics.Add($"{chance:0}% precip. chance");
        if (forecast.Precipitation is { } amount) metrics.Add($"{amount:0.###} {PrecipUnit} total");
        if (forecast.Wind is { } wind) metrics.Add($"Peak wind {wind:0} {WindUnit}");
        DaySummary = $"{forecast.Date:dddd, MMM d} · {WeatherCodes.Describe(forecast.Code).Text}"
            + (metrics.Count > 0 ? "\n" + string.Join(" · ", metrics) : "");
        foreach (var hour in _report.Hours.Where(h => h.Time.Date == forecast.Date.Date && h.Time.AddHours(1) > _report.LocalTime).OrderBy(h => h.Time))
        {
            var (condition, symbol) = WeatherCodes.Describe(hour.Code, hour.IsDay);
            Hours.Add(new WeatherHourRow(hour.Time.ToString("t", CultureInfo.CurrentCulture), symbol, condition,
                $"{hour.Temperature:0}°", hour.RainChance is { } p ? $"{p:0}%" : "—",
                hour.Precipitation is { } a ? $"{a:0.###} {PrecipUnit}" : "Unavailable",
                hour.Wind is { } w ? $"{w:0} {WindUnit}" : "—"));
        }
        OnPropertyChanged(nameof(HasHours));
    }

    private string WindUnit => _fahrenheit ? "mph" : "km/h";
    private string PrecipUnit => _fahrenheit ? "in" : "mm";
    internal static string Compass(double direction) => new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)Math.Floor((direction % 360 + 360 + 22.5) % 360 / 45)];
    internal static string DescribeRain(WeatherReport report)
    {
        var upcoming = report.Hours.Where(h => h.Time.AddHours(1) > report.LocalTime && h.Time <= report.LocalTime.AddHours(24)).OrderBy(h => h.Time).ToList();
        if (report.Rain > 0)
        {
            if (upcoming.Count == 0 || upcoming.Any(h => h.Rain is null)) return "Rain now. Hourly rain timing is unavailable.";
            var dry = upcoming.FirstOrDefault(h => h.Time > report.LocalTime && h.Rain == 0);
            return dry is null ? "Rain now. No dry hour in the next 24-hour forecast." : $"Rain now. May ease around {dry.Time:t}.";
        }
        var rainy = upcoming.FirstOrDefault(h => h.Rain > 0);
        if (rainy is not null)
        {
            var when = rainy.Time <= report.LocalTime ? "this hour" : rainy.Time.Date > report.LocalTime.Date ? $"tomorrow at {rainy.Time:t}" : $"around {rainy.Time:t}";
            return $"Rain possible {when}" + (rainy.RainChance is { } chance ? $" ({chance:0}% precip. chance)." : ".");
        }
        return upcoming.Count == 0 || upcoming.Any(h => h.Rain is null) ? "Rain timing unavailable."
            : upcoming.Count >= 24 ? "No rain in the next 24-hour forecast." : "No rain in the available hourly forecast.";
    }

    public void Dispose()
    {
        _disposed = true;
        settings.Changed -= OnSettingsChanged;
        _timer?.Stop();
        _cts?.Cancel();
    }
}
