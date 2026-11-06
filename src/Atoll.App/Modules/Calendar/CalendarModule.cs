using System.Collections.ObjectModel;
using System.Media;
using System.Net.Http;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Calendar;

/// <summary>Events for one day in the expanded panel.</summary>
public sealed class CalendarDay
{
    public required string Label { get; init; }
    public List<CalendarEventItem> Events { get; init; } = [];
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

    [ObservableProperty] private string _updated = "";

    public ObservableCollection<CalendarDay> Days { get; } = [];

    /// <summary>True when the empty-state placeholder should show instead of the event list.</summary>
    public bool ShowEmpty => !HasEvents;

    public string EmptyMessage => !HasSources
        ? "Add a calendar in Settings › Calendar"
        : string.IsNullOrEmpty(Status) ? "No upcoming events" : Status;

    [RelayCommand]
    private void Refresh() => refresh();
}

/// <summary>The pill: the next event and its countdown.</summary>
public sealed partial class NextEventViewModel : ObservableObject
{
    [ObservableProperty] private bool _hasEvent;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _when = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _color = "#0A84FF";
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
    private static readonly TimeSpan PillHorizon = TimeSpan.FromHours(24);
    private static readonly int WindowDays = 14;
    private static readonly HttpClient Http = CreateClient();

    private readonly CalendarViewModel _vm;
    private readonly NextEventViewModel _next = new();
    private ModuleContext? _context;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _ticker;
    private CancellationTokenSource? _cts;
    private List<CalendarEventItem> _events = [];
    private string _sourceSignature = "";
    private bool _nextPosted;
    private CalendarEventItem? _nextEvent;
    private readonly HashSet<string> _alerted = [];

    public CalendarModule() => _vm = new CalendarViewModel(() => _ = RefreshAsync());

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        context.Settings.Changed += OnSettingsChanged;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, context.Dispatcher) { Interval = RefreshEvery };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _refreshTimer.Start();
        _ticker = new DispatcherTimer(DispatcherPriority.Background, context.Dispatcher) { Interval = TickEvery };
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

    private async Task RefreshAsync()
    {
        if (_context is null) return;
        var settings = _context.Settings.Current.Calendar;
        var signature = SourceSignature(settings);
        var sourcesChanged = signature != _sourceSignature;
        _sourceSignature = signature;
        if (sourcesChanged) _alerted.Clear();

        var now = DateTimeOffset.Now;
        var windowStart = now;
        var windowEnd = now.AddDays(WindowDays);
        var sources = settings.Sources
            .Select(s => (Source: s, Url: SecretStore.Unprotect(s.EncryptedUrl)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .ToList();

        _vm.HasSources = sources.Count > 0;
        if (sources.Count == 0)
        {
            _vm.HasEvents = false;
            _vm.Status = "";
            _events = [];
            UpdateNext();
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var tasks = sources.Select(async x =>
            {
                try
                {
                    var ics = await Http.GetStringAsync(x.Url!, cts.Token).ConfigureAwait(false);
                    var parsed = IcsCalendar.Parse(ics, x.Source.Name, x.Source.Color, windowStart, windowEnd);
                    return (Events: (IReadOnlyList<CalendarEventItem>)parsed, Error: false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
                {
                    if (cts.IsCancellationRequested) throw;
                    Log.Info($"Calendar \"{x.Source.Name}\" download failed: {ex.Message}");
                    return (Events: (IReadOnlyList<CalendarEventItem>)[], Error: true);
                }
            });
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            var merged = results.SelectMany(r => r.Events).OrderBy(e => e.Start).ThenBy(e => e.Title).ToList();
            var failed = results.Count(r => r.Error);
            _events = merged;
            _vm.HasEvents = merged.Count > 0;
            _vm.Updated = $"Updated {DateTime.Now:t}";
            _vm.Status = failed == 0 ? "" : failed == results.Length ? "Couldn't reach any calendar. Check your connection." : $"{failed} calendar{(failed == 1 ? "" : "s")} failed to refresh.";
            RebuildDays(now);
            UpdateNext();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
    }

    private void RebuildDays(DateTimeOffset now)
    {
        _vm.Days.Clear();
        foreach (var group in _events.GroupBy(e => e.Start.Date))
        {
            _vm.Days.Add(new CalendarDay
            {
                Label = DayLabel(group.Key, now.Date),
                Events = group.ToList(),
            });
        }
    }

    private void OnTick()
    {
        if (_context is null) return;
        UpdateNext();
        CheckAlerts();
    }

    private void UpdateNext()
    {
        if (_context is null) return;
        var now = DateTimeOffset.Now;
        var next = _events.FirstOrDefault(e => e.End > now);

        var show = next is not null && next.Start <= now.Add(PillHorizon);
        if (show)
        {
            if (!_nextPosted) { _context.Post("next", _next, ActivityPriority.Ambient); _nextPosted = true; }
            if (!ReferenceEquals(next, _nextEvent))
            {
                _nextEvent = next;
                _next.Title = next!.DisplayTitle;
                _next.Subtitle = next.Subtitle;
                _next.Color = next.Color;
                _next.HasEvent = true;
            }
            _next.When = IcsCalendar.Relative(next!.Start, now);
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
        var now = DateTimeOffset.Now;
        var window = TimeSpan.FromMinutes(settings.AlertMinutesBefore);

        foreach (var e in _events)
        {
            if (e.AllDay || e.Start <= now || e.Start - now > window) continue;
            if (!_alerted.Add(Key(e))) continue;
            var minutes = Math.Max(1, (int)Math.Ceiling((e.Start - now).TotalMinutes));
            _context.Post("alert", new CalendarAlertViewModel(e.DisplayTitle, $"starts in {minutes} min", e.Color),
                ActivityPriority.High, TimeSpan.FromSeconds(8), alert: true);
            if (settings.SoundOnAlert) SystemSounds.Asterisk.Play();
        }
    }

    private static string Key(CalendarEventItem e) => $"{e.Uid}|{e.Start:O}";

    private static string SourceSignature(CalendarSettings s) =>
        string.Join("\n", s.Sources.Select(x => $"{x.Name}|{x.Color}|{x.EncryptedUrl}")) + $"|{s.AlertMinutesBefore}";

    private static string DayLabel(DateTime day, DateTime today)
    {
        if (day == today) return "Today";
        if (day == today.AddDays(1)) return "Tomorrow";
        if (day < today.AddDays(7)) return day.ToString("dddd");
        return day.ToString("ddd, MMM d");
    }

    public void Dispose()
    {
        _refreshTimer?.Stop();
        _ticker?.Stop();
        _cts?.Cancel();
        if (_context is not null) _context.Settings.Changed -= OnSettingsChanged;
    }
}
