using System.Diagnostics;
using System.Runtime.InteropServices;
using Atoll.Activities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;

namespace Atoll.Modules.Actions;

public sealed partial class QuickActionsViewModel(QuickActionsModule module) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicGlyph), nameof(MicLabel))]
    private bool _micMuted;

    public string MicGlyph => MicMuted ? "" : "";
    public string MicLabel => MicMuted ? "Unmute mic" : "Mute mic";

    [RelayCommand] private void Screenshot() => module.Screenshot();
    [RelayCommand] private void ToggleMic() => module.ToggleMic();
    [RelayCommand] private void Lock() => module.Lock();
    [RelayCommand] private void ScreenOff() => module.ScreenOff();
    [RelayCommand] private void Downloads() => module.OpenDownloads();
}

/// <summary>One-click actions: screenshot, microphone mute, lock, display off, Downloads folder.</summary>
public sealed class QuickActionsModule : IIslandModule
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private static readonly nint HWND_BROADCAST = 0xFFFF;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out string path);

    private static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    private readonly QuickActionsViewModel _vm;
    private ModuleContext? _context;

    public QuickActionsModule() => _vm = new QuickActionsViewModel(this);

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _vm.MicMuted = WithMic(v => v.Mute) ?? false;
    }

    internal void Screenshot() => Run("ms-screenclip:");

    internal void ToggleMic()
    {
        var muted = WithMic(v =>
        {
            v.Mute = !v.Mute;
            return v.Mute;
        });
        if (muted is not { } m)
        {
            Indicate("", "No microphone found");
            return;
        }
        _vm.MicMuted = m;
        Indicate(m ? "" : "", m ? "Microphone muted" : "Microphone on");
    }

    internal void Lock() => LockWorkStation();

    // Posted (not sent) so a hung window cannot block the island.
    internal void ScreenOff() => PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2);

    internal void OpenDownloads()
    {
        var path = SHGetKnownFolderPath(DownloadsFolder, 0, 0, out var p) == 0 ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Run(path);
    }

    /// <summary>Runs <paramref name="use"/> on the default communications microphone; null if there is none.</summary>
    private static bool? WithMic(Func<AudioEndpointVolume, bool> use)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var mic = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return use(mic.AudioEndpointVolume);
        }
        catch (Exception ex)
        {
            Log.Info($"Microphone unavailable: {ex.Message}");
            return null;
        }
    }

    private void Indicate(string glyph, string title) =>
        _context?.Post("action", new IndicatorViewModel(glyph, title), ActivityPriority.High, TimeSpan.FromSeconds(2));

    private static void Run(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open {target}", ex); }
    }

    public void Dispose() { }
}
