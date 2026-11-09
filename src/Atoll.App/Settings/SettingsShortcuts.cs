using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Settings;

public sealed partial class SettingsViewModel
{
    [ObservableProperty] private string _shortcutMessage = "";

    public string OpenShortcut { get => S.OpenShortcut; set => SetShortcut(value, [S.HideShortcut, S.PaletteShortcut], v => S.OpenShortcut = v, S.OpenShortcut); }
    public string HideShortcut { get => S.HideShortcut; set => SetShortcut(value, [S.OpenShortcut, S.PaletteShortcut], v => S.HideShortcut = v, S.HideShortcut); }
    public string PaletteShortcut { get => S.PaletteShortcut; set => SetShortcut(value, [S.OpenShortcut, S.HideShortcut], v => S.PaletteShortcut = v, S.PaletteShortcut); }

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

    private void HookShortcuts()
    {
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
    }
}
