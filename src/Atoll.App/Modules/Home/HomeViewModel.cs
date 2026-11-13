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

public sealed record UpNextItem(string Title, string When, string Color, string Time, CalendarEventViewModel Source)
{
    public bool CanJoin => Source.CanJoin;
    public System.Windows.Input.ICommand JoinCommand => Source.JoinCommand;
}

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
    /// <summary>The left column holds the large now-playing card; the month, when on, is a row under the cards.</summary>
    public bool ShowHeroMedia => Media is not null;
    public bool HasWeather => Weather is not null;
    public bool HasTimer => Timer is not null;
    public bool HasLeftColumn => ShowHeroMedia;
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

    public string TodayLabel => $"Today · {DateTime.Now:ddd d}";
    /// <summary>The first upcoming event with a meeting link, for the Join button in the Today card.</summary>
    public UpNextItem? JoinItem => UpNext.FirstOrDefault(item => item.CanJoin);
    public bool HasJoin => JoinItem is not null;

    private void Rebuild()
    {
        var events = Calendar?.Days.SelectMany(d => d.Events).ToList() ?? [];
        var now = DateTimeOffset.Now;
        UpNext = events.Where(e => e.Event.End > now && e.Event.Start.LocalDateTime.Date == now.LocalDateTime.Date)
            .OrderBy(e => e.Event.Start).Take(3)
            .Select(e => new UpNextItem(e.Event.DisplayTitle,
                e.Event.AllDay ? "All day" : $"{e.Event.Start.LocalDateTime:t}" + (e.Event.Location is { Length: > 0 } l ? $" · {l}" : ""),
                e.Event.Color, e.Event.AllDay ? "All day" : $"{e.Event.Start.LocalDateTime:t}", e))
            .ToList();
        OnPropertyChanged(nameof(TodayLabel));
        OnPropertyChanged(nameof(JoinItem));
        OnPropertyChanged(nameof(HasJoin));
    }
}
