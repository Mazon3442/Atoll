using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
#if !AVALONIA
using System.Media;
#endif
#if AVALONIA
using Avalonia.Threading;
using Atoll.Desktop;
#else
using System.Windows.Threading;
#endif
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using Atoll.Activities;
using Atoll.Claude;
using Atoll.Settings;

namespace Atoll.Modules.Claude;

public sealed partial class UsageViewModel : ObservableObject
{
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _hasBlock;
    [ObservableProperty] private string _blockText = "";
    [ObservableProperty] private string _blockResets = "";
    [ObservableProperty] private double _blockProgress;
    [ObservableProperty] private string _todayText = "";
    [ObservableProperty] private bool _hasLimits;

    /// <summary>Plan limits (5-hour, weekly) as Claude and Codex report them.</summary>
    public ObservableCollection<PlanLimitRow> Limits { get; } = [];

    public void SetLimits(IEnumerable<PlanLimit> limits, DateTimeOffset now)
    {
        Limits.Clear();
        foreach (var limit in limits) Limits.Add(new PlanLimitRow(limit));
        HasLimits = Limits.Count > 0;
        RefreshLimits(now);
    }

    public void RefreshLimits(DateTimeOffset now)
    {
        foreach (var row in Limits) row.Refresh(now);
    }

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

public sealed partial class PlanLimitRow(PlanLimit limit) : ObservableObject
{
    public string Name => limit.Name;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private string _resetText = "";
    [ObservableProperty] private bool _isHigh;

    public void Refresh(DateTimeOffset now)
    {
        var passed = limit.ResetsAt is { } at && at <= now; // the window already restarted
        Progress = passed ? 0 : Math.Clamp(limit.Percent / 100, 0, 1);
        PercentText = $"{Progress * 100:0}% used";
        IsHigh = Progress >= 0.8;
        ResetText = limit.ResetsAt is not { } r ? "" : passed ? "reset" : r - now < TimeSpan.FromDays(1)
            ? $"resets in {ClaudeSessionTracker.FormatDuration(r - now)}"
            : $"resets {r.ToLocalTime():ddd h tt}";
    }
}

/// <summary>Shown as the "work" activity, the Claude tab and the usage summary.</summary>
public sealed partial class ClaudeViewModel(ClaudeSessionTracker tracker, AskViewModel? ask = null) : ObservableObject
{
    public ClaudeSessionTracker Tracker { get; } = tracker;
    public UsageViewModel Usage { get; } = new();
    public AskViewModel Ask { get; } = ask ?? new AskViewModel();

    [ObservableProperty] private ClaudeSessionViewModel? _current;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private string _countBadge = "";
    [ObservableProperty] private string _connectionStatus = "Waiting for agent events. Codex also uses recent local session events when hooks are unavailable.";

    /// <summary>Sessions is where the tab opens; Ask is one switch away.</summary>
    public IReadOnlyList<string> Sections { get; } = ["Sessions", "Ask"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessions), nameof(IsAsk))]
    private int _sectionIndex;

    public bool IsSessions => SectionIndex == 0;
    public bool IsAsk => SectionIndex == 1;

    public bool HasSessions => Tracker.Sessions.Count > 0;

    public void Refresh()
    {
        Current = Tracker.Focus;
        ActiveCount = Tracker.Sessions.Count(s => s.IsActive);
        CountBadge = ActiveCount > 1 ? $"+{ActiveCount - 1}" : "";
        OnPropertyChanged(nameof(HasSessions));
    }
}

/// <summary>
/// Posted when an Ask answer arrives, so a collapsed island can say so instead of losing the answer in a closed tab.
/// Clicking the island opens the Agents tab, where the full answer is still waiting.
/// </summary>
public sealed record AskAlertViewModel(AskViewModel Ask, string Title, string Detail);

/// <summary>
/// Done / needs-you popup. Opening it focuses the session's terminal or editor. With <paramref name="Answer"/>,
/// a permission prompt can be allowed or denied right here; "Terminal" hands it back to the terminal.
/// </summary>
public sealed record ClaudeAlertViewModel(ClaudeSessionViewModel Session, string Title, string Detail, Action<string?>? Answer = null)
{
    public bool CanAnswer => Answer is not null;
    public bool CanOpen => Session.CanFocus && !CanAnswer;
    public IRelayCommand AllowCommand { get; } = new RelayCommand(() => Answer?.Invoke("allow"));
    public IRelayCommand DenyCommand { get; } = new RelayCommand(() => Answer?.Invoke("deny"));
    public IRelayCommand TerminalCommand { get; } = new RelayCommand(() => { Answer?.Invoke(null); Session.FocusCommand.Execute(null); });
}

