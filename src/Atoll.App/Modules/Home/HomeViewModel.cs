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

public sealed record MonthCell(int Day, bool InMonth, bool IsToday, bool HasEvents)
{
    public string Text => Day.ToString(CultureInfo.CurrentCulture);
}

public sealed record UpNextItem(string Title, string When, string Color);

/// <summary>
/// The Home tab: what's playing, the weather, the timer, this month and what's next, from the features that are on.
/// Cards for features that are off disappear; each card can also be hidden in Settings › Modules › Home.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly HomeSettings _settings;
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);

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

    [ObservableProperty] private string _monthTitle = "";
    [ObservableProperty] private IReadOnlyList<MonthCell> _cells = [];
    [ObservableProperty] private IReadOnlyList<UpNextItem> _upNext = [];
    public bool HasUpNext => UpNext.Count > 0;
    partial void OnUpNextChanged(IReadOnlyList<UpNextItem> value) => OnPropertyChanged(nameof(HasUpNext));

    public static IReadOnlyList<string> WeekdayInitials { get; } =
        Enumerable.Range(0, 7).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames[((int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + i) % 7][..1]).ToList();

    [RelayCommand] private void PreviousMonth() { _month = _month.AddMonths(-1); Rebuild(); }
    [RelayCommand] private void NextMonth() { _month = _month.AddMonths(1); Rebuild(); }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var events = Calendar?.Days.SelectMany(d => d.Events).Select(e => e.Event).ToList() ?? [];
        MonthTitle = _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        Cells = BuildMonth(_month, DateTime.Today, events.Select(e => e.Start.LocalDateTime.Date).ToHashSet());
        var now = DateTimeOffset.Now;
        UpNext = events.Where(e => e.End > now && e.Start.LocalDateTime.Date == now.LocalDateTime.Date)
            .OrderBy(e => e.Start).Take(3)
            .Select(e => new UpNextItem(e.DisplayTitle, e.AllDay ? "All day" : $"{e.Start.LocalDateTime:t}" + (e.Location is { Length: > 0 } l ? $" · {l}" : ""), e.Color))
            .ToList();
    }

    /// <summary>Six weeks starting on the culture's first weekday, so the grid never changes height.</summary>
    internal static IReadOnlyList<MonthCell> BuildMonth(DateTime month, DateTime today, IReadOnlySet<DateTime> eventDays)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        var offset = ((int)first.DayOfWeek - (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var start = first.AddDays(-offset);
        return Enumerable.Range(0, 42).Select(i => start.AddDays(i))
            .Select(d => new MonthCell(d.Day, d.Month == month.Month, d == today.Date, eventDays.Contains(d))).ToList();
    }
}
