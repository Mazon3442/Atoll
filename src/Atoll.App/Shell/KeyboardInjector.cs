using System.Runtime.InteropServices;

namespace Atoll.Shell;

/// <summary>Sends Ctrl+V to whichever window has the keyboard, so dictated text lands where the caret is.</summary>
internal static class KeyboardInjector
{
    private const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_V = 0x56;
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;

    /// <summary>Waits for the hotkey's own modifiers to be released, then pastes. Gives up quietly after two seconds.</summary>
    public static async Task PasteAsync()
    {
        for (var waited = 0; waited < 2000 && ModifierHeld(); waited += 25) await Task.Delay(25);
        if (ModifierHeld()) return; // never type over keys the user is still holding
        await Task.Delay(40); // let the clipboard settle
        Send(
            Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true));
    }

    internal static bool ModifierHeld() =>
        new[] { VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static INPUT Key(ushort virtualKey, bool up) => new()
    {
        Type = INPUT_KEYBOARD,
        Union = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey, Flags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nint Extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
