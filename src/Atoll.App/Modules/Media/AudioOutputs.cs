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
