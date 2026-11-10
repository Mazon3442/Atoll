using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Atoll.Modules.Claude;

/// <summary>The executable and leading arguments that run a CLI agent.</summary>
public sealed record ProcessCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>One running CLI process. Standard output is read line by line; stdin carries the prompt and is then closed.</summary>
public interface IProcessSession : IDisposable
{
    TextWriter StandardInput { get; }

    TextReader StandardOutput { get; }

    TextReader StandardError { get; }

    Task<int> Completion { get; }

    /// <summary>Stops the whole process tree, including the node process behind a Windows npm shim.</summary>
    void Kill();
}

/// <summary>
/// Starts CLI agents. Resolve is the availability probe: it runs once per session and never assumes a command exists.
/// </summary>
public interface IProcessRunner
{
    /// <summary>The command that runs <paramref name="command"/>, or null when it is not installed.</summary>
    ProcessCommand? Resolve(string command);

    IProcessSession Start(ProcessCommand command);
}

/// <summary>Starts real processes. Windows CLIs are usually npm .cmd shims, which are resolved to their node script.</summary>
public sealed class DesktopProcessRunner : IProcessRunner
{
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat", ".ps1"];

    public ProcessCommand? Resolve(string command)
    {
        if (command.Length == 0 || command.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var path = FindExecutable(command);
        return path is null ? null : EntryPoint(path);
    }

    public IProcessSession Start(ProcessCommand command)
    {
        var info = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        if (!process.Start()) throw new AgentChatException($"{Path.GetFileNameWithoutExtension(command.FileName)} could not start.");
        return new Session(process);
    }

    private static string? FindExecutable(string name)
    {
        var names = Path.HasExtension(name) ? [name] : Extensions.Select(extension => name + extension).ToArray();
        foreach (var directory in SearchDirectories())
        {
            foreach (var candidate in names)
            {
                var path = Path.Combine(directory, candidate);
                try { if (File.Exists(path)) return path; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return null;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (directory.Length > 0) yield return directory;
        // npm puts global shims here; it is normally on PATH too, but a just-installed CLI shows up before the shell refreshes.
        if (Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData) yield return Path.Combine(appData, "npm");
    }

    private static ProcessCommand? EntryPoint(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".cmd" or ".bat") return ShimEntryPoint(path);
        if (extension == ".ps1") return new("powershell.exe", ["-NoProfile", "-File", path]);
        return new(path, []);
    }

    /// <summary>
    /// npm's Windows shims are small templates. Running one directly needs cmd.exe and a quoted command line, which
    /// breaks on paths with spaces, so launch the node script (or exe) the shim would have run instead.
    /// </summary>
    private static ProcessCommand? ShimEntryPoint(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (Path.GetDirectoryName(path) is not { } directory) return null;

        var script = Regex.Match(text, "[\"']?%_prog%[\"']?\\s+\"(?<path>%dp0%\\\\[^\"]+?\\.(?:js|mjs|cjs))\"", RegexOptions.IgnoreCase);
        if (script.Success)
        {
            var target = script.Groups["path"].Value
                .Replace("%dp0%", directory, StringComparison.OrdinalIgnoreCase)
                .Replace('\\', Path.DirectorySeparatorChar);
            var sibling = Path.Combine(directory, "node.exe");
            var node = File.Exists(sibling) ? sibling : FindExecutable("node");
            if (node is not null && File.Exists(target)) return new(node, [target]);
        }

        var executable = Regex.Match(text, "\"(?<path>%(?:dp0|~dp0)%\\\\[^\"]+\\.exe)\"", RegexOptions.IgnoreCase);
        if (executable.Success)
        {
            var target = executable.Groups["path"].Value
                .Replace("%~dp0%", directory, StringComparison.OrdinalIgnoreCase)
                .Replace("%dp0%", directory, StringComparison.OrdinalIgnoreCase)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (File.Exists(target)) return new(target, []);
        }

        return null; // an unknown shim is not a trustworthy launch; the caller reports no agent found
    }

    private sealed class Session(Process process) : IProcessSession
    {
        private readonly TextWriter _input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        public TextWriter StandardInput => _input;
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;

        public Task<int> Completion => ExitAsync();

        private async Task<int> ExitAsync()
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }

        public void Kill()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
        }

        public void Dispose()
        {
            Kill();
            process.Dispose();
        }
    }
}
