using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Atoll.Modules.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Settings;

public sealed partial class SettingsViewModel
{
    private NotificationAccess _notificationAccess = NotificationAccessInfo.Get();
    [ObservableProperty] private string _notificationSetupMessage = "";
    [ObservableProperty] private bool _notificationSetupBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotificationActionLabel), nameof(NotificationGuideDescription), nameof(NotificationGuideTitle), nameof(NotificationRegistrationStep))]
    private bool _notificationRestartNeeded;
    public string NotificationGuideTitle => NotificationRestartNeeded ? "Restart to finish setup" : NotificationSetupGuide.For(_notificationAccess, NotificationSetup.FindDirectory() is not null).Title;
    public string NotificationGuideDescription => NotificationRestartNeeded ? "Setup finished. Restart ATOLL to activate its Windows registration, then allow access here." : NotificationSetupGuide.For(_notificationAccess, NotificationSetup.FindDirectory() is not null).Description;
    public string NotificationActionLabel => NotificationRestartNeeded ? "Restart ATOLL" : NotificationSetupGuide.For(_notificationAccess, NotificationSetup.FindDirectory() is not null).Action;
    public string NotificationRegistrationStep => _notificationAccess == NotificationAccess.NeedsSetup && !NotificationRestartNeeded ? "1 · Register with Windows" : "1 · Windows registration ready";
    public string NotificationPermissionStep => _notificationAccess == NotificationAccess.Allowed ? "2 · Notification access allowed" : "2 · Allow notification access";
    public bool NotificationModuleEnabled
    {
        get => S.Modules.Any(m => m.Id == "notifications" && m.Enabled);
        set
        {
            if (Modules.FirstOrDefault(m => m.Entry.Id == "notifications") is { } row) row.Enabled = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void RefreshNotificationAccess()
    {
        _notificationAccess = NotificationAccessInfo.Refresh();
        foreach (var property in new[] { nameof(NotificationStatus), nameof(CanRequestNotifications), nameof(NotificationsAllowed), nameof(NotificationGuideTitle),
            nameof(NotificationGuideDescription), nameof(NotificationActionLabel), nameof(NotificationRegistrationStep), nameof(NotificationPermissionStep), nameof(NotificationModuleEnabled) })
            OnPropertyChanged(property);
    }

    [RelayCommand]
    private async Task SetupNotificationsAsync()
    {
        NotificationSetupMessage = "";
        if (NotificationRestartNeeded)
        {
            try
            {
                if (Application.Current is App app) app.RestartToSettings(3);
                else NotificationSetupMessage = "Close and reopen ATOLL, then return to Notifications.";
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) { NotificationSetupMessage = "Could not restart ATOLL. Close and reopen it, then return to Notifications."; }
            return;
        }
        RefreshNotificationAccess();
        if (_notificationAccess == NotificationAccess.Allowed)
        {
            if (!NotificationModuleEnabled) { NotificationSetupMessage = "Turn on the Notifications feature below before previewing."; return; }
            NotificationAccessInfo.Preview();
            NotificationSetupMessage = "Preview sent to the island. To check Windows access, receive a notification from another app.";
            return;
        }
        if (_notificationAccess == NotificationAccess.NeedsPermission) { await AllowNotifications(); return; }
        if (_notificationAccess != NotificationAccess.NeedsSetup) { NotificationAccessInfo.OpenWindowsSettings(); return; }
        var directory = NotificationSetup.FindDirectory();
        if (directory is null)
        {
            Process.Start(new ProcessStartInfo("https://github.com/Mazon3442/Atoll#notifications-one-time-setup") { UseShellExecute = true });
            return;
        }
        NotificationSetupBusy = true;
        NotificationSetupMessage = "Waiting for Windows setup. Approve the administrator prompt to continue.";
        try
        {
            NotificationRestartNeeded = await NotificationSetup.InstallAsync(directory);
            NotificationSetupMessage = NotificationRestartNeeded ? "Windows registration installed. Restart ATOLL to continue." : "Setup did not finish. Try again and approve the administrator prompt.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { NotificationSetupMessage = "Setup was canceled. You can try again when ready."; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { NotificationSetupMessage = "Could not complete setup. Check that the notification package is included, then try again."; Log.Error("Notification setup failed", ex); }
        finally { NotificationSetupBusy = false; }
    }
}
