using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Atoll.Claude;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Claude;

public sealed class AgentIntegrationInstaller(string? userDirectory = null, string? hookExe = null)
{
    private readonly string _userDirectory = userDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _hookExe = hookExe ?? ClaudeHookInstaller.HookExePath;
    private const string Marker = "ATOLL agent integration";
    internal static readonly string[] CodexEvents =
        ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Stop", "Interrupt", "SessionEnd"];

    private string Home(string variable, string fallback) => userDirectory is null
        ? Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : fallback
        : fallback;

    public string PathFor(string agent) => agent switch
    {
        "claude" => Path.Combine(_userDirectory, ".claude", "settings.json"),
        "codex" => Path.Combine(Home("CODEX_HOME", Path.Combine(_userDirectory, ".codex")), "hooks.json"),
        "opencode" => Path.Combine(Home("OPENCODE_CONFIG_DIR", Path.Combine(Home("XDG_CONFIG_HOME", Path.Combine(_userDirectory, ".config")), "opencode")), "plugins", "atoll.js"),
        "hermes" => Path.Combine(Home("HERMES_HOME", userDirectory is null
            ? OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes") : Path.Combine(_userDirectory, ".hermes")
            : Path.Combine(_userDirectory, ".hermes")), "plugins", "atoll", "__init__.py"),
        "shell" => Path.Combine(Documents, "PowerShell", "atoll-commands.ps1"),
        _ => throw new ArgumentException("Unknown agent", nameof(agent)),
    };

    private string Documents => userDirectory is null ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.Combine(_userDirectory, "Documents");

    /// <summary>PowerShell 7 and Windows PowerShell profiles; each dot-sources the command alert script.</summary>
    internal IEnumerable<string> ShellProfiles => new[] { "PowerShell", "WindowsPowerShell" }.Select(d => Path.Combine(Documents, d, "Microsoft.PowerShell_profile.ps1"));

    private string ProfileLine => $". '{PathFor("shell").Replace("'", "''")}' # {Marker}";

    private string Command(string agent) => ClaudeHookInstaller.QuoteCommand(_hookExe) + (agent == "claude" ? "" : $" --agent {agent}");

