using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Settings;
using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Atoll.Modules.Notifications;

public enum NotificationAccess
{
    /// <summary>Atoll has no package identity yet (packaging\setup-identity.ps1 not run).</summary>
    NeedsSetup,
    NeedsPermission,
    Denied,
    Allowed,
    Unavailable,
}

/// <summary>Package identity and notification-permission checks, shared with the settings window.</summary>
public static class NotificationAccessInfo
{
    /// <summary>Raised after a permission request so the running module can start listening.</summary>
    public static event Action? Changed;
    public static event Action? PreviewRequested;
    public static void Preview() => PreviewRequested?.Invoke();
    public static NotificationAccess Refresh() { Changed?.Invoke(); return Get(); }

    public static bool HasIdentity()
    {
        try
        {
            return Package.Current.Id is not null;
        }
        catch (InvalidOperationException) { return false; } // unpackaged: "no package identity"
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    public static NotificationAccess Get()
    {
        if (!HasIdentity()) return NotificationAccess.NeedsSetup;
        try
        {
            return UserNotificationListener.Current.GetAccessStatus() switch
            {
                UserNotificationListenerAccessStatus.Allowed => NotificationAccess.Allowed,
                UserNotificationListenerAccessStatus.Denied => NotificationAccess.Denied,
                _ => NotificationAccess.NeedsPermission,
            };
        }
        catch (Exception ex)
        {
            Log.Error("Notification access check failed", ex);
            return NotificationAccess.Unavailable;
        }
    }

    /// <summary>Shows Windows' consent prompt. Call on the UI thread.</summary>
    public static async Task<NotificationAccess> RequestAsync()
    {
        if (HasIdentity())
        {
            try { await UserNotificationListener.Current.RequestAccessAsync(); }
            catch (Exception ex) { Log.Error("Notification access request failed", ex); }
        }
        Changed?.Invoke();
        return Get();
    }

    public static string Describe(NotificationAccess access) => access switch
    {
        NotificationAccess.NeedsSetup =>
            "Open Settings › Notifications to connect ATOLL to Windows notifications.",
        NotificationAccess.NeedsPermission => "ATOLL needs your permission to read notifications.",
        NotificationAccess.Denied =>
            "Notification access is off. Turn it on for ATOLL in Windows Settings > Privacy & security > Notifications.",
        NotificationAccess.Allowed => "ATOLL can show your notifications.",
        _ => "Notifications are not available on this PC.",
    };

    public static void OpenWindowsSettings() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true });
}

public sealed partial class NotificationItemViewModel(Action<NotificationItemViewModel> mute, Action<NotificationItemViewModel> dismiss) : ObservableObject
{
    public required uint Id { get; init; }
    public required string AppName { get; init; }
    public ImageSource? Icon { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset Time { get; init; }
    public bool IsClaude { get; init; }

    public bool HasBody => Body.Length > 0;
    public string TimeText => Time.ToLocalTime().ToString("t");

    [RelayCommand]
    private void Mute() => mute(this);

    [RelayCommand]
    private void Dismiss() => dismiss(this);
}

public sealed partial class NotificationsViewModel(Action requestAccess) : ObservableObject
{
    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllowed), nameof(StatusText), nameof(CanRequest))]
    private NotificationAccess _access;

    public bool IsAllowed => Access == NotificationAccess.Allowed;
    public bool CanRequest => Access is NotificationAccess.NeedsPermission;
    public string StatusText => NotificationAccessInfo.Describe(Access);
    public bool IsEmpty => Items.Count == 0;

    public void ItemsChanged() => OnPropertyChanged(nameof(IsEmpty));

    [RelayCommand]
    private void AllowAccess() => requestAccess();

    [RelayCommand]
    private void OpenWindowsSettings() => NotificationAccessInfo.OpenWindowsSettings();

    [RelayCommand]
    private void OpenSetup()
    {
        if (System.Windows.Application.Current is App app) app.OpenSettings(3);
    }

    [RelayCommand]
    private void Clear()
    {
        Items.Clear();
        ItemsChanged();
    }
}

/// <summary>
/// Mirrors Windows toast notifications into the island (needs package identity + user consent).
/// New toasts pop the island briefly unless Windows is in Do Not Disturb, fullscreen or presenting;
/// all of them land in the panel history. Claude desktop app messages get the Claude look.
/// </summary>
public sealed class NotificationsModule : IIslandModule
{
    private const int MaxHistory = 30;
    private static readonly TimeSpan PopupTime = TimeSpan.FromSeconds(6);

    private readonly NotificationsViewModel _vm;
    private ModuleContext? _context;
    private UserNotificationListener? _listener;
    private HashSet<uint> _known = [];
    private DispatcherTimer? _poll;
    private bool _refreshing;
    private bool _refreshAgain;
    private bool _disposed;