/// <summary>
/// Live Claude Code activity (from hooks), done/needs-you alerts and token usage (from transcripts).
/// </summary>
public sealed class ClaudeModule : IIslandModule
{
    private readonly ClaudeSessionTracker _tracker = new(TimeProvider.System);
    private readonly ClaudeViewModel _vm;
    private readonly HookPipeServer _server = new();
    private readonly CodexSessionMonitor _codex = new();
    private readonly Dictionary<string, DateTimeOffset> _codexHooks = [];
    private readonly UsageMonitor _usage = new(UsageMonitor.DefaultRoot);
    private readonly AgentChatService _chat = new();
    private ModuleContext? _context;
    private DispatcherTimer? _ticker;
    private bool _workPosted;
    private bool _disposed;
    private readonly HashSet<string> _attention = [];
    private readonly Dictionary<string, TaskCompletionSource<string?>> _pending = []; // attention key → hook waiting for an answer
    private static readonly HttpClient LimitsHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyList<PlanLimit> _claudeLimits = [];
    private DateTimeOffset _nextLimitsCheck;

    public ClaudeModule() => _vm = new ClaudeViewModel(_tracker, new AskViewModel(_chat,
        () => new AgentChatOptions(_context?.Settings.Current.AgentChat ?? new AgentChatSettings(),
            _context?.Settings.Current.Providers.Accounts ?? []),
        action =>
        {
            if (_context is { } context) context.Dispatcher.BeginInvoke(action);
            else action();
        },
        () => _context?.Settings.NotifyChanged(),
#if AVALONIA
        () => false)); // the Avalonia shell has no Ask box, so nothing types out
#else
        () => System.Windows.SystemParameters.ClientAreaAnimation
            && _context?.Settings.Current.Theme.Motion != Settings.MotionStyle.Off));
