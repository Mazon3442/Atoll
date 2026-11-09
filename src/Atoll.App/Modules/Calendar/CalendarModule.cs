using System.Collections.ObjectModel;
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
    private bool CanDismiss => !IsDismissed;
    [RelayCommand] private void TogglePin() => pin(Event);
    [RelayCommand(CanExecute = nameof(CanDismiss))] private void Dismiss() => dismiss(Event);
}

public sealed partial class CalendarViewModel(Action refresh) : ObservableObject
{
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
    private static readonly int WindowDays = 14;
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
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshEvery };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _refreshTimer.Start();
        _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickEvery };
        _ticker.Tick += (_, _) => OnTick();
        _ticker.Start();
        _ = RefreshAsync();
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ATOLL/1.0");
        return http;
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
        var windowStart = new DateTimeOffset(now.Date, TimeZoneInfo.Local.GetUtcOffset(now.Date));
        var windowEnd = now.AddDays(WindowDays);
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
                RebuildDays(now);
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
                RebuildDays(now);
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

    private void RebuildDays(DateTimeOffset now)
    {
        _shownDate = now.Date;
        _vm.Days.Clear();
        foreach (var group in _events.Where(e => e.End > now || e.Start >= now).GroupBy(e => e.Start.Date < now.Date ? now.Date : e.Start.Date))
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
        UpdateRowStates();
    }

    private void OnTick()
    {
        if (_context is null) return;
        var now = _time.GetLocalNow();
        if (_shownDate != now.Date || _vm.Days.SelectMany(d => d.Events).Any(row => row.Event.End <= now && row.Event.Start < now))
            RebuildDays(now);
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
        if (_context is not null) _context.Settings.Changed -= OnSettingsChanged;
    }
}
