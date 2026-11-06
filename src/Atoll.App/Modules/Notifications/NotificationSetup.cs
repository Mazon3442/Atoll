using System.ComponentModel;
using System.Diagnostics;

namespace Atoll.Modules.Notifications;

public sealed record NotificationSetupGuide(string Title, string Description, string Action)
{
    public static NotificationSetupGuide For(NotificationAccess access, bool setupAvailable) => access switch
    {
        NotificationAccess.NeedsSetup when setupAvailable => new("Connect Windows notifications", "First register ATOLL with Windows. Setup asks for administrator approval to trust ATOLL's signing certificate and register its notification package. Then restart ATOLL and allow access.", "Set up notifications"),
        NotificationAccess.NeedsSetup => new("Notification setup files are missing", "This copy does not include the Windows notification package. Open the setup instructions to prepare it, then return here.", "Open setup instructions"),
        NotificationAccess.NeedsPermission => new("Allow notification access", "ATOLL is registered with Windows. Allow access in the Windows prompt to show notifications from your apps in the island.", "Allow access"),
        NotificationAccess.Denied => new("Notification access is off", "Open Windows Settings, enable notification access for ATOLL, then return here. Access is checked again automatically.", "Open Windows settings"),
        NotificationAccess.Allowed => new("Notifications are connected", "New notifications appear in the island. Windows Do Not Disturb and fullscreen rules still apply. Receive a notification from another app to check the connection.", "Preview in the island"),
        _ => new("Windows notifications are unavailable", "Windows could not read notification access. Check Windows settings, then use Check again.", "Open Windows settings"),
    };
}

public static class NotificationSetup
{
    public static string? FindDirectory()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "NotificationSetup");
        if (Complete(bundled)) return bundled;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "packaging");
            if (Complete(candidate)) return candidate;
        }
        return null;
    }
    internal static bool Complete(string dir) => new[] { "setup-identity.ps1", "out/AtollDev.cer", "out/Atoll.Identity.msix" }
        .All(file => File.Exists(Path.Combine(dir, file)));

    public static async Task<bool> InstallAsync(string directory)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(directory, "setup-identity.ps1"), "-AppDir", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "-NoPause" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Setup did not start.");
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }
}
