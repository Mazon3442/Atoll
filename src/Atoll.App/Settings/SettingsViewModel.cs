using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Atoll.Modules;
using Atoll.Modules.Claude;
using Atoll.Modules.Notifications;
using Atoll.Shell;
using Atoll.Theme;

namespace Atoll.Settings;

public sealed record Choice<T>(T Value, string Label);

public sealed record SettingsPage(string Title, string Glyph);

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
        RebuildThemes();
    }

    private AppSettings S => _service.Current;
    private ThemeSettings T => S.Theme;

    // ---------- navigation ----------

    public IReadOnlyList<SettingsPage> Pages { get; } =
    [
        new("General", ""),
        new("Appearance", ""),
        new("Modules", ""),
        new("Notifications", ""),
        new("Claude Code", ""),
        new("About", ""),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    private int _pageIndex;

    public string PageTitle => Pages[Math.Clamp(PageIndex, 0, Pages.Count - 1)].Title;

    // ---------- General ----------

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set => Set(S.StartWithWindows, value, v =>
        {
            S.StartWithWindows = v;
            StartupRegistration.Apply(v);
        });
    }

    public bool ClickThrough { get => S.ClickThrough; set => Set(S.ClickThrough, value, v => S.ClickThrough = v); }

    public IReadOnlyList<Choice<FullscreenMode>> FullscreenModes { get; } =
    [
        new(FullscreenMode.PopUp, "Pop up for alerts, then hide"),
        new(FullscreenMode.Stay, "Stay visible"),
        new(FullscreenMode.Dim, "Stay visible, 50% opacity"),
        new(FullscreenMode.Hide, "Hide completely"),
    ];

    public FullscreenMode FullscreenMode { get => S.FullscreenMode; set => Set(S.FullscreenMode, value, v => S.FullscreenMode = v); }
    public int HoverDelayMs { get => S.HoverDelayMs; set => Set(S.HoverDelayMs, value, v => S.HoverDelayMs = v); }
    public double TopOffset { get => S.TopOffset; set => Set(S.TopOffset, value, v => S.TopOffset = v); }

    public ObservableCollection<DisplayRow> Displays { get; }

    public IReadOnlyList<Choice<MonitorMode>> MonitorModes { get; } =
    [
        new(MonitorMode.Primary, "Main display"),
        new(MonitorMode.All, "Every display"),
        new(MonitorMode.Selected, "Displays I choose"),
    ];

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

    // ---------- Appearance ----------

    public ObservableCollection<ThemeCard> Themes { get; } = [];

    [ObservableProperty] private string _newThemeName = "";
    [ObservableProperty] private string _themeMessage = "";

    public bool Glass { get => T.Glass; set => Set(T.Glass, value, v => T.Glass = v); }
    public string Background { get => T.Background; set => SetColor(T.Background, value, v => T.Background = v); }
    public string Foreground { get => T.Foreground; set => SetColor(T.Foreground, value, v => T.Foreground = v); }
    public string Accent { get => T.Accent; set => SetColor(T.Accent, value, v => T.Accent = v); }
    public double Opacity { get => T.Opacity; set => Set(T.Opacity, value, v => T.Opacity = v); }
    public double CompactHeight { get => T.CompactHeight; set => Set(T.CompactHeight, value, v => T.CompactHeight = v); }
    public double Roundness { get => T.Roundness; set => Set(T.Roundness, value, v => T.Roundness = v); }
    public double ExpandedWidth { get => T.ExpandedWidth; set => Set(T.ExpandedWidth, value, v => T.ExpandedWidth = v); }
    public double ExpandedHeight { get => T.ExpandedHeight; set => Set(T.ExpandedHeight, value, v => T.ExpandedHeight = v); }

    // Live preview of the island with the current theme.
    public Brush PreviewFill
    {
        get
        {
            var bg = ThemeService.TryParseColor(T.Background, out var c) ? c : Colors.Black;
            var alpha = Math.Clamp(T.Opacity, 0.3, 1) * bg.A / 255.0;
            // Glass shows the backdrop through a tint; a solid theme shows its color at the chosen opacity.
            return Freeze(new SolidColorBrush(bg) { Opacity = T.Glass ? Math.Max(alpha, 0.25) : alpha });
        }
    }

    public Brush PreviewText => ColorBrush(T.Foreground, Colors.White);
    public Brush PreviewSubtle => Freeze(new SolidColorBrush(((SolidColorBrush)PreviewText).Color) { Opacity = 0.6 });
    public Brush PreviewAccent => ColorBrush(T.Accent, Colors.DodgerBlue);
    public double PreviewHeight => Math.Clamp(T.CompactHeight, 24, 60);
    public CornerRadius PreviewCorner => new(Math.Clamp(T.Roundness, 0, 1) * PreviewHeight / 2);
    public CornerRadius PreviewPeekCorner => new(Math.Clamp(T.Roundness, 0, 1) * Math.Min(PreviewHeight * 0.85, 28));
    public double PreviewBlur => T.Glass ? 18 : 0;

    [RelayCommand]
    private void ApplyTheme(ThemeCard card)
    {
        var p = card.Preset;
        T.Background = p.Background;
        T.Foreground = p.Foreground;
        T.Accent = p.Accent;
        T.Glass = p.Glass;
        foreach (var name in new[] { nameof(Background), nameof(Foreground), nameof(Accent), nameof(Glass) }) OnPropertyChanged(name);
        ThemeMessage = "";
        Save();
    }

    [RelayCommand]
    private void SaveTheme()
    {
        var name = NewThemeName.Trim();
        if (name.Length == 0)
        {
            ThemeMessage = "Give the theme a name first.";
            return;
        }
        if (ThemeService.Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ThemeMessage = $"\"{name}\" is a built-in theme. Pick another name.";
            return;
        }
        var existing = S.CustomThemes.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var theme = new ThemePreset(name, T.Background, T.Foreground, T.Accent, T.Glass);
        if (existing >= 0) S.CustomThemes[existing] = theme;
        else S.CustomThemes.Add(theme);
        ThemeMessage = existing >= 0 ? $"Updated \"{name}\"." : $"Saved \"{name}\".";
        NewThemeName = "";
        Save();
    }

    [RelayCommand]
    private void DeleteTheme(ThemeCard card)
    {
        S.CustomThemes.RemoveAll(p => p.Name == card.Preset.Name);
        ThemeMessage = $"Deleted \"{card.Preset.Name}\".";
        Save();
    }

    private void RebuildThemes()
    {
        Themes.Clear();
        foreach (var p in ThemeService.Presets) Themes.Add(new ThemeCard(p, isCustom: false));
        foreach (var p in S.CustomThemes) Themes.Add(new ThemeCard(p, isCustom: true));
        RefreshThemeSelection();
    }

    private void RefreshThemeSelection()
    {
        // The first match wins so a saved copy of a built-in theme does not light up twice.
        var match = Themes.FirstOrDefault(c => Same(c.Preset.Background, T.Background) && Same(c.Preset.Foreground, T.Foreground)
                                               && Same(c.Preset.Accent, T.Accent) && c.Preset.Glass == T.Glass);
        foreach (var c in Themes) c.IsSelected = ReferenceEquals(c, match);
    }

    private void RefreshAppearance()
    {
        foreach (var name in new[] { nameof(PreviewFill), nameof(PreviewText), nameof(PreviewSubtle), nameof(PreviewAccent),
                                     nameof(PreviewHeight), nameof(PreviewCorner), nameof(PreviewPeekCorner), nameof(PreviewBlur) })
            OnPropertyChanged(name);
        if (!Themes.Where(c => c.IsCustom).Select(c => c.Preset).SequenceEqual(S.CustomThemes))
            RebuildThemes();
        else
            RefreshThemeSelection();
    }

    // ---------- Modules ----------

    public ObservableCollection<ModuleRow> Modules { get; }

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

    // ---------- Notifications ----------

    public string NotificationStatus => NotificationAccessInfo.Describe(NotificationAccessInfo.Get());
    public bool CanRequestNotifications => NotificationAccessInfo.Get() == NotificationAccess.NeedsPermission;
    public bool NotificationsAllowed => NotificationAccessInfo.Get() == NotificationAccess.Allowed;

    public bool ShowNotificationContent
    {
        get => S.Notifications.ShowContent;
        set => Set(S.Notifications.ShowContent, value, v => S.Notifications.ShowContent = v);
    }

    public ObservableCollection<string> MutedApps => new(S.Notifications.MutedApps);
    public bool HasMutedApps => S.Notifications.MutedApps.Count > 0;

    [RelayCommand]
    private async Task AllowNotifications()
    {
        await NotificationAccessInfo.RequestAsync();
        OnPropertyChanged(nameof(NotificationStatus));
        OnPropertyChanged(nameof(CanRequestNotifications));
        OnPropertyChanged(nameof(NotificationsAllowed));
    }

    [RelayCommand]
    private void OpenNotificationSettings() => NotificationAccessInfo.OpenWindowsSettings();

    [RelayCommand]
    private void Unmute(string app)
    {
        S.Notifications.MutedApps.RemoveAll(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(MutedApps));
        OnPropertyChanged(nameof(HasMutedApps));
        Save();
    }

    // ---------- Claude Code ----------

    public bool SoundOnDone { get => S.Claude.SoundOnDone; set => Set(S.Claude.SoundOnDone, value, v => S.Claude.SoundOnDone = v); }
    public bool SoundOnAttention { get => S.Claude.SoundOnAttention; set => Set(S.Claude.SoundOnAttention, value, v => S.Claude.SoundOnAttention = v); }

    public string HookSettingsPath => ClaudeHookInstaller.SettingsPath;
    public bool HooksInstalled => ClaudeHookInstaller.GetStatus() == ClaudeHookInstaller.Status.Installed;

    public string HookStatus => ClaudeHookInstaller.GetStatus() switch
    {
        ClaudeHookInstaller.Status.Installed => "Installed. New Claude Code sessions show up in ATOLL.",
        ClaudeHookInstaller.Status.InstalledElsewhere => "Installed for another copy of ATOLL. Install again to point them here.",
        ClaudeHookInstaller.Status.Unreadable => "Claude Code's settings file could not be read.",
        _ => "Not installed. Usage still shows; live activity needs the hooks.",
    };

    [ObservableProperty] private string _hookMessage = "";

    [RelayCommand]
    private void InstallHooks()
    {
        HookMessage = ClaudeHookInstaller.Install()
            ?? "Done. A backup of your Claude Code settings was saved next to them. Restart running sessions to pick up the hooks.";
        OnPropertyChanged(nameof(HookStatus));
        OnPropertyChanged(nameof(HooksInstalled));
    }

    [RelayCommand]
    private void RemoveHooks()
    {
        HookMessage = ClaudeHookInstaller.Uninstall() ?? "Removed ATOLL's hooks. Your other hooks were left as they were.";
        OnPropertyChanged(nameof(HookStatus));
        OnPropertyChanged(nameof(HooksInstalled));
    }

    // ---------- About ----------

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
    public string DataFolder => Path.GetDirectoryName(SettingsStore.DefaultPath)!;

    [RelayCommand]
    private void OpenDataFolder() => Process.Start(new ProcessStartInfo(DataFolder) { UseShellExecute = true });

    // ---------- helpers ----------

    private void SetColor(string current, string value, Action<string> assign)
    {
        // Only commit parseable colors so a half-typed hex does not flash the island.
        if (!ThemeService.TryParseColor(value, out _)) return;
        Set(current, value, assign);
    }

    private void Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(name);
        Save();
    }

    private void Save()
    {
        RefreshAppearance();
        _service.NotifyChanged();
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Brush ColorBrush(string text, Color fallback) =>
        Freeze(new SolidColorBrush(ThemeService.TryParseColor(text, out var c) ? c : fallback));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

public sealed partial class ThemeCard(ThemePreset preset, bool isCustom) : ObservableObject
{
    public ThemePreset Preset { get; } = preset;
    public bool IsCustom { get; } = isCustom;

    [ObservableProperty] private bool _isSelected;
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
