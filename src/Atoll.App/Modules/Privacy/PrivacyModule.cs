using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Atoll.Activities;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Atoll.Modules.Privacy;

/// <summary>What is using the camera or microphone right now. Orange is the microphone, green the camera.</summary>
public sealed partial class PrivacyViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Description))]
    private IReadOnlyList<string> _microphoneApps = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Description))]
    private IReadOnlyList<string> _cameraApps = [];

    public bool Microphone => MicrophoneApps.Count > 0;
    public bool Camera => CameraApps.Count > 0;

    public string Title => (Camera, Microphone) switch
    {
        (true, true) => "Camera and microphone in use",
        (true, false) => "Camera in use",
        _ => "Microphone in use",
    };

    /// <summary>The apps involved, e.g. "Discord · Teams".</summary>
    public string Apps => string.Join(" · ", CameraApps.Concat(MicrophoneApps).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>One line for screen readers and tooltips.</summary>
    public string Description => Apps.Length > 0 ? $"{Title}: {Apps}" : Title;

    partial void OnMicrophoneAppsChanged(IReadOnlyList<string> value) { OnPropertyChanged(nameof(Microphone)); OnPropertyChanged(nameof(Apps)); }
    partial void OnCameraAppsChanged(IReadOnlyList<string> value) { OnPropertyChanged(nameof(Camera)); OnPropertyChanged(nameof(Apps)); }
}

/// <summary>
/// Shows a dot while any app uses the microphone or camera, the way phones do. Windows records each app's
/// use under CapabilityAccessManager\ConsentStore; an app is using the device while its LastUsedTimeStop is 0.
/// A watcher thread wakes on registry changes, so nothing polls.
/// </summary>
public sealed class PrivacyModule : IIslandModule
{
    internal const string ConsentStorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private static readonly TimeSpan Announce = TimeSpan.FromSeconds(5);

    private readonly PrivacyViewModel _state = new();
    private readonly CancellationTokenSource _stop = new();
    private ModuleContext? _context;
    private DispatcherTimer? _settle;

    public object? Panel => null;

    public void Start(ModuleContext context)
    {
        _context = context;
        Rescan();
        new Thread(() => Watch(_stop.Token)) { IsBackground = true, Name = "ATOLL camera and mic watch" }.Start();
    }

    private void Rescan()
    {
        if (_context is null || _stop.IsCancellationRequested) return;
        using var store = Registry.CurrentUser.OpenSubKey(ConsentStorePath);
        var microphone = store is null ? [] : AppsUsing(store, "microphone");
        var camera = store is null ? [] : AppsUsing(store, "webcam");
        var started = microphone.Except(_state.MicrophoneApps).Any() || camera.Except(_state.CameraApps).Any();
        if (!microphone.SequenceEqual(_state.MicrophoneApps)) _state.MicrophoneApps = microphone;
        if (!camera.SequenceEqual(_state.CameraApps)) _state.CameraApps = camera;

        if (!_state.Microphone && !_state.Camera)
        {
            _settle?.Stop();
            _context.Remove("inuse");
            return;
        }
        if (!started) return;
        // Say who started it for a moment, then settle to a quiet dot beside whatever else is showing.
        _context.Post("inuse", _state, ActivityPriority.Normal);
        _settle ??= new DispatcherTimer { Interval = Announce };
        _settle.Tick -= OnSettle;
        _settle.Tick += OnSettle;
        _settle.Stop();
        _settle.Start();
    }

    private void OnSettle(object? sender, EventArgs e)
    {
        _settle?.Stop();
        if (_state.Microphone || _state.Camera) _context?.Post("inuse", _state, ActivityPriority.Ambient);
    }

    /// <summary>Display names of the apps using a capability ("microphone" or "webcam") right now.</summary>
    internal static IReadOnlyList<string> AppsUsing(RegistryKey store, string capability)
    {
        using var root = store.OpenSubKey(capability);
        if (root is null) return [];
        var names = new List<string>();
        foreach (var name in root.GetSubKeyNames())
        {
            using var app = root.OpenSubKey(name);
            if (app is null) continue;
            if (name == "NonPackaged")
            {
                foreach (var path in app.GetSubKeyNames())
                {
                    using var desktopApp = app.OpenSubKey(path);
                    if (desktopApp is not null && InUse(desktopApp)) names.Add(DesktopAppName(path));
                }
            }
            else if (InUse(app)) names.Add(PackagedAppName(name));
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool InUse(RegistryKey app) =>
        app.GetValue("LastUsedTimeStart") is long start && start != 0 && app.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" becomes "Windows Camera".</summary>
    internal static string PackagedAppName(string packageFamily)
    {
        var name = packageFamily.Split('_')[0];
        name = name[(name.LastIndexOf('.') + 1)..];
        return Regex.Replace(name, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " "); // "MSTeams" → "MS Teams"
    }

    /// <summary>"C:#Program Files#Discord#Discord.exe" becomes the exe's description ("Discord"), else its file name.</summary>
    internal static string DesktopAppName(string encodedPath)
    {
        var path = encodedPath.Replace('#', '\\');
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
                return description.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private void Watch(CancellationToken token)
    {
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(ConsentStorePath);
            if (store is null) return;
            using var changed = new AutoResetEvent(false);
            while (!token.IsCancellationRequested)
            {
                // One-shot: re-armed after each change. The thread stays alive, as asynchronous notification requires.
                if (RegNotifyChangeKeyValue(store.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true) != 0) return;
                if (WaitHandle.WaitAny([changed, token.WaitHandle]) != 0) return;
                Thread.Sleep(150); // apps write start and stop times in bursts
                _context?.Dispatcher.BeginInvoke(Rescan);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Info($"Camera and microphone watch stopped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _settle?.Stop();
        _context?.Remove("inuse");
    }

    private const int REG_NOTIFY_CHANGE_NAME = 0x1;
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int filter, SafeWaitHandle evt, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
