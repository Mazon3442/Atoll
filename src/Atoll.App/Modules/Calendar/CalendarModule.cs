using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
#if !AVALONIA
using System.Media;
#endif
using System.Net.Http;
#if AVALONIA
using Avalonia.Threading;
using Atoll.Desktop;
#else
using System.Windows.Threading;
#endif
using Atoll.Activities;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Calendar;

/// <summary>Events for one day in the expanded panel.</summary>
public sealed class CalendarDay
{
    public required string Label { get; init; }
    public List<CalendarEventViewModel> Events { get; init; } = [];
}

/// <summary>One day in the month grid: the event count and the calendars that have something on it.</summary>
public sealed record CalendarDayMark(int EventCount, IReadOnlyList<string> Colors);

/// <summary>One cell in the six-week month grid.</summary>
public sealed record CalendarMonthCell(
    DateTime Date,
    bool InMonth,
    bool IsToday,
    bool IsSelected,
    IReadOnlyList<string> DotColors,
    int EventCount)
{
    public string Text => Date.Day.ToString(CultureInfo.CurrentCulture);
    public bool HasEvents => EventCount > 0;
    public string AccessibleLabel => EventCount switch
    {
        0 => Date.ToString("D", CultureInfo.CurrentCulture),
        1 => $"{Date.ToString("D", CultureInfo.CurrentCulture)}, 1 event",
        _ => $"{Date.ToString("D", CultureInfo.CurrentCulture)}, {EventCount} events",
    };
}

public sealed partial class CalendarEventViewModel(CalendarEventItem item, Action<CalendarEventItem> pin, Action<CalendarEventItem> dismiss) : ObservableObject
{
    public CalendarEventItem Event { get; } = item;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    private bool _isPinned;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DismissCommand))]
    private bool _isDismissed;
    /// <summary>"Now", "in 20 min" for the next event, "Hidden" once dismissed from the island, or empty.</summary>
    [ObservableProperty] private string _badge = "";
    /// <summary>True when the badge is about time (now or next) and gets the accent color.</summary>
    [ObservableProperty] private bool _isHighlighted;
    /// <summary>Rows after the first day are one line: title and start time.</summary>
    public bool IsCompact { get; init; }
    public string PinLabel => IsPinned ? "Unpin from island" : "Pin to island";
    public bool CanJoin => Event.HasJoin;
    private bool CanDismiss => !IsDismissed;
    [RelayCommand] private void TogglePin() => pin(Event);
    [RelayCommand(CanExecute = nameof(CanDismiss))] private void Dismiss() => dismiss(Event);

    [RelayCommand]
    private void Join()
    {
        if (Event.Url is not { Length: > 0 } url) return;
        try { using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not open the meeting link", ex);
        }
    }
}