    public NotificationsModule() => _vm = new NotificationsViewModel(() => _ = NotificationAccessInfo.RequestAsync());

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        NotificationAccessInfo.Changed += OnAccessChanged;
        NotificationAccessInfo.PreviewRequested += ShowPreview;
        OnAccessChanged();
    }

    private void ShowPreview()
    {
        if (_context is null || _disposed) return;
        var item = new NotificationItemViewModel(Mute, Dismiss)
        {
            Id = 0, AppName = "ATOLL preview", Title = "Your island is ready",
            Body = "This is a preview. New Windows notifications will appear here once access is connected.", Time = DateTimeOffset.Now,
        };
        _context.Post("preview", item, ActivityPriority.High, PopupTime, alert: true);
    }

    private void OnAccessChanged() => _context?.Dispatcher.BeginInvoke(async () =>
    {
        if (_disposed) return;
        _vm.Access = NotificationAccessInfo.Get();
        if (_vm.Access == NotificationAccess.Allowed && _listener is null) await StartListeningAsync();
        else if (_vm.Access != NotificationAccess.Allowed) StopListening();
    });

    private async Task StartListeningAsync()
    {
        var listener = _listener = UserNotificationListener.Current;
        try
        {
            // Toasts already in the notification center are history, not news.
            var existing = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (_disposed || _vm.Access != NotificationAccess.Allowed || !ReferenceEquals(_listener, listener)) return;
            _known = existing.Select(n => n.Id).ToHashSet();
        }
        catch (Exception ex)
        {
            Log.Error("Reading notifications failed", ex);
        }

        if (_disposed || _vm.Access != NotificationAccess.Allowed || !ReferenceEquals(_listener, listener)) return;

        try
        {
            listener.NotificationChanged += OnNotificationChanged;
        }
        catch (Exception ex)
        {
            // Some Windows builds only raise this event for background tasks; poll instead.
            Log.Info($"Notification events unavailable ({ex.Message}); polling");
            _poll = new DispatcherTimer(DispatcherPriority.Background, _context!.Dispatcher) { Interval = TimeSpan.FromSeconds(1.5) };
            _poll.Tick += (_, _) => _ = RefreshAsync();
            _poll.Start();
        }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind == UserNotificationChangedKind.Added) _context?.Dispatcher.BeginInvoke(() => _ = RefreshAsync());
    }

    private async Task RefreshAsync()
    {
        if (_listener is null || _disposed) return;
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                if (_disposed || _vm.Access != NotificationAccess.Allowed) return;
                var fresh = list.Where(n => !_known.Contains(n.Id)).OrderBy(n => n.CreationTime).ToList();
                _known = list.Select(n => n.Id).ToHashSet();
                foreach (var n in fresh) await ShowAsync(n);
            } while (_refreshAgain && !_disposed);
        }
        catch (Exception ex)
        {
            Log.Error("Reading notifications failed", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ShowAsync(UserNotification n)
    {
        var settings = _context!.Settings.Current.Notifications;
        var item = await BuildAsync(n, settings.ShowContent);
        if (item is null || settings.MutedApps.Contains(item.AppName, StringComparer.OrdinalIgnoreCase)) return;

        _vm.Items.Insert(0, item);
        while (_vm.Items.Count > MaxHistory) _vm.Items.RemoveAt(_vm.Items.Count - 1);
        _vm.ItemsChanged();

        if (Native.SHQueryUserNotificationState(out var state) == 0 && state != Native.QUNS_ACCEPTS_NOTIFICATIONS)
            return; // Do Not Disturb, fullscreen, presentation: history only
        _context.Post("latest", item, ActivityPriority.High, PopupTime, alert: true);
    }

    private async Task<NotificationItemViewModel?> BuildAsync(UserNotification n, bool showContent)
    {
        string appName = "Notification";
        ImageSource? icon = null;
        var isClaude = false;
        try
        {
            var info = n.AppInfo;
            appName = info.DisplayInfo.DisplayName;
            isClaude = appName == "Claude" || SafePackageFamily(info).StartsWith("Claude_", StringComparison.Ordinal);
            icon = await ImageLoader.LoadAsync(info.DisplayInfo.GetLogo(new Windows.Foundation.Size(48, 48)), 48);
        }
        catch (Exception)
        {
            // Some system toasts carry no app info or logo; show them with defaults.
        }

        var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?
            .GetTextElements().Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList() ?? [];
        if (texts.Count == 0) return null; // nothing readable to show

        return new NotificationItemViewModel(Mute, Dismiss)
        {
            Id = n.Id,
            AppName = appName,
            Icon = icon,
            Title = showContent ? texts[0] : appName,
            Body = showContent ? string.Join(" ", texts.Skip(1)) : "New notification",
            Time = n.CreationTime,
            IsClaude = isClaude,
        };
    }

    private static string SafePackageFamily(AppInfo info)
    {
        try { return info.PackageFamilyName ?? ""; }
        catch (Exception) { return ""; } // unpackaged senders
    }

    private void Mute(NotificationItemViewModel item)
    {
        var muted = _context!.Settings.Current.Notifications.MutedApps;
        if (!muted.Contains(item.AppName, StringComparer.OrdinalIgnoreCase)) muted.Add(item.AppName);
        foreach (var other in _vm.Items.Where(i => i.AppName == item.AppName).ToList()) _vm.Items.Remove(other);
        _vm.ItemsChanged();
        _context.Settings.NotifyChanged();
    }

    private void Dismiss(NotificationItemViewModel item)
    {
        _vm.Items.Remove(item);
        _vm.ItemsChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        NotificationAccessInfo.Changed -= OnAccessChanged;
        NotificationAccessInfo.PreviewRequested -= ShowPreview;
        StopListening();
    }

    private void StopListening()
    {
        _poll?.Stop();
        _poll = null;
        _context?.Remove("latest");
        _context?.Remove("preview");
        _vm.Items.Clear();
        _vm.ItemsChanged();
        if (_listener is not null)
        {
            try { _listener.NotificationChanged -= OnNotificationChanged; }
            catch (Exception) { /* was never subscribed (polling mode) */ }
            _listener = null;
        }
    }
}
