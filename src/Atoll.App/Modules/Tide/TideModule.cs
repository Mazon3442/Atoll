using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Atoll.Settings;
using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Tide;

/// <summary>Tide: local scripts, a notch teleprompter and optional voice follow.</summary>
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

    public TideViewModel(TideSettings settings, Action save)
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
        _speed = Math.Clamp(settings.Speed, 0.3, 3);
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
    }

    public ObservableCollection<TideScript> Scripts { get; } = [];
    public IReadOnlyList<string> Sections { get; } = ["Scripts", "Prompter"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScripts), nameof(IsPrompter))]
    private int _sectionIndex;

    public bool IsScripts => SectionIndex == 0;
    public bool IsPrompter => SectionIndex == 1;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private double _speed = 1;
    [ObservableProperty] private double _fontSize = 34;
    [ObservableProperty] private bool _mirror;
    [ObservableProperty] private bool _followVoice;
    [ObservableProperty] private bool _hideFromCapture = true;
    [ObservableProperty] private bool _isRunning;
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

    public string SpeedLabel => Speed switch
    {
        < 0.8 => "Slow",
        < 1.25 => "Normal",
        < 1.9 => "Fast",
        _ => "Sprint",
    };

    public string FontLabel => $"{FontSize:0}";

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

    partial void OnSpeedChanged(double value)
    {
        _settings.Speed = value;
        OnPropertyChanged(nameof(SpeedLabel));
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
        if (IsRunning) CapturePrivacy.Set(value);
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

    [RelayCommand]
    private void DeleteScript()
    {
        if (_selectedScript is null) return;
        var index = Scripts.IndexOf(_selectedScript);
        Scripts.Remove(_selectedScript);
        if (Scripts.Count == 0) Scripts.Add(new TideScript { Title = "New script", Body = "" });
        _selectedScript = null;
        SelectedScript = Scripts[Math.Clamp(index, 0, Scripts.Count - 1)];
    }

    [RelayCommand]
    private void Start()
    {
        if (Body.Length == 0) return;
        SaveNow();
        if (Offset >= MaxOffset && MaxOffset > 0) Offset = 0;
        IsRunning = true;
        _scroll.Start();
        if (HideFromCapture) CapturePrivacy.Set(true);
        if (FollowVoice) StartVoice();
    }

    [RelayCommand]
    private void Pause()
    {
        IsRunning = false;
        _scroll.Stop();
        CapturePrivacy.Set(false);
        StopVoice();
    }

    [RelayCommand]
    private void Reset()
    {
        Pause();
        Offset = 0;
        _spokenChar = 0;
    }

    [RelayCommand] private void Slow() => Speed = 0.6;
    [RelayCommand] private void Normal() => Speed = 1;
    [RelayCommand] private void Fast() => Speed = 1.6;
    [RelayCommand] private void Sprint() => Speed = 2.4;
    [RelayCommand] private void FontUp() => FontSize = Math.Min(72, FontSize + 4);
    [RelayCommand] private void FontDown() => FontSize = Math.Max(20, FontSize - 4);

    public void ScrollBy(double pixels) => Offset = Math.Clamp(Offset + pixels, 0, MaxOffset);

    private void Advance()
    {
        if (!IsRunning) return;
        Offset = Math.Min(MaxOffset, Offset + Speed * 2);
        if (Offset >= MaxOffset && MaxOffset > 0) Pause();
    }

    private void StartVoice()
    {
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
        _vm = new TideViewModel(context.Settings.Current.Tide, () => _context.Settings.NotifyChanged());
    }

    public void Dispose() => _vm?.Dispose();
}
