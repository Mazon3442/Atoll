using System.Diagnostics;
using Avalonia.Threading;
using Avalonia.Media;

namespace Atoll.Desktop
{
    public static class DispatcherExtensions
    {
        public static void BeginInvoke(this Dispatcher dispatcher, Action action) => dispatcher.Post(action);
        public static void BeginInvoke(this Dispatcher dispatcher, DispatcherPriority priority, Action action) => dispatcher.Post(action, priority);
    }

    internal static class SystemSounds
    {
        public static Sound Exclamation { get; } = new();
        public static Sound Asterisk { get; } = new();
        public sealed class Sound
        {
            public void Play()
            {
                if (OperatingSystem.IsWindows()) WindowsSound();
                else _ = PlayAsync();
            }
            private static async Task PlayAsync()
            {
                try
                {
                    if (OperatingSystem.IsMacOS()) await Commands.RunAsync("afplay", "/System/Library/Sounds/Glass.aiff");
                    else await Commands.RunAsync("paplay", "/usr/share/sounds/freedesktop/stereo/complete.oga");
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { Log.Info("Alert sound is unavailable on this desktop."); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool MessageBeep(uint type);
        private static void WindowsSound() => MessageBeep(0x40);
    }

    internal static class Commands
    {
        public static async Task<string> RunAsync(string file, params string[] arguments)
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException($"Could not start {file}.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw new IOException($"{file} timed out."); }
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0) throw new IOException($"{file} could not complete the command.");
            return (await output).Trim();
        }

        public static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { Log.Error("Could not open item", ex); }
        }
    }
}

namespace Atoll.Settings
{
    public sealed record Choice<T>(T Value, string Label);
}

namespace Atoll.Shell
{
    public static class ShellIcons
    {
        public static IImage? For(string path) => null;
    }
}