    public bool IsInstalled(string agent)
    {
        try
        {
            if (agent is "claude" or "codex")
                return ClaudeHookInstaller.GetStatus(PathFor(agent), Command(agent),
                    agent == "claude" ? ClaudeHookInstaller.Events.Length : CodexEvents.Length) == ClaudeHookInstaller.Status.Installed;
            if (agent == "shell" && !ShellProfiles.All(p => File.Exists(p) && File.ReadAllText(p).Contains(ProfileLine, StringComparison.Ordinal))) return false;
            return File.Exists(PathFor(agent)) && File.ReadAllText(PathFor(agent)) == Script(agent)
                && (agent != "hermes" || File.Exists(Path.Combine(Path.GetDirectoryName(PathFor(agent))!, "plugin.yaml")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool HasAdapter(string agent)
    {
        try
        {
            if (agent is "claude" or "codex")
                return ClaudeHookInstaller.GetStatus(PathFor(agent), Command(agent)) is
                    ClaudeHookInstaller.Status.Installed or ClaudeHookInstaller.Status.InstalledElsewhere;
            return File.Exists(PathFor(agent)) && File.ReadAllText(PathFor(agent)).Contains(Marker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal string Script(string agent) => agent == "shell"
        ? Resource("powershell.ps1").Replace("__ATOLL_HOOK_EXE__", $"'{_hookExe.Replace("'", "''")}'")
        : Resource(agent == "opencode" ? "opencode.js" : "hermes.py").Replace("__ATOLL_HOOK_EXE__", JsonSerializer.Serialize(_hookExe));

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Atoll.Integrations.{name}")
            ?? throw new InvalidOperationException($"Missing agent adapter: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task<string?> InstallAsync(string agent)
    {
        try
        {
            if (!File.Exists(_hookExe)) return "The ATOLL hook executable is missing. Rebuild or reinstall ATOLL first.";
            if (agent is "claude" or "codex")
                return ClaudeHookInstaller.Update(root =>
                {
                    ClaudeHookInstaller.RemoveHooks(root);
                    ClaudeHookInstaller.AddHooks(root, Command(agent), agent == "claude" ? ClaudeHookInstaller.Events : CodexEvents);
                }, PathFor(agent));

            WriteOwned(PathFor(agent), Script(agent));
            if (agent == "shell")
                foreach (var profile in ShellProfiles)
                {
                    var text = File.Exists(profile) ? File.ReadAllText(profile) : "";
                    if (text.Contains(ProfileLine, StringComparison.Ordinal)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
                    File.AppendAllText(profile, (text.Length == 0 || text.EndsWith('\n') ? "" : Environment.NewLine) + ProfileLine + Environment.NewLine);
                }
            if (agent == "hermes")
            {
                WriteOwned(Path.Combine(Path.GetDirectoryName(PathFor(agent))!, "plugin.yaml"), "# " + Marker + "\n" + Resource("hermes-plugin.yaml"));
                return await ToggleHermesAsync(enable: true);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Installing agent integration failed", ex);
            return ex.Message;
        }
    }

    public async Task<string?> RemoveAsync(string agent)
    {
        try
        {
            if (agent is "claude" or "codex")
                return ClaudeHookInstaller.Update(ClaudeHookInstaller.RemoveHooks, PathFor(agent));
            if (agent == "hermes")
            {
                var error = await ToggleHermesAsync(enable: false);
                if (error is not null) return error;
            }
            if (agent == "shell")
                foreach (var profile in ShellProfiles.Where(File.Exists))
                {
                    var lines = File.ReadAllLines(profile);
                    var kept = lines.Where(l => !l.Contains(Marker, StringComparison.Ordinal)).ToArray();
                    var bom = File.ReadAllBytes(profile).AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]); // Windows PowerShell reads BOM-less files as ANSI
                    if (kept.Length != lines.Length) File.WriteAllLines(profile, kept, new System.Text.UTF8Encoding(bom));
                }
            DeleteOwned(PathFor(agent));
            if (agent == "hermes") DeleteOwned(Path.Combine(Path.GetDirectoryName(PathFor(agent))!, "plugin.yaml"));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Removing agent integration failed", ex);
            return ex.Message;
        }
    }

    private static void WriteOwned(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (!File.ReadAllText(path).Contains(Marker, StringComparison.Ordinal))
                throw new IOException($"{path} already exists and does not belong to ATOLL; left unchanged.");
            File.Copy(path, path + ".atoll-backup", overwrite: true);
        }
        var temp = path + ".atoll-tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static void DeleteOwned(string path)
    {
        if (!File.Exists(path)) return;
        if (!File.ReadAllText(path).Contains(Marker, StringComparison.Ordinal))
            throw new IOException($"{path} does not belong to ATOLL; left unchanged.");
        File.Delete(path);
    }

    private async Task<string?> ToggleHermesAsync(bool enable)
    {
        var verb = enable ? "enable" : "disable";
        try
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "hermes.exe" : "hermes")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("plugins");
            start.ArgumentList.Add(verb);
            start.ArgumentList.Add("atoll");
            start.Environment["HERMES_HOME"] = Directory.GetParent(Path.GetDirectoryName(PathFor("hermes"))!)!.Parent!.FullName;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Hermes.");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return $"Hermes setup timed out. Run `hermes plugins {verb} atoll` in your terminal.";
            }
            await Task.WhenAll(output, errors);
            return process.ExitCode == 0 ? null : $"Run `hermes plugins {verb} atoll` in your terminal to finish setup.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Could not run Hermes. Run `hermes plugins {verb} atoll` in your terminal: {ex.Message}";
        }
    }
}

public sealed partial class AgentIntegrationRow(string agent, AgentIntegrationInstaller installer) : ObservableObject
{
    private bool _installed = installer.IsInstalled(agent);
    private bool _hasAdapter = installer.HasAdapter(agent);
    public string Name => HookProtocol.AgentName(agent);
    public string Path => installer.PathFor(agent);
    public bool Installed => _installed;
    public bool CanRemove => _hasAdapter;
    public string ActionLabel => Installed ? "Installed" : CanRemove ? "Update" : "Install";
    public string ActionAccessibleName => $"{ActionLabel} {Name} integration";
    public string Status => (Installed ? "Adapter is up to date. " : CanRemove ? "Adapter needs an update for this build. " : "Not installed. ") + SetupHint;
    public string SetupHint => agent switch
    {
        "codex" => "Review and trust ATOLL's hooks in Codex Settings › Hooks (or /hooks in the CLI), then restart sessions. Hooks must be enabled.",
        "hermes" => "Setup enables the atoll plugin through Hermes. Restart running Hermes sessions afterward.",
        "shell" => "Commands that run 10 seconds or longer show in the island when they finish while you're in another window. Open a new PowerShell window afterward.",
        _ => "Restart running sessions after installing or updating the adapter.",
    };
    [ObservableProperty] private string _message = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(RemoveCommand))]
    private bool _isBusy;
    private bool CanChange => !IsBusy;
    private bool CanInstall => !IsBusy && !Installed;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        IsBusy = true;
        Message = CanRemove ? "Updating adapter…" : "Installing adapter…";
        try { Message = await installer.InstallAsync(agent) ?? "Installed. " + SetupHint; }
        finally { IsBusy = false; Refresh(); }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task Remove()
    {
        IsBusy = true;
        Message = "Removing adapter…";
        try { Message = await installer.RemoveAsync(agent) ?? "Removed ATOLL's adapter. Other integrations were kept."; }
        finally { IsBusy = false; Refresh(); }
    }

    public void Refresh()
    {
        _installed = installer.IsInstalled(agent);
        _hasAdapter = installer.HasAdapter(agent);
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ActionAccessibleName));
        InstallCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }
}
