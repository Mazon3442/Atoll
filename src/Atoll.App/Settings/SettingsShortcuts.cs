using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Settings;

public sealed partial class SettingsViewModel
{
    [ObservableProperty] private string _shortcutMessage = "";

    public string OpenShortcut { get => S.OpenShortcut; set => SetShortcut(value, [S.HideShortcut, S.PaletteShortcut, .. ExtraValues()], v => S.OpenShortcut = v, S.OpenShortcut); }
    public string HideShortcut { get => S.HideShortcut; set => SetShortcut(value, [S.OpenShortcut, S.PaletteShortcut, .. ExtraValues()], v => S.HideShortcut = v, S.HideShortcut); }
    public string PaletteShortcut { get => S.PaletteShortcut; set => SetShortcut(value, [S.OpenShortcut, S.HideShortcut, .. ExtraValues()], v => S.PaletteShortcut = v, S.PaletteShortcut); }

    public string OpenShortcutStatus => Status(S.OpenShortcut);
    public string HideShortcutStatus => Status(S.HideShortcut);
    public string PaletteShortcutStatus => Status(S.PaletteShortcut);

    private void SetShortcut(string value, IReadOnlyList<string> others, Action<string> assign, string current, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (value.Length > 0 && others.Any(other => string.Equals(value, other, StringComparison.OrdinalIgnoreCase)))
        {
            ShortcutMessage = $"{HotkeyService.Describe(value)} is already another shortcut.";
            OnPropertyChanged(name); // put the box back to the saved shortcut
            return;
        }
        ShortcutMessage = "";
        Set(current, value, assign, name);
        OnShortcutsChanged();
    }

    private static string Status(string shortcut) =>
        HotkeyService.Current?.IsUnavailable(shortcut) == true ? "Another app is using this shortcut. Choose a different one." : "";

    /// <summary>One row per catalog shortcut that has a working action. Built when Settings opens.</summary>
    public IReadOnlyList<ShortcutRow> ExtraShortcuts { get; private set; } = [];

    private IEnumerable<string> ExtraValues() => ExtraShortcuts.Select(row => row.Shortcut).Where(value => value.Length > 0);

    private void BuildExtraShortcuts()
    {
        var registered = HotkeyService.Current?.RegisteredKeys ?? [];
        ExtraShortcuts = ShortcutCatalog.All.Where(def => registered.Contains(def.Id)).Select(def => new ShortcutRow(this, def)).ToList();
    }

    internal string ExtraShortcut(ShortcutDef def) => ShortcutCatalog.Resolve(S, def.Id);

    internal bool TrySetExtraShortcut(ShortcutDef def, string value)
    {
        var others = new[] { S.OpenShortcut, S.HideShortcut, S.PaletteShortcut }
            .Concat(ExtraShortcuts.Where(row => row.Def.Id != def.Id).Select(row => row.Shortcut));
        if (value.Length > 0 && others.Any(other => string.Equals(value, other, StringComparison.OrdinalIgnoreCase)))
        {
            ShortcutMessage = $"{HotkeyService.Describe(value)} is already another shortcut.";
            return false;
        }
        ShortcutMessage = "";
        S.Shortcuts[def.Id] = value;
        Save();
        OnShortcutsChanged();
        return true;
    }

    private void HookShortcuts()
    {
        BuildExtraShortcuts();
        if (HotkeyService.Current is { } hotkeys) hotkeys.Changed += OnShortcutsChanged;
    }

    private void UnhookShortcuts()
    {
        if (HotkeyService.Current is { } hotkeys) hotkeys.Changed -= OnShortcutsChanged;
    }

    private void OnShortcutsChanged()
    {
        OnPropertyChanged(nameof(OpenShortcutStatus));
        OnPropertyChanged(nameof(HideShortcutStatus));
        OnPropertyChanged(nameof(PaletteShortcutStatus));
        foreach (var row in ExtraShortcuts) row.Refresh();
    }
}

/// <summary>A rebindable shortcut in Settings: its name, current keys and whether another app holds them.</summary>
public sealed class ShortcutRow(SettingsViewModel owner, ShortcutDef def) : ObservableObject
{
    public ShortcutDef Def { get; } = def;
    public string Name => Def.Name;
    public string Description => Def.Description;

    public string Shortcut
    {
        get => owner.ExtraShortcut(Def);
        set
        {
            if (!owner.TrySetExtraShortcut(Def, value)) OnPropertyChanged(); // put the box back to the saved shortcut
            else Refresh();
        }
    }

    public string Status => HotkeyService.Current?.IsUnavailable(Shortcut) == true
        ? "Another app is using this shortcut. Choose a different one." : "";

    public void Refresh()
    {
        OnPropertyChanged(nameof(Shortcut));
        OnPropertyChanged(nameof(Status));
    }
}
