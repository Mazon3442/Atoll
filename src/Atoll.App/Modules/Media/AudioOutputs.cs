using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Atoll.Modules.Media;

/// <summary>Lists speakers and headphones and switches the Windows default output, like the volume flyout does.</summary>
internal static class AudioOutputs
{
    public static (IReadOnlyList<MediaSourceChoice> Outputs, string? Current) List()
    {
        using var devices = new MMDeviceEnumerator();
        var outputs = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => { using (d) return new MediaSourceChoice(d.ID, d.FriendlyName); }).ToList();
        if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return (outputs, null);
        using var current = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return (outputs, current.ID);
    }

    /// <summary>
    /// Volume of the playing app's audio sessions on the default output, or null when it has none.
    /// Matched by process name too: browsers and Spotify play from child processes.
    /// </summary>
    public static float? GetAppVolume(uint processId) => AppSessions(processId, s => s.SimpleAudioVolume.Volume).FirstOrDefault() is var v && v > -1 ? v : null;

    public static void SetAppVolume(uint processId, float level) =>
        AppSessions(processId, s => { s.SimpleAudioVolume.Volume = Math.Clamp(level, 0, 1); return 0f; }).ToList();

    private static List<float> AppSessions(uint processId, Func<AudioSessionControl, float> use)
    {
        var name = ProcessName(processId);
        using var devices = new MMDeviceEnumerator();
        if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return [-1];
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        var results = new List<float>();
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            var pid = session.GetProcessID;
            if (pid == processId || name is not null && pid != 0 && ProcessName(pid) == name) results.Add(use(session));
        }
        return results.Count > 0 ? results : [-1];
    }

    private static string? ProcessName(uint pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>Makes the device the default for every role (console, multimedia, communications).</summary>
    public static void SetDefault(string id)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            for (var role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    // Undocumented but stable since Windows 7; the volume flyout, EarTrumpet and SoundSwitch use it.
    // Only SetDefaultEndpoint is called; the slots before it keep the vtable order.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat();
        void GetProcessingPeriod(); void SetProcessingPeriod(); void GetShareMode(); void SetShareMode();
        void GetPropertyValue(); void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;
}
