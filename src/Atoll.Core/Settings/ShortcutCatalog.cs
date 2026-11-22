namespace Atoll.Settings;

/// <summary>One rebindable global shortcut: a stable id, how Settings names it, and its default keys.</summary>
public sealed record ShortcutDef(string Id, string Name, string Description, string Default);

/// <summary>
/// Every global shortcut beyond the three that have their own settings (open, hide, palette).
/// <see cref="AppSettings.Shortcuts"/> only stores what the user changed, so a shortcut added later gets its
/// default on existing installs. An empty saved value means the user turned that shortcut off.
/// </summary>
public static class ShortcutCatalog
{
    public const string Dictate = "dictate";
    public const string ScreenshotArea = "screenshotArea";
    public const string ScreenshotScreen = "screenshotScreen";
    public const string Ocr = "ocr";
    public const string Qr = "qr";
    public const string ColorPicker = "colorPicker";
    public const string Record = "record";
    public const string PlainPaste = "plainPaste";
    public const string MicMute = "micMute";
    public const string Text = "text";
    public const string Controls = "controls";

    public static IReadOnlyList<ShortcutDef> All { get; } =
    [
        new(Dictate, "Voice to text", "Press, speak, press again. The text is copied and typed into the window you were in.", "Ctrl+Alt+D"),
        new(ScreenshotArea, "Screenshot of an area", "Drag a rectangle; the picture goes to the clipboard and a folder.", "Ctrl+Alt+S"),
        new(ScreenshotScreen, "Screenshot of the screen", "The whole screen under the cursor.", "Ctrl+Alt+Shift+S"),
        new(Ocr, "Text from the screen", "Drag over text; it is recognised and copied.", "Ctrl+Alt+O"),
        new(Qr, "QR code from the screen", "Drag over a QR code; its contents are copied.", "Ctrl+Alt+Q"),
        new(ColorPicker, "Color picker", "Pick any pixel on screen; the hex value is copied.", "Ctrl+Alt+C"),
        new(Record, "Screen recording", "Start and stop recording an area or the screen.", "Ctrl+Alt+R"),
        new(PlainPaste, "Paste without formatting", "Pastes the clipboard as plain text.", "Ctrl+Alt+Shift+V"),
        new(MicMute, "Mute or unmute the microphone", "Toggles the default microphone.", "Ctrl+Alt+M"),
        new(Text, "Text tools", "Opens the Text tab.", ""),
        new(Controls, "Controls", "Opens the quick controls from the header.", ""),
    ];

    public static ShortcutDef? Find(string id) => All.FirstOrDefault(def => def.Id == id);

    /// <summary>The keys for an id: the user's choice (empty = off) or the default.</summary>
    public static string Resolve(AppSettings settings, string id) =>
        settings.Shortcuts.TryGetValue(id, out var saved) ? saved.Trim() : Find(id)?.Default ?? "";
}
