using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Atoll.Settings;

namespace Atoll.Shell;

/// <summary>
/// System-wide shortcuts from <see cref="AppSettings.OpenShortcut"/> and <see cref="AppSettings.HideShortcut"/>,
/// registered on a message-only window. UI thread only.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private const int OpenId = 1, HideId = 2, PaletteId = 3;

    private static readonly KeyGestureConverter Converter = new();

    private readonly SettingsService _settings;
    private readonly Action? _open;
    private readonly Action? _toggleHidden;
    private readonly Action? _openPalette;
    private readonly HwndSource _window;
    private readonly HashSet<string> _unavailable = new(StringComparer.OrdinalIgnoreCase);
    private string? _applied;
    private bool _suspended;

    /// <summary>Extra shortcuts added after construction: window-message id, resolver for the current keys, action.</summary>
    private readonly Dictionary<int, (string Key, Func<string> Shortcut, Action Run)> _extra = new();
    private int _nextExtraId = 100;

    public HotkeyService(SettingsService settings, Action? open, Action? toggleHidden, Action? openPalette)
    {
        _settings = settings;
        _open = open;
        _toggleHidden = toggleHidden;
        _openPalette = openPalette;
        _window = new HwndSource(new HwndSourceParameters("ATOLL shortcuts") { ParentWindow = -3, WindowStyle = 0 }); // -3 = HWND_MESSAGE
        _window.AddHook(WndProc);
        _settings.Changed += Apply;
        Apply();
        Current = this;
    }

    /// <summary>The running service, so Settings can show conflicts and pause shortcuts while one is being recorded.</summary>
    public static HotkeyService? Current { get; private set; }

    /// <summary>Raised after registration changes, so Settings can refresh its "in use" messages.</summary>
    public event Action? Changed;

    /// <summary>True when Windows refused the shortcut, usually because another app already owns it.</summary>
    public bool IsUnavailable(string shortcut) => _unavailable.Contains(shortcut.Trim());

    /// <summary>Releases the shortcuts while the user records a new one, so pressing the current one reaches Settings.</summary>
    public void Suspend(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        _applied = null;
        Apply();
    }

    /// <summary>
    /// Adds a catalog shortcut (see <see cref="ShortcutCatalog"/>). Its keys follow the user's setting, so the
    /// action stays registered while the shortcut is off and starts working as soon as one is chosen.
    /// </summary>
    public void AddAction(string catalogId, Action run) =>
        AddAction(catalogId, () => ShortcutCatalog.Resolve(_settings.Current, catalogId), run);

    /// <summary>Adds a shortcut whose keys come from <paramref name="shortcut"/>, for example a per-item launcher hotkey.</summary>
    public void AddAction(string key, Func<string> shortcut, Action run)
    {
        foreach (var id in _extra.Where(pair => pair.Value.Key == key).Select(pair => pair.Key).ToList())
        {
            UnregisterHotKey(_window.Handle, id); // a restarted module replaces its old shortcut
            _extra.Remove(id);
        }
        _extra[_nextExtraId++] = (key, shortcut, run);
        _applied = null;
        Apply();
    }

    /// <summary>Removes every shortcut added under <paramref name="keyPrefix"/>.</summary>
    public void RemoveActions(string keyPrefix)
    {
        foreach (var id in _extra.Where(pair => pair.Value.Key.StartsWith(keyPrefix, StringComparison.Ordinal)).Select(pair => pair.Key).ToList())
        {
            UnregisterHotKey(_window.Handle, id);
            _extra.Remove(id);
        }
        _applied = null;
        Apply();
    }

    /// <summary>Catalog ids that have an action, so Settings only lists shortcuts that do something.</summary>
    public IReadOnlyList<string> RegisteredKeys => _extra.Values.Select(value => value.Key).ToList();

    private void Apply()
    {
        var s = _settings.Current;
        var wanted = new List<(int Id, string Shortcut)>
        {
            (OpenId, s.OpenShortcut.Trim()), (HideId, s.HideShortcut.Trim()), (PaletteId, s.PaletteShortcut.Trim()),
        };
        wanted.AddRange(_extra.Select(pair => (pair.Key, pair.Value.Shortcut().Trim())));
        var signature = string.Join("|", wanted.Select(item => $"{item.Id}={item.Shortcut}"));
        if (_applied == signature) return;
        _applied = signature;
        foreach (var (id, _) in wanted) UnregisterHotKey(_window.Handle, id);
        _unavailable.Clear();
        if (!_suspended)
            foreach (var (id, shortcut) in wanted) Register(id, shortcut);
        Changed?.Invoke();
    }

    private void Register(int id, string shortcut)
    {
        if (!TryParse(shortcut, out var modifiers, out var key)) return;
        if (RegisterHotKey(_window.Handle, id, modifiers | MOD_NOREPEAT, key)) return;
        _unavailable.Add(shortcut);
        Log.Info($"Shortcut {shortcut} is unavailable (error {Marshal.GetLastWin32Error()}).");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return 0;
        handled = true;
        if (wParam == OpenId) _open?.Invoke();
        else if (wParam == HideId) _toggleHidden?.Invoke();
        else if (wParam == PaletteId) _openPalette?.Invoke();
        else if (_extra.TryGetValue((int)wParam, out var action)) action.Run();
        return 0;
    }

    /// <summary>Parses a saved shortcut such as "Ctrl+Alt+Space". Empty or invalid means none.</summary>
    internal static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = virtualKey = 0;
        if (Parse(text) is not { } gesture) return false;
        var m = gesture.Modifiers;
        modifiers = (m.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0) | (m.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0)
                    | (m.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0) | (m.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0);
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        return virtualKey != 0;
    }

    internal static KeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return Converter.ConvertFromInvariantString(text.Trim()) as KeyGesture; }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The saved form of a key press, or null when it cannot be a global shortcut. It needs Ctrl, Alt or Win
    /// (Shift alone would steal ordinary typing), except for the function keys.
    /// </summary>
    internal static string? FromKeys(ModifierKeys modifiers, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None) return null;
        var functionKey = key is >= Key.F1 and <= Key.F24;
        if (!functionKey && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) return null;
        try { return Converter.ConvertToInvariantString(new KeyGesture(key, modifiers)); }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>"Ctrl+Alt+Space" as people read it: "Ctrl + Alt + Space".</summary>
    internal static string Describe(string? shortcut) =>
        Parse(shortcut) is { } gesture
            ? string.Join(" + ", Converter.ConvertToInvariantString(gesture)!.Split('+').Select(part => part == "Windows" ? "Win" : part))
            : "Off";

    public void Dispose()
    {
        _settings.Changed -= Apply;
        UnregisterHotKey(_window.Handle, OpenId);
        UnregisterHotKey(_window.Handle, HideId);
        UnregisterHotKey(_window.Handle, PaletteId);
        foreach (var id in _extra.Keys) UnregisterHotKey(_window.Handle, id);
        _window.Dispose();
        if (Current == this) Current = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
