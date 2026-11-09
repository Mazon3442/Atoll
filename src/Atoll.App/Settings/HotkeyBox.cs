using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Atoll.Shell;

namespace Atoll.Settings;

/// <summary>
/// Shows a shortcut; click it (or press Space) and press the new keys. Esc cancels, Backspace turns the shortcut off.
/// The app's own shortcuts pause while recording so pressing the current one is captured here.
/// </summary>
public sealed class HotkeyBox : Button
{
    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(
        nameof(Shortcut), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).ShowShortcut()));

    private bool _recording;

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(Button)); // the window's button look; implicit styles skip subclasses
        MinWidth = 200;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetHelpText(this, "Press Space, then the new keys. Escape cancels. Backspace turns the shortcut off.");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        ShowShortcut();
    }

    public string Shortcut
    {
        get => (string)GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    internal bool IsRecording => _recording;

    protected override void OnClick()
    {
        base.OnClick();
        if (_recording) return;
        _recording = true;
        HotkeyService.Current?.Suspend(true);
        Content = "Press the new keys…";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recording) { base.OnPreviewKeyDown(e); return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt combinations arrive as Key.System
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && key == Key.Tab) { Stop(); return; } // let focus move on
        e.Handled = true;
        if (modifiers == ModifierKeys.None && key == Key.Escape) { Stop(); return; }
        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete) { Commit(""); return; }
        if (HotkeyService.FromKeys(modifiers, key) is { } shortcut) { Commit(shortcut); return; }
        // Only modifiers so far (or a key that needs Ctrl, Alt or Win): show what is held.
        Content = modifiers == ModifierKeys.None ? "Press the new keys…" : Held(modifiers);
    }

    private static string Held(ModifierKeys modifiers) => string.Join(" + ",
        new[] { (ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win") }
            .Where(m => modifiers.HasFlag(m.Item1)).Select(m => m.Item2).Append("…"));

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Stop();
    }

    private void Commit(string shortcut)
    {
        Stop();
        Shortcut = shortcut;
        ShowShortcut(); // the view model may refuse the value and keep the old one
    }

    private void Stop()
    {
        if (!_recording) return;
        _recording = false;
        HotkeyService.Current?.Suspend(false);
        ShowShortcut();
    }

    private void ShowShortcut()
    {
        if (!_recording) Content = HotkeyService.Describe(Shortcut);
    }
}