public sealed partial class CalendarViewModel(Action refresh) : ObservableObject
{
    public static IReadOnlyList<string> WeekdayInitials { get; } =
        Enumerable.Range(0, 7)
            .Select(i => CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames[
                ((int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + i) % 7][..1])
            .ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    private bool _hasSources;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    private bool _hasEvents;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshLabel))]
    private string _updated = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    [NotifyPropertyChangedFor(nameof(RefreshLabel))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isRefreshing;

    [ObservableProperty] private DateTime _selectedDate = DateTime.Today;
    [ObservableProperty] private DateTime _visibleMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private string _monthTitle = DateTime.Today.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    [ObservableProperty] private IReadOnlyList<CalendarMonthCell> _monthCells = [];
    [ObservableProperty] private CalendarMonthCell? _selectedCell;
    [ObservableProperty] private IReadOnlyList<CalendarEventViewModel> _selectedDayRows = [];
    [ObservableProperty] private string _selectedDayTitle = "Today";
    [ObservableProperty] private string _selectedDayDate = DateTime.Today.ToString("MMM d", CultureInfo.CurrentCulture);
    [ObservableProperty] private string _selectedDayCount = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelectedDayEmpty))]
    private bool _hasSelectedDayEvents;
    public bool ShowSelectedDayEmpty => !HasSelectedDayEvents;
    public bool IsSelectedToday => SelectedDate.Date == DateTime.Today;
    public string SelectedDayEmptyMessage => "Nothing scheduled.";

    partial void OnSelectedDateChanged(DateTime value)
    {
        var date = value.Date;
        var month = new DateTime(date.Year, date.Month, 1);
        if (VisibleMonth != month) VisibleMonth = month;
        SelectedDayTitle = date == DateTime.Today ? "Today"
            : date == DateTime.Today.AddDays(1) ? "Tomorrow"
            : date.ToString("dddd", CultureInfo.CurrentCulture);
        SelectedDayDate = date.ToString("MMM d", CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(IsSelectedToday));
    }

    partial void OnVisibleMonthChanged(DateTime value)
    {
        var normalized = new DateTime(value.Year, value.Month, 1);
        if (value != normalized) VisibleMonth = normalized;
        else MonthTitle = normalized.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    }

    partial void OnSelectedCellChanged(CalendarMonthCell? value)
    {
        // The list clears its selection while its items are replaced; ignore that, not the user's click.
        if (value is null || SelectedDate.Date == value.Date) return;
        SelectedDate = value.Date;
    }

    /// <summary>The refresh button's tooltip, which also says when the calendars last updated.</summary>
    public string RefreshLabel => IsRefreshing ? "Refreshing calendars…"
        : string.IsNullOrEmpty(Updated) ? "Refresh calendars" : $"Refresh calendars ({Updated})";

    public ObservableCollection<CalendarDay> Days { get; } = [];

    /// <summary>True when the empty-state placeholder should show instead of the event list.</summary>
    public bool ShowEmpty => !HasEvents;

    public string EmptyMessage => !HasSources
        ? "No calendars added yet"
        : IsRefreshing ? "Loading calendar events…"
        : string.IsNullOrEmpty(Status) ? "No events in the next 14 days" : Status;

    [RelayCommand]
    private void Today()
    {
        var today = DateTime.Today;
        VisibleMonth = new DateTime(today.Year, today.Month, 1);
        SelectedDate = today;
    }

    [RelayCommand] private void PreviousMonth() => MoveMonth(-1);
    [RelayCommand] private void NextMonth() => MoveMonth(1);

    private void MoveMonth(int delta)
    {
        var month = VisibleMonth.AddMonths(delta);
        var day = Math.Min(SelectedDate.Day, DateTime.DaysInMonth(month.Year, month.Month));
        SelectedDate = new DateTime(month.Year, month.Month, day);
        VisibleMonth = new DateTime(month.Year, month.Month, 1);
    }

    [RelayCommand]
    internal void SelectDay(CalendarMonthCell? cell)
    {
        if (cell is null) return;
        SelectedDate = cell.Date.Date;
    }

    /// <summary>Six weeks starting on the culture's first weekday, with event dots for each day.</summary>
    public static IReadOnlyList<CalendarMonthCell> BuildMonth(
        DateTime month, DateTime today, DateTime selected, IReadOnlyDictionary<DateTime, CalendarDayMark> marks)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        var offset = ((int)first.DayOfWeek - (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var start = first.AddDays(-offset);
        return Enumerable.Range(0, 42).Select(i =>
        {
            var date = start.AddDays(i).Date;
            var mark = marks.TryGetValue(date, out var found) ? found : new CalendarDayMark(0, []);
            return new CalendarMonthCell(date, date.Month == month.Month, date == today.Date, date == selected.Date,
                mark.Colors, mark.EventCount);
        }).ToList();
    }

    private bool CanRefresh => !IsRefreshing;
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh() => refresh();

    [RelayCommand]
    private void OpenSettings()
    {
        Atoll.Navigation.OpenSettings?.Invoke(5);
    }
}

/// <summary>The pill: the next event and its countdown.</summary>
public sealed partial class NextEventViewModel(Action dismiss, Action pin) : ObservableObject
{
    [ObservableProperty] private bool _hasEvent;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _when = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _color = "#0A84FF";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    private bool _isPinned;
    public string PinLabel => IsPinned ? "Unpin from island" : "Pin to island";
    [RelayCommand] private void Dismiss() => dismiss();
    [RelayCommand] private void TogglePin() => pin();
}