#endif

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _vm.Ask.Answered += OnAnswered;
        _vm.PropertyChanged += OnPanelPropertyChanged;
        _server.Received += m => context.Dispatcher.BeginInvoke(() =>
        {
            if (m.Agent == "codex") _codexHooks[m.SessionId] = DateTimeOffset.UtcNow;
            _vm.ConnectionStatus = $"Last hook: {HookProtocol.AgentName(m.Agent)} · {m.Event} · {DateTimeOffset.Now:t}";
            OnHook(m);
        });
        _server.Decide = m =>
        {
            var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Dispatcher.BeginInvoke(() =>
            {
                _vm.ConnectionStatus = $"Last hook: {HookProtocol.AgentName(m.Agent)} · {m.Event} · {DateTimeOffset.Now:t}";
                OnHook(m, answer);
            });
            return answer.Task;
        };
        _server.Start();
        _codex.Received += m => context.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _codexHooks.TryGetValue(m.SessionId, out var seen) && DateTimeOffset.UtcNow - seen < TimeSpan.FromSeconds(15)) return;
            _vm.ConnectionStatus = $"Codex connected through local session events · {DateTimeOffset.Now:t}. Hooks remain available for permission alerts.";
            OnHook(m);
        });
        _codex.Start();

        _usage.Updated += s => context.Dispatcher.BeginInvoke(() => _vm.Usage.Update(s, DateTimeOffset.Now));
        _usage.Start();

        _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) =>
        {
            _tracker.Tick();
            _vm.Refresh();
            UpdateActivities();
            if (DateTimeOffset.Now >= _nextLimitsCheck) _ = CheckLimitsAsync();
            else _vm.Usage.RefreshLimits(DateTimeOffset.Now);
        };
        _ticker.Start();
    }

    private async Task CheckLimitsAsync()
    {
        _nextLimitsCheck = DateTimeOffset.Now.AddMinutes(5);
        try { _claudeLimits = await PlanLimits.ReadClaudeAsync(LimitsHttp, PlanLimits.ClaudeCredentialsPath, _stop.Token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or TaskCanceledException or UnauthorizedAccessException)
        {
            Log.Info($"Claude plan limits unavailable: {ex.Message}"); // keep the last known numbers
        }
        var codex = await Task.Run(() => PlanLimits.ReadCodex(PlanLimits.CodexSessionsRoot));
        if (!_disposed) _vm.Usage.SetLimits(_claudeLimits.Concat(codex), DateTimeOffset.Now);
    }

    private void OnHook(HookMessage message, TaskCompletionSource<string?>? answer = null)
    {
        if (_disposed) { answer?.TrySetResult(null); return; }
        if (message.Agent == "shell") { OnCommandFinished(message); return; }
        var (session, transition) = _tracker.Apply(message);
        if (answer is not null && (session is null || !session.NeedsYou)) answer.TrySetResult(null);
        _vm.Refresh();
        UpdateActivities();
        if (session is null || _context is null) return;

        var settings = _context.Settings.Current.Claude;
        switch (transition)
        {
            case ClaudeTransition.NeedsYou:
                var key = AttentionKey(session);
                _attention.Add(key);
                Action<string?>? reply = null;
                if (answer is not null)
                {
                    _pending[key] = answer;
                    reply = choice =>
                    {
                        if (!_pending.Remove(key, out var waiting) || !waiting.TrySetResult(choice) || choice is null) return;
                        session.State = ClaudeState.Thinking; // the hook answered; don't wait for the next event to clear the prompt
                        session.Detail = choice == "allow" ? "Allowed from ATOLL" : "Denied from ATOLL";
                        _context.Remove(key);
                        _attention.Remove(key);
                    };
                }
                _context.Post(key, new ClaudeAlertViewModel(session, $"{session.Project} needs you", session.Detail, reply),
                    ActivityPriority.Critical, alert: true);
                if (settings.SoundOnAttention) SystemSounds.Exclamation.Play();
                break;
            case ClaudeTransition.Finished:
                if (FocusService.IsQuietNow) break;
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
        var waiting = _tracker.Sessions.Where(s => s.NeedsYou).Select(AttentionKey).ToHashSet();
        foreach (var key in _attention.Where(k => !waiting.Contains(k)).ToList())
        {
            _context.Remove(key);
            _attention.Remove(key);
        }
        // Answered in the terminal or the session moved on: release the waiting hook.
        foreach (var key in _pending.Keys.Where(k => !waiting.Contains(k)).ToList())
            if (_pending.Remove(key, out var stale)) stale.TrySetResult(null);
    }

    /// <summary>A long PowerShell command finished while its terminal was in the background.</summary>
    private void OnCommandFinished(HookMessage m)
    {
        if (_context is null || FocusService.IsQuietNow) return; // the hook already skipped commands whose terminal is in front
        var failed = m.NotificationType is { } code && code != "0";
        var project = m.Cwd is { Length: > 0 } cwd ? Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd)) : "";
        var session = new ClaudeSessionViewModel("shell:" + m.SessionId) { Agent = "shell", Hwnd = m.Hwnd, Project = project.Length > 0 ? project : "PowerShell" };
        _context.Post("command", new ClaudeAlertViewModel(session, failed ? $"Failed · exit {m.NotificationType}" : "Command finished",
            $"{m.Detail} · {m.Message}"), failed ? ActivityPriority.High : ActivityPriority.Normal, TimeSpan.FromSeconds(8), alert: true);
        if (_context.Settings.Current.Claude.SoundOnDone) (failed ? SystemSounds.Exclamation : SystemSounds.Asterisk).Play();
    }

    private static string AttentionKey(ClaudeSessionViewModel session) => $"attention.{session.Agent}.{session.SessionId}";

    /// <summary>The answer stays in the Ask section; this only says it arrived while the island may be closed.</summary>
    private void OnAnswered(AskViewModel ask)
    {
        if (_disposed || _context is null) return;
        // Already looking at the answer: nothing to announce, and any older alert is stale.
        if (_panelShown && _vm.IsAsk)
        {
            _context.Remove("ask");
            return;
        }
        var preview = ask.Answer.ReplaceLineEndings(" ").Trim();
        if (preview.Length > 110) preview = preview[..110] + "…";
        var who = ask.Provider.Length > 0 ? ask.Provider : "the agent";
        _context.Post("ask", new AskAlertViewModel(ask, $"Answer from {who}", preview),
            ActivityPriority.High, TimeSpan.FromMinutes(2));
    }

    private bool _panelShown;

    /// <summary>Seeing the answer clears the alert that announced it.</summary>
    public void OnPanelShown()
    {
        _panelShown = true;
        ClearAskAlert();
    }

    public void OnPanelHidden() => _panelShown = false;

    private void ClearAskAlert()
    {
        if (_vm.IsAsk) _context?.Remove("ask");
    }

    private void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClaudeViewModel.SectionIndex)) ClearAskAlert();
    }

    public void Dispose()
    {
        _disposed = true;
        _vm.PropertyChanged -= OnPanelPropertyChanged;
        _vm.Ask.Answered -= OnAnswered;
        _vm.Ask.Dispose();
        _chat.Dispose();
        _stop.Cancel();
        foreach (var waiting in _pending.Values) waiting.TrySetResult(null);
        _ticker?.Stop();
        _server.Dispose();
        _codex.Dispose();
        _usage.Dispose();
    }
}
