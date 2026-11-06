using System.Security;
using Atoll.Settings;

namespace Atoll.Desktop;

internal static class Startup
{
    public static void SetEnabled(bool enabled)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("ATOLL's executable path is unavailable.");
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue("Atoll", $"\"{executable}\""); else key.DeleteValue("Atoll", false);
            return;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "LaunchAgents", "com.mazon3442.atoll.plist")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "autostart", "atoll.desktop");
        if (!enabled) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = OperatingSystem.IsMacOS()
            ? $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>Label</key><string>com.mazon3442.atoll</string><key>ProgramArguments</key><array><string>{SecurityElement.Escape(executable)}</string></array><key>RunAtLoad</key><true/></dict></plist>"
            : "[Desktop Entry]\nType=Application\nName=ATOLL\nExec=\"" + executable.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"\nTerminal=false\n";
        File.WriteAllText(path, content);
    }
}