/// <summary>Short pre-event popup. Shows the calendar color as a dot.</summary>
public sealed record CalendarAlertViewModel(string Title, string Detail, string Color);

/// <summary>
/// Upcoming events from Google Calendar and Microsoft 365 via their private ICS links. Refreshes
/// every 15 minutes, shows the next event as a countdown pill, and pops up shortly before events.
/// </summary>
public sealed class CalendarModule : IIslandModule
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PillHorizon = TimeSpan.FromHours(2); // unpinned: only what's coming up soon, never tomorrow night
    private const int UpcomingDays = 14;
    private static readonly HttpClient Http = CreateClient();

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly CalendarViewModel _vm;
    private readonly NextEventViewModel _next;
    private ModuleContext? _context;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _ticker;
    private CancellationTokenSource? _cts;
    private List<CalendarEventItem> _events = [];
    private string _sourceSignature = "";
    private bool _nextPosted;
    private CalendarEventItem? _nextEvent;
    private readonly HashSet<string> _alerted = [];
    private bool _disposed;
    private DateTime _shownDate;
    private DateTimeOffset _fetchStart = DateTimeOffset.MinValue;
    private DateTimeOffset _fetchEnd = DateTimeOffset.MaxValue;

    public CalendarModule() : this(Http, TimeProvider.System) { }

    internal CalendarModule(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
        _vm = new CalendarViewModel(() => _ = RefreshAsync());
        _next = new NextEventViewModel(() => { if (_nextEvent is { } item) Dismiss(item); },
            () => { if (_nextEvent is { } item) TogglePin(item); });
    }

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        context.Settings.Changed += OnSettingsChanged;
        _vm.PropertyChanged += OnCalendarPropertyChanged;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshEvery };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _refreshTimer.Start();
        _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickEvery };
        _ticker.Tick += (_, _) => OnTick();
        _ticker.Start();
        BuildPresentation(_time.GetLocalNow()); // show the month before the first download finishes
        _ = RefreshAsync();
    }

    private void OnCalendarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context is null || _disposed) return;
        if (e.PropertyName is not (nameof(CalendarViewModel.SelectedDate) or nameof(CalendarViewModel.VisibleMonth))) return;
        var now = _time.GetLocalNow();
        if (e.PropertyName == nameof(CalendarViewModel.VisibleMonth) && NeedsFetch(now))
        {
            BuildPresentation(now); // show the requested month immediately; the download fills in its dots
            _ = RefreshAsync();
        }
        else BuildPresentation(now);
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ATOLL/1.0");
        return http;
    }

    /// <summary>The visible month plus its neighbours, and always enough of today for Home and the pill.</summary>
    private (DateTimeOffset Start, DateTimeOffset End) FetchWindow(DateTimeOffset now)
    {
        var month = new DateTime(_vm.VisibleMonth.Year, _vm.VisibleMonth.Month, 1);
        var nearFirst = month.AddMonths(-1);
        var nearLast = month.AddMonths(2);
        var firstDate = nearFirst < now.Date ? nearFirst : now.Date;
        var lastDate = nearLast > now.Date.AddDays(UpcomingDays) ? nearLast : now.Date.AddDays(UpcomingDays);
        // Browsing far from today should not pull years of events; the visible month is enough there.
        if ((lastDate - firstDate).TotalDays > 200) { firstDate = nearFirst; lastDate = nearLast; }
        return (new DateTimeOffset(firstDate, TimeZoneInfo.Local.GetUtcOffset(firstDate)),
            new DateTimeOffset(lastDate, TimeZoneInfo.Local.GetUtcOffset(lastDate)));
    }

    private bool NeedsFetch(DateTimeOffset now)
    {
        var (start, end) = FetchWindow(now);
        return start < _fetchStart || end > _fetchEnd;
    }

    private void OnSettingsChanged()
    {
        if (_context is not null && SourceSignature(_context.Settings.Current.Calendar) != _sourceSignature)
            _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (_context is null || _disposed) return;
        _context.Dispatcher.VerifyAccess();
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var settings = _context.Settings.Current.Calendar;
        var signature = SourceSignature(settings);
        var sourcesChanged = signature != _sourceSignature;
        _sourceSignature = signature;
        if (sourcesChanged) _alerted.Clear();

        var now = _time.GetLocalNow();
        var (windowStart, windowEnd) = FetchWindow(now);
        _fetchStart = windowStart;
        _fetchEnd = windowEnd;
        var sources = settings.Sources
            .Select(s => (s.Name, s.Color, Url: SecretStore.Unprotect(s.EncryptedUrl)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .ToList();

        try
        {
            _vm.HasSources = settings.Sources.Any(s => !string.IsNullOrWhiteSpace(s.EncryptedUrl));
            if (sources.Count == 0)
            {
                _events = [];
                _vm.Status = _vm.HasSources ? "Couldn't read the saved calendar link. Save it again in Settings › Calendar." : "";
                _vm.Updated = "";
                BuildPresentation(now);
                UpdateNext();
                return;
            }
            _vm.IsRefreshing = true;
            var tasks = sources.Select(async x =>
            {
                try
                {
                    var ics = await _http.GetStringAsync(x.Url!, cts.Token).ConfigureAwait(false);
                    var valid = IcsCalendar.TryParse(ics, x.Name, x.Color, windowStart, windowEnd, out var parsed);
                    return (Events: parsed, Error: valid ? "" : "A link returned something other than a valid ICS calendar. Check its private ICS address in Settings › Calendar.");
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException or InvalidOperationException or NotSupportedException)
                {
                    if (cts.IsCancellationRequested) throw;
                    // Download exception messages can contain the private calendar URL.
                    Log.Info($"Calendar download failed ({ex.GetType().Name}).");
                    return (Events: (IReadOnlyList<CalendarEventItem>)[], Error: "Couldn't download a calendar. Check the ICS link in Settings › Calendar and your connection.");
                }
            });
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            await _context.Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || _disposed) return;
                _events = results.SelectMany(r => r.Events).OrderBy(e => e.Start).ThenBy(e => e.Title).ToList();
                _vm.Updated = $"Updated {_time.GetLocalNow():t}";
                _vm.Status = string.Join(" ", results.Select(r => r.Error).Where(e => e.Length > 0).Distinct());
                if (sources.Count < settings.Sources.Count) _vm.Status += " A saved link couldn't be read. Save it again in Settings › Calendar.";
                PruneDismissals(now);
                BuildPresentation(now);
                UpdateNext();
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            await _context.Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_cts, cts)) { _vm.IsRefreshing = false; _cts = null; }
            });
            cts.Dispose();
        }
    }

    private void BuildPresentation(DateTimeOffset now)
    {
        _shownDate = now.Date;
        BuildDays(now);
        BuildMonth(now);
        BuildAgenda(now);
        UpdateRowStates();
    }

    /// <summary>The fourteen-day list used by the pill's picker, Home and the Avalonia panel.</summary>
    private void BuildDays(DateTimeOffset now)
    {
        _vm.Days.Clear();
        var horizon = now.AddDays(UpcomingDays);
        foreach (var group in _events.Where(e => e.End > now && e.Start < horizon)
                     .GroupBy(e => e.Start.Date < now.Date ? now.Date : e.Start.Date))
        {
            // The nearest day gets full rows; later days are one line each so more of the fortnight fits.
            var compact = _vm.Days.Count > 0;
            _vm.Days.Add(new CalendarDay
            {
                Label = DayLabel(group.Key, now.Date),
                Events = group.Select(e => new CalendarEventViewModel(e, TogglePin, Dismiss) { IsCompact = compact }).ToList(),
            });
        }
        _vm.HasEvents = _vm.Days.Count > 0;
    }

    private void BuildMonth(DateTimeOffset now)
    {
        var marks = new Dictionary<DateTime, CalendarDayMark>();
        foreach (var item in _events)
        {
            for (var day = item.Start.Date; day <= item.End.Date; day = day.AddDays(1))
            {
                // An event ending exactly at midnight belongs to the day before, not the next day.
                if (day > item.Start.Date && day == item.End.Date && item.End.TimeOfDay == TimeSpan.Zero) break;
                marks.TryGetValue(day, out var mark);
                var colors = mark?.Colors.ToList() ?? [];
                if (!colors.Contains(item.Color, StringComparer.OrdinalIgnoreCase)) colors.Add(item.Color);
                marks[day] = new CalendarDayMark((mark?.EventCount ?? 0) + 1, colors.Take(3).ToList());
            }
        }
        _vm.MonthCells = CalendarViewModel.BuildMonth(_vm.VisibleMonth, now.Date, _vm.SelectedDate.Date, marks);
        _vm.SelectedCell = _vm.MonthCells.FirstOrDefault(cell => cell.Date == _vm.SelectedDate.Date);
    }

    private void BuildAgenda(DateTimeOffset now)
    {
        var dayStart = new DateTimeOffset(_vm.SelectedDate.Date, TimeZoneInfo.Local.GetUtcOffset(_vm.SelectedDate.Date));
        var dayEnd = dayStart.AddDays(1);
        var rows = _events
            .Where(e => e.Start < dayEnd && e.End > dayStart)
            .OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start)
            .Select(e => new CalendarEventViewModel(e, TogglePin, Dismiss))
            .ToList();
        _vm.SelectedDayRows = rows;
        _vm.HasSelectedDayEvents = rows.Count > 0;
        _vm.SelectedDayCount = rows.Count switch { 0 => "No events", 1 => "1 event", _ => $"{rows.Count} events" };
    }

    private void OnTick()
    {
        if (_context is null) return;
        var now = _time.GetLocalNow();
        if (_shownDate != now.Date && _vm.SelectedDate.Date == _shownDate)
        {
            // The day rolled over while the agenda followed today; move both with it.
            _vm.VisibleMonth = new DateTime(now.Year, now.Month, 1);
            _vm.SelectedDate = now.Date;
        }
        if (_shownDate != now.Date || _vm.Days.SelectMany(d => d.Events).Any(row => row.Event.End <= now && row.Event.Start < now))
            BuildPresentation(now);
        else
            UpdateRowStates();
        UpdateNext();
        CheckAlerts();
    }

    private void UpdateNext()
    {
        if (_context is null) return;
        var now = _time.GetLocalNow();
        var settings = _context.Settings.Current.Calendar;
        var pinned = _events.FirstOrDefault(e => e.OccurrenceKey == settings.PinnedOccurrence && e.End > now);
        var next = pinned ?? _events.FirstOrDefault(e => !e.AllDay && e.End > now && !settings.DismissedOccurrences.ContainsKey(e.OccurrenceKey));

        var show = next is not null && (pinned is not null || next.Start <= now.Add(PillHorizon));
        if (show)
        {
            if (!_nextPosted) { _context.Post("next", _next, ActivityPriority.Ambient); _nextPosted = true; }
            if (!ReferenceEquals(next, _nextEvent))
            {
                _nextEvent = next;
                _next.Title = next!.DisplayTitle;
                _next.Subtitle = next.Detail;
                _next.Color = next.Color;
                _next.HasEvent = true;
            }
            _next.IsPinned = pinned is not null;
            _next.When = next!.AllDay ? "All day" : IcsCalendar.Relative(next.Start, now);
        }
        else
        {
            if (_nextPosted) { _context.Remove("next"); _nextPosted = false; }
            if (_nextEvent is not null)
            {
                _nextEvent = null;
                _next.HasEvent = false;
            }
        }
    }

    private void CheckAlerts()
    {
        if (_context is null) return;
        var settings = _context.Settings.Current.Calendar;
        if (settings.AlertMinutesBefore <= 0) return;
        var now = _time.GetLocalNow();
        var window = TimeSpan.FromMinutes(settings.AlertMinutesBefore);

        foreach (var e in _events)
        {
            if (e.AllDay || e.Start <= now || e.Start - now > window || settings.DismissedOccurrences.ContainsKey(e.OccurrenceKey)) continue;
            if (!_alerted.Add(e.OccurrenceKey)) continue;
            if (FocusService.IsQuietNow) continue; // Do Not Disturb: no popup or sound
            var minutes = Math.Max(1, (int)Math.Ceiling((e.Start - now).TotalMinutes));
            _context.Post("alert", new CalendarAlertViewModel(e.DisplayTitle, $"starts in {minutes} min", e.Color),
                ActivityPriority.High, TimeSpan.FromSeconds(8), alert: true);
            if (settings.SoundOnAlert) SystemSounds.Asterisk.Play();
        }
    }

    private void TogglePin(CalendarEventItem item)
    {
        if (_context is null) return;
        var settings = _context.Settings.Current.Calendar;
        settings.PinnedOccurrence = settings.PinnedOccurrence == item.OccurrenceKey ? null : item.OccurrenceKey;
        settings.DismissedOccurrences.Remove(item.OccurrenceKey);
        SavePresentation();
    }

    private void Dismiss(CalendarEventItem item)
    {
        if (_context is null) return;
        var settings = _context.Settings.Current.Calendar;
        settings.DismissedOccurrences[item.OccurrenceKey] = item.End;
        if (settings.PinnedOccurrence == item.OccurrenceKey) settings.PinnedOccurrence = null;
        _context.Remove("alert");
        SavePresentation();
    }

    private void SavePresentation()
    {
        UpdateRowStates();
        UpdateNext();
        _context!.Settings.NotifyChanged();
    }

    private void UpdateRowStates()
    {
        if (_context is null) return;
        var settings = _context.Settings.Current.Calendar;
        var now = _time.GetLocalNow();
        var rows = _vm.Days.SelectMany(d => d.Events).ToList();
        foreach (var row in rows)
        {
            row.IsPinned = row.Event.OccurrenceKey == settings.PinnedOccurrence;
            row.IsDismissed = settings.DismissedOccurrences.ContainsKey(row.Event.OccurrenceKey);
        }
        // Only what is on now and the one event after it get a time badge, so the eye lands there.
        // One-line rows show their start time in the badge's place instead.
        var next = rows.FirstOrDefault(r => !r.IsCompact && !r.Event.AllDay && !r.IsDismissed && r.Event.Start > now && r.Event.Start - now < PillHorizon);
        foreach (var row in rows)
        {
            var isNow = !row.Event.AllDay && row.Event.Start <= now && row.Event.End > now;
            row.IsHighlighted = !row.IsDismissed && (isNow || ReferenceEquals(row, next));
            row.Badge = row.IsDismissed ? "Hidden" : isNow ? "Now" : ReferenceEquals(row, next) ? IcsCalendar.Relative(row.Event.Start, now)
                : row.IsCompact ? row.Event.StartText : "";
        }
        foreach (var row in _vm.SelectedDayRows)
        {
            row.IsPinned = row.Event.OccurrenceKey == settings.PinnedOccurrence;
            row.IsDismissed = settings.DismissedOccurrences.ContainsKey(row.Event.OccurrenceKey);
            row.IsHighlighted = !row.IsDismissed && !row.Event.AllDay && row.Event.Start <= now && row.Event.End > now;
            row.Badge = row.IsDismissed ? "Hidden" : "";
        }
    }

    private void PruneDismissals(DateTimeOffset now)
    {
        if (_context is null) return;
        var settings = _context.Settings.Current.Calendar;
        var expired = settings.DismissedOccurrences.Where(e => e.Value <= now).Select(e => e.Key).ToList();
        foreach (var key in expired) settings.DismissedOccurrences.Remove(key);
        if (expired.Count > 0) _context.Settings.NotifyChanged();
    }

    private static string SourceSignature(CalendarSettings s) =>
        string.Join("\n", s.Sources.Select(x => $"{x.Name}|{x.Color}|{x.EncryptedUrl}"));

    private static string DayLabel(DateTime day, DateTime today)
    {
        if (day == today) return "Today";
        if (day == today.AddDays(1)) return "Tomorrow";
        if (day < today.AddDays(7)) return day.ToString("dddd");
        return day.ToString("ddd, MMM d");
    }

    public void Dispose()
    {
        _disposed = true;
        _refreshTimer?.Stop();
        _ticker?.Stop();
        _cts?.Cancel();
        _vm.PropertyChanged -= OnCalendarPropertyChanged;
        if (_context is not null) _context.Settings.Changed -= OnSettingsChanged;
    }
}
