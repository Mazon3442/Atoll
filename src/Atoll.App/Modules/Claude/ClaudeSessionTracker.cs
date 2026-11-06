using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Atoll.Claude;
using Atoll.Shell;

namespace Atoll.Modules.Claude;

public enum ClaudeState
{
    Idle,
    Thinking,
    Working,
    NeedsYou,
    Done,
    Error,
}

public enum ClaudeTransition
{
    None,
    NeedsYou,
    Finished,
    Ended,
}

public sealed partial class ClaudeSessionViewModel(string sessionId) : ObservableObject
{
    public string SessionId { get; } = sessionId;
    public string Agent { get; init; } = "claude";
    public string AgentName => HookProtocol.AgentName(Agent);
    public bool IsClaude => Agent == "claude";

    [ObservableProperty] private string _project = "Claude Code";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsWorking), nameof(NeedsYou))]
    private ClaudeState _state;

    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _elapsed = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFocus))]
    private long _hwnd;

    /// <summary>The agent's process id when its adapter reports one; lets the island notice it was closed.</summary>
    public int AgentPid { get; set; }

    public DateTimeOffset TurnStarted { get; set; }
    public DateTimeOffset LastEvent { get; set; }

    public bool IsActive => State is ClaudeState.Thinking or ClaudeState.Working or ClaudeState.NeedsYou;
    public bool IsWorking => State is ClaudeState.Thinking or ClaudeState.Working;
    public bool NeedsYou => State == ClaudeState.NeedsYou;
    public bool CanFocus => Hwnd != 0;

    [RelayCommand]
    private void Focus() => WindowFocus.Bring((nint)Hwnd);
}

/// <summary>Turns the stream of hook events into per-session state. UI thread only.</summary>
public sealed class ClaudeSessionTracker(TimeProvider time, Func<int, bool>? isProcessRunning = null)
{
    private static readonly TimeSpan ForgetIdleAfter = TimeSpan.FromHours(3);
    private readonly Func<int, bool> _isProcessRunning = isProcessRunning ?? ProcessRunning;

    public ObservableCollection<ClaudeSessionViewModel> Sessions { get; } = [];

    /// <summary>The session the compact island should talk about: one that needs you, else the latest busy one.</summary>
    public ClaudeSessionViewModel? Focus =>
        Sessions.FirstOrDefault(s => s.NeedsYou)
        ?? Sessions.Where(s => s.IsWorking).MaxBy(s => s.LastEvent);

    public (ClaudeSessionViewModel? Session, ClaudeTransition Transition) Apply(HookMessage m)
    {
        if (string.IsNullOrEmpty(m.SessionId) || !HookProtocol.Agents.Contains(m.Agent)) return (null, ClaudeTransition.None);
        var now = time.GetUtcNow();
        var s = Sessions.FirstOrDefault(x => x.SessionId == m.SessionId && x.Agent == m.Agent);

        if (m.Event == "SessionEnd")
        {
            if (s is not null) Sessions.Remove(s);
            return (s, ClaudeTransition.Ended);
        }

        if (s is null)
        {
            s = new ClaudeSessionViewModel(m.SessionId)
            { Agent = m.Agent, Project = HookProtocol.AgentName(m.Agent), TurnStarted = now };
            Sessions.Insert(0, s);
        }
        if (!string.IsNullOrEmpty(m.Cwd)) s.Project = ProjectName(m.Cwd);
        if (m.Hwnd != 0) s.Hwnd = m.Hwnd;
        if (m.AgentPid != 0) s.AgentPid = m.AgentPid;
        s.LastEvent = now;

        switch (m.Event)
        {
            case "SessionStart":
                if (!s.IsActive) Set(s, ClaudeState.Idle, "Ready");
                break;

            case "UserPromptSubmit":
                s.TurnStarted = now;
                Set(s, ClaudeState.Thinking, "Thinking");
                break;

            case "PreToolUse":
                StartTurnIfNeeded(s, now);
                Set(s, ClaudeState.Working, m.Detail ?? "Working");
                break;

            case "PostToolUse":
                StartTurnIfNeeded(s, now);
                Set(s, ClaudeState.Thinking, "Thinking");
                break;

            case "PermissionRequest":
            case "Notification" when IsPermissionRequest(m):
                var wasWaiting = s.NeedsYou;
                StartTurnIfNeeded(s, now);
                Set(s, ClaudeState.NeedsYou, m.Message ?? "Needs your permission");
                return (s, wasWaiting ? ClaudeTransition.None : ClaudeTransition.NeedsYou);

            case "Stop":
                if (s.State == ClaudeState.Done) break;
                Set(s, ClaudeState.Done, $"Done in {FormatDuration(now - s.TurnStarted)}");
                s.Elapsed = "";
                return (s, ClaudeTransition.Finished);
            case "Interrupt":
                Set(s, ClaudeState.Idle, "Interrupted");
                s.Elapsed = "";
                break;
            case "Error":
                Set(s, ClaudeState.Error, "Session failed");
                s.Elapsed = "";
                break;
        }
        return (s, ClaudeTransition.None);
    }

    /// <summary>
    /// Refreshes elapsed times, drops sessions whose agent process has exited (closing an agent sends no
    /// final event) and forgets long-idle sessions. Call about once a second.
    /// </summary>
    public void Tick()
    {
        var now = time.GetUtcNow();
        foreach (var s in Sessions.ToList())
        {
            if (s.AgentPid != 0 && !_isProcessRunning(s.AgentPid)) Sessions.Remove(s);
            else if (s.IsActive) s.Elapsed = FormatDuration(now - s.TurnStarted);
            else if (now - s.LastEvent > ForgetIdleAfter) Sessions.Remove(s);
        }
    }

    private static bool ProcessRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }          // no such process
        catch (InvalidOperationException) { return false; }  // exited while we looked
        catch (System.ComponentModel.Win32Exception) { return true; } // access denied: it exists
    }

    private static void StartTurnIfNeeded(ClaudeSessionViewModel s, DateTimeOffset now)
    {
        // Events can arrive without a UserPromptSubmit (island started mid-turn).
        if (!s.IsActive) s.TurnStarted = now;
    }

    private static void Set(ClaudeSessionViewModel s, ClaudeState state, string detail)
    {
        s.State = state;
        s.Detail = detail;
    }

    private static bool IsPermissionRequest(HookMessage m) =>
        m.NotificationType == "permission_prompt"
        || (m.NotificationType is null && m.Message?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true);

    internal static string ProjectName(string cwd)
    {
        var name = Path.GetFileName(cwd.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(name) ? cwd : name;
    }

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
        : $"{Math.Max(0, (int)t.TotalSeconds)}s";
}
