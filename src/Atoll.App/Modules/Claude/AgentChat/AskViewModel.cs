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
    private readonly Action _save;
    private readonly bool _ownsChat;
    private IAgentChat? _chatService;
    private CancellationTokenSource? _run;

    /// <param name="save">Persists the settings after a model or tool change.</param>
    public AskViewModel(IAgentChat? chat = null, Func<AgentChatOptions>? options = null, Action<Action>? post = null,
        Action? save = null)
    {
        _ownsChat = chat is null;
        _chatService = chat;
        _options = options ?? (() => new AgentChatOptions(new Settings.AgentChatSettings(), []));
        _post = post ?? (action => action());
        _save = save ?? (() => { });
        _sourceValue = _options().Settings.Preferred;
    }

    /// <summary>The service is created on first use, so an island that never asks starts no HTTP client.</summary>
    private IAgentChat Chat => _chatService ??= new AgentChatService();

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

    /// <summary>Which agent answers, once one has been resolved. Claude is first, so it is the assumed default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Models), nameof(ModelValue), nameof(ModelLabel), nameof(ModelTitle), nameof(ModelKind))]
    private AgentChatKind _kind = AgentChatKind.ClaudeCode;

    /// <summary>The source chosen in the toolbar; empty tries each agent in turn.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Models), nameof(ModelValue), nameof(ModelLabel), nameof(ModelTitle), nameof(ModelKind), nameof(SourceLabel))]
    private string _sourceValue = "";

    [ObservableProperty] private bool _isModelMenuOpen;
    [ObservableProperty] private bool _isSourceMenuOpen;

    /// <summary>Auto, the three CLIs, then every saved API key by name.</summary>
    public IReadOnlyList<ModelOption> Sources
    {
        get
        {
            var list = new List<ModelOption> { new("Auto · first available", "") };
            list.Add(new ModelOption("Claude Code", "claude"));
            list.Add(new ModelOption("Codex", "codex"));
            list.Add(new ModelOption("OpenCode", "opencode"));
            foreach (var account in _options().Accounts)
                list.Add(new ModelOption(account.Name, $"api:{account.Id}"));
            return list;
        }
    }

    public string SourceLabel => SourceValue.Length == 0
        ? "Ask · Auto"
        : $"Ask · {Sources.FirstOrDefault(s => s.Value == SourceValue)?.Label ?? "Auto"}";

    /// <summary>A chosen source decides the model list; on Auto it follows whatever answered last.</summary>
    public AgentChatKind ModelKind => SourceValue switch
    {
        "claude" => AgentChatKind.ClaudeCode,
        "codex" => AgentChatKind.Codex,
        "opencode" => AgentChatKind.OpenCode,
        var value when value.StartsWith("api:", StringComparison.Ordinal) => AgentChatKind.Api,
        _ => Kind,
    };

    /// <summary>Claude Code takes the short aliases; the other agents keep whatever Settings holds.</summary>
    public IReadOnlyList<ModelOption> Models
    {
        get
        {
            var models = ModelKind switch
            {
                AgentChatKind.ClaudeCode => ClaudeModels,
                AgentChatKind.Api => ApiModels(),
                _ => OtherModels,
            };
            if (models.Count > 0 && models[0].Value == ModelValue) return models;
            return ModelValue.Length == 0
                ? models
                : [models[0], new($"Current · {ModelValue}", ModelValue), .. models.Skip(1)];
        }
    }

    private static readonly ModelOption[] ClaudeModels =
    [
        new("Auto · agent default", ""),
        new("Opus", "opus"),
        new("Sonnet", "sonnet"),
        new("Haiku", "haiku"),
    ];

    private static readonly ModelOption[] OtherModels = [new("Auto · agent default", "")];

    /// <summary>Model ids worth offering for a saved key, by host; anything else stays in Settings.</summary>
    private IReadOnlyList<ModelOption> ApiModels()
    {
        var accounts = _options().Accounts;
        var chosen = SourceValue.StartsWith("api:", StringComparison.Ordinal) ? SourceValue[4..] : "";
        var host = (accounts.FirstOrDefault(a => a.Id == chosen) ?? accounts.FirstOrDefault())?.BaseUrl ?? "";
        var auto = new ModelOption("Auto · provider default", "");
        if (host.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            return [auto, new("DeepSeek Chat", "deepseek-chat"), new("DeepSeek Reasoner", "deepseek-reasoner")];
        if (host.Contains("anthropic", StringComparison.OrdinalIgnoreCase))
            return [auto, new("Sonnet", "claude-sonnet-4-5"), new("Opus", "claude-opus-4-1")];
        if (host.Contains("openai", StringComparison.OrdinalIgnoreCase))
            return [auto, new("GPT-5", "gpt-5"), new("GPT-4.1", "gpt-4.1")];
        return [auto];
    }

    /// <summary>The model field for the agent that answers, so each agent keeps its own choice.</summary>
    public string ModelValue
    {
        get => ModelKind switch
        {
            AgentChatKind.ClaudeCode => _options().Settings.ClaudeModel,
            AgentChatKind.Codex => _options().Settings.CodexModel,
            AgentChatKind.OpenCode => _options().Settings.OpenCodeModel,
            _ => _options().Settings.ApiModel,
        };
        set
        {
            switch (ModelKind)
            {
                case AgentChatKind.ClaudeCode: _options().Settings.ClaudeModel = value; break;
                case AgentChatKind.Codex: _options().Settings.CodexModel = value; break;
                case AgentChatKind.OpenCode: _options().Settings.OpenCodeModel = value; break;
                default: _options().Settings.ApiModel = value; break;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModelLabel));
            OnPropertyChanged(nameof(Models));
            _save();
        }
    }

    public string ModelLabel => ModelValue.Length == 0 ? "Model · Auto" : $"Model · {ModelValue}";

    public string ModelTitle => ModelKind switch
    {
        AgentChatKind.ClaudeCode => "Which Claude model answers",
        AgentChatKind.Codex => "Codex model. Blank uses its own default; type any id in Settings.",
        AgentChatKind.OpenCode => "OpenCode model. Blank uses its own default; type any id in Settings.",
        _ => "API model. Blank picks a default for the provider; type any id in Settings.",
    };

    /// <summary>Tools are off by default; the note under the box says what each agent can do with them.</summary>
    public bool AllowTools
    {
        get => _options().Settings.AllowTools;
        set
        {
            if (_options().Settings.AllowTools == value) return;
            _options().Settings.AllowTools = value;
            OnPropertyChanged();
            _save();
        }
    }

    [RelayCommand]
    private void ChooseModel(ModelOption? option)
    {
        if (option is not null) ModelValue = option.Value;
        IsModelMenuOpen = false;
    }

    [RelayCommand]
    private void ToggleModelMenu() => IsModelMenuOpen = !IsModelMenuOpen;

    [RelayCommand]
    private void ChooseSource(ModelOption? option)
    {
        if (option is not null) SourceValue = option.Value;
        IsSourceMenuOpen = false;
    }

    [RelayCommand]
    private void ToggleSourceMenu() => IsSourceMenuOpen = !IsSourceMenuOpen;

    partial void OnSourceValueChanged(string value)
    {
        _options().Settings.Preferred = value;
        _save();
    }

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
                Kind = selection.Provider.Kind;
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
        if (_ownsChat) (_chatService as IDisposable)?.Dispose();
    }
}

/// <summary>One row in the model menu.</summary>
public sealed record ModelOption(string Label, string Value);
