using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Atoll.Modules.Tide.Listen;
using Atoll.Settings;
using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Tide;

/// <summary>Tide: local scripts, a notch teleprompter, optional voice follow and a local meeting listener.</summary>
public sealed partial class TideViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);
    private readonly TideSettings _settings;
    private readonly Action _save;
    private readonly DispatcherTimer _scroll;
    private readonly DispatcherTimer _saveTimer;
    private readonly VoiceFollower _voice = new();
    private TideScript? _selectedScript;
    private int _spokenChar;

    public TideViewModel(TideSettings settings, Action save, Func<AppSettings?>? appSettings = null)
    {
        _settings = settings;
        _save = save;
        foreach (var script in settings.Scripts) Scripts.Add(script);
        if (Scripts.Count == 0)
        {
            Scripts.Add(new TideScript
            {
                Title = "Welcome to Tide",
                Body = "Paste or type your script here.\n\nPress Start and read along. The script scrolls under the camera, so your eyes stay on the call.",
            });
        }

        _selectedScript = Scripts.FirstOrDefault(s => s.Id == settings.SelectedScriptId) ?? Scripts[0];
        _speedIndex = NearestSpeedIndex(settings.Speed);
        _fontSize = Math.Clamp(settings.FontSize, 20, 72);
        _mirror = settings.Mirror;
        _followVoice = settings.FollowVoice;
        _hideFromCapture = settings.HideFromCapture;

        _scroll = new DispatcherTimer(DispatcherPriority.Render) { Interval = Tick };
        _scroll.Tick += (_, _) => Advance();
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
        _saveTimer.Tick += (_, _) => SaveNow();
        Title = _selectedScript.Title;
        Body = _selectedScript.Body;
        Listen = new ListenViewModel(settings, save, appSettings, OnListeningChanged);
    }

    public ObservableCollection<TideScript> Scripts { get; } = [];
    public IReadOnlyList<string> Sections { get; } = ["Scripts", "Prompter", "Listen"];

    /// <summary>Listen keeps the capture, transcript, notes and model state; the Tide sections stay scripts-first.</summary>
    public ListenViewModel Listen { get; }

    /// <summary>Scroll presets, in the order the segmented switch shows them.</summary>
    public IReadOnlyList<string> Speeds { get; } = ["Slow", "Normal", "Fast", "Sprint"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScripts), nameof(IsPrompter), nameof(IsListen), nameof(ShowsPrompterBar),
        nameof(PrimaryLabel), nameof(PrimaryIsPause))]
    private int _sectionIndex;

    public bool IsScripts => SectionIndex == 0;
    public bool IsPrompter => SectionIndex == 1;
    public bool IsListen => SectionIndex == 2;

    /// <summary>The selected preset doubles as the scroll multiplier; 1 is normal.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Speed))]
    private int _speedIndex = 1;

    public double Speed => SpeedIndex switch { 0 => 0.6, 2 => 1.6, 3 => 2.4, _ => 1 };

    private static int NearestSpeedIndex(double speed) => speed switch
    {
        >= 2.0 => 3,
        >= 1.3 => 2,
        >= 0.8 => 1,
        _ => 0,
    };

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private double _fontSize = 34;
    [ObservableProperty] private bool _mirror;
    [ObservableProperty] private bool _followVoice;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCaptureBanner))]
    private bool _hideFromCapture = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused), nameof(PrimaryLabel), nameof(PrimaryIsPause))]
    private bool _isRunning;
    [ObservableProperty] private double _offset;
    [ObservableProperty] private double _maxOffset;
    [ObservableProperty] private string _voiceStatus = "";

    public TideScript? SelectedScript
    {
        get => _selectedScript;
        set
        {
            if (ReferenceEquals(_selectedScript, value) || value is null) return;
            SaveNow();
            _selectedScript = value;
            OnPropertyChanged();
            Title = value.Title;
            Body = value.Body;
            _settings.SelectedScriptId = value.Id;
            SaveNow();
        }
    }

    public string FontLabel => $"{FontSize:0}";

    /// <summary>Scripts and Prompter share one bar: reading speed, voice follow and the main button.</summary>
    public bool ShowsPrompterBar => SectionIndex < 2;

    /// <summary>Started and then stopped part-way: the prompter shows its Paused pill.</summary>
    public bool IsPaused => !IsRunning && Offset > 0;

    /// <summary>Only while the island is not hidden from capture: a warning that has a one-click answer.</summary>
    public bool ShowCaptureBanner => !HideFromCapture;

    /// <summary>Start from Scripts; then Pause and Resume once the prompter is up.</summary>
    public string PrimaryLabel => IsScripts ? "Start" : IsRunning ? "Pause" : IsPaused ? "Resume" : "Start";

    public bool PrimaryIsPause => IsPrompter && IsRunning;

    partial void OnOffsetChanged(double oldValue, double newValue)
    {
        if ((oldValue > 0) != (newValue > 0))
        {
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PrimaryLabel));
        }
    }

    /// <summary>The Hide chip names the action: hide the island from capture, or bring it back.</summary>
    public string CaptureLabel => HideFromCapture ? "Show" : "Hide";

    public string CaptureHint => HideFromCapture
        ? "Hidden from screen shares. Click to show it again."
        : "Visible in screen shares. Click to hide it.";

    public double PrompterLineHeight => FontSize * 1.45;

    partial void OnTitleChanged(string value)
    {
        if (_selectedScript is not null) _selectedScript.Title = value;
        QueueSave();
    }

    partial void OnBodyChanged(string value)
    {
        if (_selectedScript is not null) _selectedScript.Body = value;
        QueueSave();
    }

    partial void OnSpeedIndexChanged(int value)
    {
        _settings.Speed = Speed;
        _save();
    }

    partial void OnFontSizeChanged(double value)
    {
        _settings.FontSize = value;
        OnPropertyChanged(nameof(FontLabel));
        OnPropertyChanged(nameof(PrompterLineHeight));
        _save();
    }

    partial void OnMirrorChanged(bool value)
    {
        _settings.Mirror = value;
        _save();
    }

    partial void OnHideFromCaptureChanged(bool value)
    {
        _settings.HideFromCapture = value;
        OnPropertyChanged(nameof(CaptureLabel));
        OnPropertyChanged(nameof(CaptureHint));
        ApplyCapturePrivacy();
        _save();
    }

    partial void OnFollowVoiceChanged(bool value)
    {
        _settings.FollowVoice = value;
        if (IsRunning)
        {
            if (value) StartVoice();
            else StopVoice();
        }
        else if (!value) StopVoice();
        _save();
    }

    [RelayCommand]
    private void NewScript()
    {
        var script = new TideScript { Title = "New script", Body = "" };
        Scripts.Add(script);
        SelectedScript = script;
    }

    /// <summary>Deletes the script on a row, or the selected one when no row is given.</summary>
    [RelayCommand]
    private void DeleteScript(TideScript? script = null)
    {
        script ??= _selectedScript;
        if (script is null || !Scripts.Contains(script)) return;
        var wasSelected = ReferenceEquals(script, _selectedScript);
        var index = Scripts.IndexOf(script);
        Scripts.Remove(script);
        if (Scripts.Count == 0) Scripts.Add(new TideScript { Title = "New script", Body = "" });
        if (!wasSelected)
        {
            SaveNow();
            return;
        }
        _selectedScript = null;
        SelectedScript = Scripts[Math.Clamp(index, 0, Scripts.Count - 1)];
    }

    /// <summary>The main button: Start opens the prompter and runs it; in the prompter it pauses and resumes.</summary>
    [RelayCommand]
    private void Primary()
    {
        if (IsScripts)
        {
            if (Body.Length == 0) return;
            SectionIndex = 1;
            Start();
        }
        else if (IsRunning) Pause();
        else Start();
    }

    /// <summary>Tap the script to pause or resume, like the buttons do.</summary>
    [RelayCommand]
    private void TogglePause()
    {
        if (IsRunning) Pause();
        else if (Body.Length > 0) Start();
    }

    /// <summary>Ends the run, rewinds, and goes back to the script.</summary>
    [RelayCommand]
    private void StopPrompter()
    {
        Reset();
        SectionIndex = 0;
    }

    [RelayCommand]
    private void HideNow() => HideFromCapture = true;

    [RelayCommand]
    private void LargerText() => FontSize = Math.Min(72, FontSize + 2);

    [RelayCommand]
    private void SmallerText() => FontSize = Math.Max(20, FontSize - 2);

    [RelayCommand]
    private void Start()
    {
        if (Body.Length == 0) return;
        SaveNow();
        if (Offset >= MaxOffset && MaxOffset > 0) Offset = 0;
        IsRunning = true;
        _scroll.Start();
        ApplyCapturePrivacy();
        if (FollowVoice) StartVoice();
    }

    [RelayCommand]
    private void Pause()
    {
        IsRunning = false;
        _scroll.Stop();
        ApplyCapturePrivacy();
        StopVoice();
    }

    [RelayCommand]
    private void Reset()
    {
        Pause();
        Offset = 0;
        _spokenChar = 0;
    }

    public void ScrollBy(double pixels) => Offset = Math.Clamp(Offset + pixels, 0, MaxOffset);

    private void Advance()
    {
        if (!IsRunning) return;
        Offset = Math.Min(MaxOffset, Offset + Speed * 2);
        if (Offset >= MaxOffset && MaxOffset > 0) Pause();
    }

    private void StartVoice()
    {
        // The microphone has one owner: Listen's capture wins, and voice follow steps aside instead of failing.
        if (Listen.IsListening)
        {
            _settings.FollowVoice = false;
            FollowVoice = false;
            VoiceStatus = "Listen is using the microphone. Voice follow is off.";
            return;
        }
        if (!_voice.Start())
        {
            _settings.FollowVoice = false;
            FollowVoice = false;
            VoiceStatus = _voice.Error;
            return;
        }
        _voice.Recognized -= OnRecognized;
        _voice.Recognized += OnRecognized;
        VoiceStatus = "Following your voice.";
    }

    private void StopVoice()
    {
        _voice.Recognized -= OnRecognized;
        _voice.Stop();
        VoiceStatus = "";
    }

    private void OnRecognized(string text)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => FollowSpeech(text));
            return;
        }
        FollowSpeech(text);
    }

    private void FollowSpeech(string text)
    {
        if (!FollowVoice || !IsRunning || Body.Length == 0 || MaxOffset <= 0) return;
        var phrase = new string(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
        if (phrase.Length < 4) return;
        var haystack = Body.ToLowerInvariant();
        var start = Math.Min(_spokenChar, haystack.Length);
        var index = haystack.IndexOf(phrase, start, StringComparison.Ordinal);
        if (index < 0) return;
        _spokenChar = index + phrase.Length;
        Offset = MaxOffset * (_spokenChar / (double)Body.Length);
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// One flag drives the island's capture hiding: the prompter or Listen is running and the Hide chip is on.
    /// Listen follows the same switch so the transcript is not shown to a screen share by default.
    /// </summary>
    private void ApplyCapturePrivacy() => CapturePrivacy.Set(HideFromCapture && (IsRunning || Listen.IsListening));

    /// <summary>Listen owns the microphone while it runs, so voice follow backs off and says why.</summary>
    private void OnListeningChanged(bool listening)
    {
        if (listening)
        {
            if (FollowVoice)
            {
                _settings.FollowVoice = false;
                FollowVoice = false;
            }
            VoiceStatus = "Listen is using the microphone. Voice follow is off.";
        }
        else if (!IsRunning)
        {
            VoiceStatus = "";
        }
        ApplyCapturePrivacy();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        _settings.Scripts = Scripts.ToList();
        _settings.SelectedScriptId = _selectedScript?.Id ?? "";
        _save();
    }

    public void Dispose()
    {
        _scroll.Stop();
        _saveTimer.Stop();
        SaveNow();
        StopVoice();
        Listen.Dispose();
        CapturePrivacy.Set(false);
        _voice.Dispose();
    }
}

/// <summary>Tide tab: scripts and the teleprompter.</summary>
public sealed class TideModule : IIslandModule
{
    private TideViewModel? _vm;
    private ModuleContext? _context;

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _vm = new TideViewModel(context.Settings.Current.Tide, () => _context.Settings.NotifyChanged(),
            () => _context.Settings.Current);
    }

    public void Dispose() => _vm?.Dispose();
}
