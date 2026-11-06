using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Atoll.Modules.Media;

internal sealed record AudioSource(uint ProcessId, string Name, bool Active);

internal static class AudioSources
{
    public static IReadOnlyList<AudioSource> Get()
    {
        var result = new Dictionary<uint, AudioSource>();
        using var devices = new MMDeviceEnumerator();
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var manager = device.AudioSessionManager;
        using var sessions = manager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            var pid = session.GetProcessID;
            if (pid == 0 || session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired) continue;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                result[pid] = new AudioSource(pid, process.ProcessName, session.State == AudioSessionState.AudioSessionStateActive);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return result.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
