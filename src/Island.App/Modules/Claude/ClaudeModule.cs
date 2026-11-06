using System.Globalization;
using System.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Island.Activities;
using Island.Claude;

namespace Island.Modules.Claude;

public sealed partial class UsageViewModel : ObservableObject
{
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _hasBlock;
    [ObservableProperty] private string _blockText = "";
    [ObservableProperty] private string _blockResets = "";
    [ObservableProperty] private double _blockProgress;
    [ObservableProperty] private string _todayText = "";

    public void Update(UsageSummary s, DateTimeOffset now)
    {
        HasData = s.TodayTokens > 0 || s.BlockTokens > 0;
        HasBlock = s.BlockStart is not null;
        BlockText = $"{Tokens(s.BlockTokens)} tokens · ≈{Money(s.BlockCost)}";
        if (s.BlockEnd is { } end && s.BlockStart is { } start)
        {
            BlockResets = $"resets in {ClaudeSessionTracker.FormatDuration(end - now)}";
            BlockProgress = Math.Clamp((now - start) / UsageCalculator.BlockLength, 0, 1);
        }
        TodayText = $"{Tokens(s.TodayTokens)} tokens · ≈{Money(s.TodayCost)}";
    }

    internal static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.0}B",
        >= 1_000_000 => $"{n / 1e6:0.0}M",
        >= 1_000 => $"{n / 1e3:0}K",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    private static string Money(decimal d) => d.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
}

/// <summary>Shown as the "work" activity, the Claude tab and the usage summary.</summary>
public sealed partial class ClaudeViewModel(ClaudeSessionTracker tracker) : ObservableObject
{
    public ClaudeSessionTracker Tracker { get; } = tracker;
    public UsageViewModel Usage { get; } = new();

    [ObservableProperty] private ClaudeSessionViewModel? _current;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private string _countBadge = "";

    public bool HasSessions => Tracker.Sessions.Count > 0;

    public void Refresh()
    {
        Current = Tracker.Focus;
        ActiveCount = Tracker.Sessions.Count(s => s.IsActive);
        CountBadge = ActiveCount > 1 ? $"+{ActiveCount - 1}" : "";
        OnPropertyChanged(nameof(HasSessions));
    }
}

/// <summary>Done / needs-you popup. Opening it focuses the session's terminal or editor.</summary>
public sealed record ClaudeAlertViewModel(ClaudeSessionViewModel Session, string Title, string Detail);

/// <summary>
/// Live Claude Code activity (from hooks), done/needs-you alerts and token usage (from transcripts).
/// </summary>
public sealed class ClaudeModule : IIslandModule
{
    private readonly ClaudeSessionTracker _tracker = new(TimeProvider.System);
    private readonly ClaudeViewModel _vm;
    private readonly HookPipeServer _server = new();
    private readonly UsageMonitor _usage = new(UsageMonitor.DefaultRoot);
    private ModuleContext? _context;
    private DispatcherTimer? _ticker;
    private bool _workPosted;

    public ClaudeModule() => _vm = new ClaudeViewModel(_tracker);

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _server.Received += m => context.Dispatcher.BeginInvoke(() => OnHook(m));
        _server.Start();

        _usage.Updated += s => context.Dispatcher.BeginInvoke(() => _vm.Usage.Update(s, DateTimeOffset.Now));
        _usage.Start();

        _ticker = new DispatcherTimer(DispatcherPriority.Background, context.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) =>
        {
            _tracker.Tick();
            _vm.Refresh();
            UpdateActivities();
        };
        _ticker.Start();
    }

    private void OnHook(HookMessage message)
    {
        var (session, transition) = _tracker.Apply(message);
        _vm.Refresh();
        UpdateActivities();
        if (session is null || _context is null) return;

        var settings = _context.Settings.Current.Claude;
        switch (transition)
        {
            case ClaudeTransition.NeedsYou:
                _context.Post("attention", new ClaudeAlertViewModel(session, $"{session.Project} needs you", session.Detail),
                    ActivityPriority.Critical, alert: true);
                if (settings.SoundOnAttention) SystemSounds.Exclamation.Play();
                break;
            case ClaudeTransition.Finished:
                _context.Post("done", new ClaudeAlertViewModel(session, $"{session.Project} is done", session.Detail),
                    ActivityPriority.High, TimeSpan.FromSeconds(6), alert: true);
                if (settings.SoundOnDone) SystemSounds.Asterisk.Play();
                break;
        }
    }

    private void UpdateActivities()
    {
        if (_context is null) return;
        var anyWorking = _tracker.Sessions.Any(s => s.IsWorking);
        if (anyWorking && !_workPosted) _context.Post("work", _vm, ActivityPriority.Normal);
        else if (!anyWorking && _workPosted) _context.Remove("work");
        _workPosted = anyWorking;

        // The permission popup stays until that session moves on (approved, denied or stopped).
        if (!_tracker.Sessions.Any(s => s.NeedsYou)) _context.Remove("attention");
    }

    public void Dispose()
    {
        _ticker?.Stop();
        _server.Dispose();
        _usage.Dispose();
    }
}
