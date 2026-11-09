using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Atoll.Modules.Calendar;
using Atoll.Modules.Clock;
using Atoll.Modules.Games;
using Atoll.Modules.Media;
using Atoll.Modules.Weather;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Home;

public sealed record UpNextItem(string Title, string When, string Color);

/// <summary>
/// The Home tab: what's playing, the weather, the timer, this month and what's next, from the features that are on.
/// Cards for features that are off disappear; each card can also be hidden in Settings › Modules › Home.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly HomeSettings _settings;

    public HomeViewModel(MediaViewModel? media, WeatherViewModel? weather, ClockPanelViewModel? clock, CalendarViewModel? calendar,
        GameViewModel? game, HomeSettings settings)
    {
        _settings = settings;
        Media = settings.Media ? media : null;
        Weather = settings.Weather ? weather : null;
        Timer = settings.Timer ? clock?.Timer : null;
        Calendar = calendar;
        Game = settings.Game ? game : null;
        if (calendar is not null) calendar.Days.CollectionChanged += OnEventsChanged;
        Rebuild();
    }

    public MediaViewModel? Media { get; }
    public WeatherViewModel? Weather { get; }
    public TimerViewModel? Timer { get; }
    public CalendarViewModel? Calendar { get; }
    public GameViewModel? Game { get; }

    public bool ShowMonth => Calendar is not null && _settings.Calendar;
    public bool ShowUpNext => Calendar is not null && _settings.UpNext;
    public bool HasRightColumn => Weather is not null || Timer is not null || ShowUpNext || Game is not null;

    [ObservableProperty] private IReadOnlyList<UpNextItem> _upNext = [];
    public bool HasUpNext => UpNext.Count > 0;
    partial void OnUpNextChanged(IReadOnlyList<UpNextItem> value) => OnPropertyChanged(nameof(HasUpNext));

    public static IReadOnlyList<string> WeekdayInitials { get; } =
        Enumerable.Range(0, 7).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames[((int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + i) % 7][..1]).ToList();

    /// <summary>Home's month grid is a shortcut: pick a date, then open the full Calendar tab on it.</summary>
    [RelayCommand]
    private void OpenDay(CalendarMonthCell? cell)
    {
        if (cell is null || Calendar is null) return;
        Calendar.SelectDay(cell);
        Atoll.Navigation.OpenModule?.Invoke("calendar");
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var events = Calendar?.Days.SelectMany(d => d.Events).Select(e => e.Event).ToList() ?? [];
        var now = DateTimeOffset.Now;
        UpNext = events.Where(e => e.End > now && e.Start.LocalDateTime.Date == now.LocalDateTime.Date)
            .OrderBy(e => e.Start).Take(3)
            .Select(e => new UpNextItem(e.DisplayTitle, e.AllDay ? "All day" : $"{e.Start.LocalDateTime:t}" + (e.Location is { Length: > 0 } l ? $" · {l}" : ""), e.Color))
            .ToList();
    }
}
