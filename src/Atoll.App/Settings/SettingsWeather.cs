using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Atoll.Modules.Weather;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Devices.Geolocation;

namespace Atoll.Settings;

public sealed partial class SettingsViewModel : IDisposable
{
    private CancellationTokenSource? _locationCts;
    [ObservableProperty] private string _weatherQuery = "";
    [ObservableProperty] private string _weatherLocationMessage = "";
    [ObservableProperty] private bool _weatherLocationBusy;
    [ObservableProperty] private bool _weatherLocationExpanded = true;
    public bool CanChooseWeatherLocation => !WeatherLocationBusy;
    partial void OnWeatherLocationBusyChanged(bool value) => OnPropertyChanged(nameof(CanChooseWeatherLocation));
    public ObservableCollection<Place> WeatherResults { get; } = [];
    public WeatherViewModel Weather { get; }
    public string WeatherLocation => S.Weather.City.Length == 0 ? "No location selected" : S.Weather.City;

    partial void OnPageIndexChanged(int value)
    {
        if (value == 6) Weather.Start();
        if (value == 3) RefreshNotificationAccess();
    }

    [RelayCommand]
    private async Task SearchWeatherAsync()
    {
        var query = WeatherQuery.Trim();
        if (query.Length < 2) { WeatherLocationMessage = "Enter at least two letters of a city name."; return; }
        _locationCts?.Cancel();
        using var cts = _locationCts = new CancellationTokenSource();
        WeatherLocationBusy = true; WeatherResults.Clear(); WeatherLocationMessage = "Searching cities…";
        try
        {
            var results = await OpenMeteo.SearchAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var place in results) WeatherResults.Add(place);
            WeatherLocationMessage = results.Count == 0 ? "No cities found. Try a nearby town or a different spelling." : "Choose the matching city below.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        { if (!cts.IsCancellationRequested) WeatherLocationMessage = "City search is unavailable. Check your connection and try again."; }
        finally { if (ReferenceEquals(_locationCts, cts)) { _locationCts = null; WeatherLocationBusy = false; } }
    }

    [RelayCommand]
    private void UseWeatherPlace(Place place)
    {
        if (!OpenMeteo.ValidCoordinates(place.Latitude, place.Longitude)) return;
        S.Weather.City = place.Name; S.Weather.Latitude = place.Latitude; S.Weather.Longitude = place.Longitude;
        WeatherResults.Clear(); WeatherLocationMessage = "Location saved. Forecast times use this location's time zone.";
        WeatherLocationExpanded = false;
        OnPropertyChanged(nameof(WeatherLocation)); OnPropertyChanged(nameof(WeatherCity));
        Save();
        Weather.Start();
    }

    [RelayCommand]
    private async Task UseWeatherLocationAsync()
    {
        _locationCts?.Cancel();
        using var cts = _locationCts = new CancellationTokenSource();
        WeatherLocationBusy = true; WeatherResults.Clear(); WeatherLocationMessage = "Requesting Windows location…";
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (cts.IsCancellationRequested) return;
            if (access != GeolocationAccessStatus.Allowed)
            {
                WeatherLocationMessage = "Windows location is off or access was declined. Enable location in Windows Settings, or search for your city.";
                return;
            }
            var locator = new Geolocator { DesiredAccuracyInMeters = 10000 };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20)).AsTask(cts.Token);
            if (cts.IsCancellationRequested) return;
            // A weather forecast needs an area, not an exact address.
            var latitude = Math.Round(position.Coordinate.Point.Position.Latitude, 2);
            var longitude = Math.Round(position.Coordinate.Point.Position.Longitude, 2);
            UseWeatherPlace(new Place(FormattableString.Invariant($"Current area ({latitude:0.00}, {longitude:0.00})"), latitude, longitude));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException)
        { if (!cts.IsCancellationRequested) WeatherLocationMessage = "Windows could not locate this PC. Try again, check Location settings, or search for a city."; }
        finally { if (ReferenceEquals(_locationCts, cts)) { _locationCts = null; WeatherLocationBusy = false; } }
    }

    [RelayCommand]
    private void OpenLocationSettings() => Process.Start(new ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true });

    public void Dispose()
    {
        _locationCts?.Cancel();
        Weather.Dispose();
    }
}
