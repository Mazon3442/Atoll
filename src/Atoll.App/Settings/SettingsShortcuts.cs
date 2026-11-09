using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Settings;

public sealed partial class SettingsViewModel
{
    [ObservableProperty] private string _shortcutMessage = "";

    public string OpenShortcut { get => S.OpenShortcut; set => SetShortcut(value, S.HideShortcut, v => S.OpenShortcut = v, S.OpenShortcut); }
    public string HideShortcut { get => S.HideShortcut; set => SetShortcut(value, S.OpenShortcut, v => S.HideShortcut = v, S.HideShortcut); }

    public string OpenShortcutStatus => Status(S.OpenShortcut);
    public string HideShortcutStatus => Status(S.HideShortcut);

    private void SetShortcut(string value, string other, Action<string> assign, string current, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (value.Length > 0 && string.Equals(value, other, StringComparison.OrdinalIgnoreCase))
        {
            ShortcutMessage = $"{HotkeyService.Describe(value)} is already the other shortcut.";
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
    }
}
