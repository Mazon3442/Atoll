using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Island.Modules;
using Island.Modules.Claude;
using Island.Shell;
using Island.Theme;

namespace Island.Settings;

/// <summary>Edits <see cref="SettingsService.Current"/> in place; every change applies live and saves.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _service;

    public SettingsViewModel(SettingsService service, IReadOnlyList<ModuleDescriptor> catalog)
    {
        _service = service;
        Modules = new(S.Modules
            .Select(e => (Entry: e, Descriptor: catalog.FirstOrDefault(d => d.Id == e.Id)))
            .Where(x => x.Descriptor is not null)
            .Select(x => new ModuleRow(x.Entry, x.Descriptor!, Save)));
        Displays = new(Shell.Monitors.GetAll().Select(m => new DisplayRow(m, S, Save)));
    }

    private AppSettings S => _service.Current;
    private ThemeSettings T => S.Theme;

    public ObservableCollection<ModuleRow> Modules { get; }
    public ObservableCollection<DisplayRow> Displays { get; }
    public IReadOnlyList<MonitorMode> MonitorModes { get; } = Enum.GetValues<MonitorMode>();
    public IReadOnlyList<ThemePreset> Presets => ThemeService.Presets;

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set => Set(S.StartWithWindows, value, v =>
        {
            S.StartWithWindows = v;
            StartupRegistration.Apply(v);
        });
    }

    public bool HideInFullscreen { get => S.HideInFullscreen; set => Set(S.HideInFullscreen, value, v => S.HideInFullscreen = v); }
    public int HoverDelayMs { get => S.HoverDelayMs; set => Set(S.HoverDelayMs, value, v => S.HoverDelayMs = v); }
    public double TopOffset { get => S.TopOffset; set => Set(S.TopOffset, value, v => S.TopOffset = v); }

    public MonitorMode MonitorMode
    {
        get => S.MonitorMode;
        set
        {
            Set(S.MonitorMode, value, v => S.MonitorMode = v);
            OnPropertyChanged(nameof(IsSelectedMode));
        }
    }

    public bool IsSelectedMode => S.MonitorMode == MonitorMode.Selected;

    public ThemePreset? SelectedPreset
    {
        get => Presets.FirstOrDefault(p => Same(p.Background, T.Background) && Same(p.Foreground, T.Foreground) && Same(p.Accent, T.Accent));
        set
        {
            if (value is null) return;
            T.Background = value.Background;
            T.Foreground = value.Foreground;
            T.Accent = value.Accent;
            OnPropertyChanged(nameof(Background));
            OnPropertyChanged(nameof(Foreground));
            OnPropertyChanged(nameof(Accent));
            OnPropertyChanged();
            Save();
        }
    }

    public string Background { get => T.Background; set => SetColor(T.Background, value, v => T.Background = v); }
    public string Foreground { get => T.Foreground; set => SetColor(T.Foreground, value, v => T.Foreground = v); }
    public string Accent { get => T.Accent; set => SetColor(T.Accent, value, v => T.Accent = v); }

    public double Opacity { get => T.Opacity; set => Set(T.Opacity, value, v => T.Opacity = v); }
    public double CompactHeight { get => T.CompactHeight; set => Set(T.CompactHeight, value, v => T.CompactHeight = v); }
    public double Roundness { get => T.Roundness; set => Set(T.Roundness, value, v => T.Roundness = v); }
    public double ExpandedWidth { get => T.ExpandedWidth; set => Set(T.ExpandedWidth, value, v => T.ExpandedWidth = v); }
    public double ExpandedHeight { get => T.ExpandedHeight; set => Set(T.ExpandedHeight, value, v => T.ExpandedHeight = v); }

    // ---------- Claude Code ----------

    public bool SoundOnDone { get => S.Claude.SoundOnDone; set => Set(S.Claude.SoundOnDone, value, v => S.Claude.SoundOnDone = v); }
    public bool SoundOnAttention { get => S.Claude.SoundOnAttention; set => Set(S.Claude.SoundOnAttention, value, v => S.Claude.SoundOnAttention = v); }

    public string HookSettingsPath => ClaudeHookInstaller.SettingsPath;

    public string HookStatus => ClaudeHookInstaller.GetStatus() switch
    {
        ClaudeHookInstaller.Status.Installed => "Hooks installed. New Claude Code sessions report to the island.",
        ClaudeHookInstaller.Status.InstalledElsewhere => "Hooks point to another copy of Island. Install again to update them.",
        ClaudeHookInstaller.Status.Unreadable => "Claude Code settings could not be read.",
        _ => "Hooks not installed. Usage still shows; live activity needs the hooks.",
    };

    [ObservableProperty] private string _hookMessage = "";

    [RelayCommand]
    private void InstallHooks()
    {
        HookMessage = ClaudeHookInstaller.Install()
            ?? $"Added hooks to {HookSettingsPath} (backup saved next to it). Restart running Claude Code sessions to pick them up.";
        OnPropertyChanged(nameof(HookStatus));
    }

    [RelayCommand]
    private void RemoveHooks()
    {
        HookMessage = ClaudeHookInstaller.Uninstall() ?? "Removed the island's hooks. Your other hooks were left as they were.";
        OnPropertyChanged(nameof(HookStatus));
    }

    [RelayCommand]
    private void MoveUp(ModuleRow row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(ModuleRow row) => Move(row, +1);

    private void Move(ModuleRow row, int delta)
    {
        var from = Modules.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Modules.Count) return;
        Modules.Move(from, to);

        // Rebuild the settings list in the new order, keeping entries for modules this build does not know.
        var unknown = S.Modules.Where(e => Modules.All(m => m.Entry != e)).ToList();
        S.Modules.Clear();
        S.Modules.AddRange(Modules.Select(m => m.Entry));
        S.Modules.AddRange(unknown);
        Save();
    }

    private void SetColor(string current, string value, Action<string> assign)
    {
        // Only commit parseable colors so a half-typed hex does not flash the island.
        if (!ThemeService.TryParseColor(value, out _)) return;
        Set(current, value, assign);
        OnPropertyChanged(nameof(SelectedPreset));
    }

    private void Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(name);
        Save();
    }

    private void Save() => _service.NotifyChanged();

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed class ModuleRow(ModuleEntry entry, ModuleDescriptor descriptor, Action save) : ObservableObject
{
    public ModuleEntry Entry { get; } = entry;
    public string Name => descriptor.Name;
    public string Glyph => descriptor.Glyph;

    public bool Enabled
    {
        get => Entry.Enabled;
        set
        {
            if (Entry.Enabled == value) return;
            Entry.Enabled = value;
            OnPropertyChanged();
            save();
        }
    }
}

public sealed class DisplayRow(MonitorInfo monitor, AppSettings settings, Action save) : ObservableObject
{
    public string Label => monitor.Label;

    public bool IsSelected
    {
        get => settings.SelectedMonitors.Contains(monitor.DeviceName);
        set
        {
            if (IsSelected == value) return;
            if (value) settings.SelectedMonitors.Add(monitor.DeviceName);
            else settings.SelectedMonitors.Remove(monitor.DeviceName);
            OnPropertyChanged();
            save();
        }
    }
}
