using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace Atoll.Modules.Actions;

/// <summary>Wi-Fi and Bluetooth radios, through the same switch as Windows' quick settings.</summary>
internal static class RadioControl
{
    /// <summary>Null when this PC has no such radio (a wired desktop has no Wi-Fi) or Windows will not say.</summary>
    public static async Task<bool?> IsOnAsync(RadioKind kind)
    {
        try
        {
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
            return radio is null ? null : radio.State == RadioState.On;
        }
        catch (Exception ex)
        {
            Log.Info($"Could not read the {kind} radio: {ex.Message}");
            return null;
        }
    }

    /// <summary>Flips the radio and returns its new state, or null if it could not be changed.</summary>
    public static async Task<bool?> ToggleAsync(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
            if (radio is null) return null;
            var turnOn = radio.State != RadioState.On;
            var result = await radio.SetStateAsync(turnOn ? RadioState.On : RadioState.Off);
            return result == RadioAccessStatus.Allowed ? turnOn : null;
        }
        catch (Exception ex)
        {
            Log.Info($"Could not switch the {kind} radio: {ex.Message}");
            return null;
        }
    }
}

/// <summary>Windows' own dark or light mode for apps and the shell. ATOLL's colours are never touched.</summary>
internal static class WindowsTheme
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    public static void SetDark(bool dark)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        var value = dark ? 0 : 1;
        key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
        // Tells running apps and the shell to reload their colours.
        SendMessageTimeout(0xFFFF, 0x001A, 0, "ImmersiveColorSet", 0x2, 1000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}

/// <summary>Sleep, restart and shut down. Callers arm these with a second press first.</summary>
internal static class PowerActions
{
    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    public static void Sleep() => SetSuspendState(false, false, false);
    public static void Restart() => Shutdown("/r");
    public static void ShutDown() => Shutdown("/s");

    private static void Shutdown(string mode)
    {
        try { Process.Start(new ProcessStartInfo("shutdown.exe", $"{mode} /t 0") { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { Log.Error("Could not run shutdown", ex); }
    }
}
