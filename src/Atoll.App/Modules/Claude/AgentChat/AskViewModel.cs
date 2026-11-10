using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Claude;

/// <summary>
/// One question to the first available agent. Streaming text, Stop and readable errors; UI updates go through
/// <c>post</c> so this stays free of any UI framework and testable without a dispatcher.
/// </summary>
public sealed partial class AskViewModel : ObservableObject, IDisposable
{
    private readonly Func<AgentChatOptions> _options;
    private readonly Action<Action> _post;
    private readonly bool _ownsChat;
    private AgentChatService? _chatService;
    private CancellationTokenSource? _run;

    public AskViewModel(AgentChatService? chat = null, Func<AgentChatOptions>? options = null, Action<Action>? post = null)
    {
        _ownsChat = chat is null;
        _chatService = chat;
        _options = options ?? (() => new AgentChatOptions(new Settings.AgentChatSettings(), []));
        _post = post ?? (action => action());
    }

    /// <summary>The service is created on first use, so an island that never asks starts no HTTP client.</summary>
    private AgentChatService Chat => _chatService ??= new AgentChatService();

    /// <summary>Raised on the UI thread after a finished answer, so a collapsed island can show it.</summary>
    public event Action<AskViewModel>? Answered;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(HasDraft))]
    private string _draft = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer), nameof(IsEmpty))]
    private string _answer = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))]
    private string _error = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isRunning;

    /// <summary>Who answered: "Claude Code", "Codex", "OpenCode" or the saved key's name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(SourceLine), nameof(HasSource), nameof(HasStatusLine))]
    private string _provider = "";

    /// <summary>The honest tool policy for this run: chat-only, or what the CLI can't restrict.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceLine), nameof(HasSource), nameof(HasStatusLine))]
    private string _note = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusLine))]
    private string _status = "";

    public bool HasAnswer => Answer.Length > 0;
    public bool HasError => Error.Length > 0;
    public bool HasDraft => Draft.Length > 0;

    /// <summary>The empty state: nothing asked yet, nothing running, nothing wrong.</summary>
    public bool IsEmpty => !HasAnswer && !HasError && !IsRunning;

    public bool HasStatusLine => Status.Length > 0 || HasSource;

    /// <summary>"Ask Claude anything" once an agent has answered; plain "Ask anything" before that.</summary>
    public string Title => Provider.Length > 0 ? $"Ask {Provider} anything" : "Ask anything";

    /// <summary>What stays local, said plainly. The question still goes to whichever model answers it.</summary>
    public string Subtitle => "Chats stay in this session and are never saved.";

    public string SourceLine => Provider.Length == 0 ? "" : Note.Length == 0 ? Provider : $"{Provider} · {Note}";

    public bool HasSource => SourceLine.Length > 0;

    /// <summary>Things worth tapping in an empty box, so the first question is not a blank page.</summary>
    public IReadOnlyList<string> Suggestions { get; } =
    [
        "What can ATOLL do?",
        "How does Ask AI work?",
        "Convert 30 cm to inches",
    ];

    [RelayCommand]
    private void UseSuggestion(string? suggestion)
    {
        if (!string.IsNullOrWhiteSpace(suggestion)) Draft = suggestion;
    }

    private bool CanSend => Draft.Trim().Length > 0 && !IsRunning;
    private bool CanStop => IsRunning;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var prompt = Draft.Trim();
        if (prompt.Length == 0) return;
        var run = new CancellationTokenSource();
        _run = run;
        IsRunning = true;
        Answer = "";
        Error = "";
        Provider = "";
        Note = "";
        Status = "Finding an agent…";
        var options = _options();
        var text = new StringBuilder();
        var failed = false;
        var cancelled = false;

        try
        {
            var selection = await Chat.ResolveAsync(options, run.Token);
            _post(() =>
            {
                Provider = selection.Provider.Name;
                Note = selection.Note;
                Status = "Thinking…";
            });
            await foreach (var delta in Chat.StreamAsync(selection, prompt, options.Settings, run.Token))
            {
                if (delta.IsError)
                {
                    failed = true;
                    var message = delta.Text;
                    _post(() => { Error = message; Status = ""; });
                    continue;
                }
                text.Append(delta.Text);
                var snapshot = text.ToString();
                _post(() => { Answer = snapshot; Status = "Answering…"; });
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            cancelled = true;
            _post(() => Status = "Stopped.");
        }
        catch (AgentChatException ex)
        {
            failed = true;
            var message = ex.Message;
            _post(() => { Error = message; Status = ""; Answer = ""; });
        }
        catch (Exception)
        {
            failed = true;
            _post(() => { Error = "The agent could not answer. Check Settings › Agents."; Status = ""; });
        }
        finally
        {
            var answered = !cancelled && !failed && text.ToString().Trim().Length > 0;
            if (!answered && !run.IsCancellationRequested && !failed)
                _post(() => { Error = "The agent returned no text."; Status = ""; });
            _post(() =>
            {
                IsRunning = false;
                if (answered) Status = "";
                if (answered) Answered?.Invoke(this);
            });
            if (ReferenceEquals(_run, run)) _run = null;
            run.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        var run = _run;
        if (run is null) return;
        Status = "Stopping…";
        try { run.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    [RelayCommand]
    private void Clear()
    {
        Draft = "";
        Answer = "";
        Error = "";
        Status = "";
    }

    /// <summary>
    /// The island's Escape key checks with the Ask box first: stop a run, then clear what is left. False lets
    /// Escape close the island as usual.
    /// </summary>
    public bool HandleEscape()
    {
        if (IsRunning)
        {
            Stop();
            return true;
        }
        if (Draft.Length > 0)
        {
            Draft = "";
            return true;
        }
        if (Error.Length > 0 || Answer.Length > 0)
        {
            Error = "";
            Answer = "";
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        try { _run?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (_ownsChat) _chatService?.Dispose();
    }
}
