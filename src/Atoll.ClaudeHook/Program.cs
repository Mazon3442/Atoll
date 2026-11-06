// Atoll.ClaudeHook: run by Claude Code hooks (see ClaudeHookInstaller in the app).
// Reads the hook JSON from stdin and forwards a compact summary to the island over a named pipe.
// It never writes to stdout and always exits 0: hook output can change Claude Code's behavior,
// and the island being closed must never slow down or break a session.
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Atoll.Claude;

try
{
    var agent = args.Length == 2 && args[0] == "--agent" ? args[1] : "claude";
    if (!HookProtocol.Agents.Contains(agent)) return 0;
    using var stdin = Console.OpenStandardInput();
    using var buffer = new MemoryStream();
    stdin.CopyTo(buffer); // drain fully even if the island is closed
    var input = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
    if (input.Span.StartsWith("﻿"u8)) input = input[3..]; // some shells prepend a UTF-8 BOM
    using var doc = JsonDocument.Parse(input);

    // Pipe missing = island not running: leave right away instead of waiting out a connect timeout.
    if (!PipeCheck.Exists()) return 0;

    // Tool events are the most frequent and the island already knows the window from earlier events.
    var eventName = doc.RootElement.TryGetProperty("hook_event_name", out var ev) ? ev.GetString() : null;
    var hwnd = eventName is "PreToolUse" or "PostToolUse" ? 0 : HostWindow.Find();

    var message = HookProtocol.FromHookInput(doc.RootElement, hwnd, agent);
    var line = JsonSerializer.SerializeToUtf8Bytes(message, HookProtocol.Json);
    if (line.Length > HookProtocol.MaxMessageBytes) return 0;

    using var pipe = new NamedPipeClientStream(".", HookProtocol.PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
    pipe.Connect(150); // throws TimeoutException when the island is not running
    pipe.Write(line);
    pipe.WriteByte((byte)'\n');
    pipe.Flush();
}
catch (Exception ex)
{
    // Silent by default; see the header comment. stderr never reaches Claude's context on exit 0.
    if (Environment.GetEnvironmentVariable("ATOLL_HOOK_DEBUG") == "1") Console.Error.WriteLine(ex);
}
return 0;

internal static class PipeCheck
{
    private const int ERROR_FILE_NOT_FOUND = 2;

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WaitNamedPipe(string name, uint timeoutMs);

    /// <summary>False only when no island pipe exists; a busy pipe still counts as existing.</summary>
    public static bool Exists() =>
        WaitNamedPipe($@"\\.\pipe\{HookProtocol.PipeName}", 100) || Marshal.GetLastWin32Error() != ERROR_FILE_NOT_FOUND;
}

/// <summary>Finds the nearest ancestor process with a visible main window (terminal, VS Code, ...).</summary>
internal static class HostWindow
{
    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(nint snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(nint snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    public static long Find()
    {
        var parents = ParentMap();
        var pid = (uint)Environment.ProcessId;
        for (var hop = 0; hop < 12 && parents.TryGetValue(pid, out var parent) && parent != 0; hop++)
        {
            pid = parent;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (p.MainWindowHandle != 0) return p.MainWindowHandle;
            }
            catch (ArgumentException) { return 0; } // ancestor already exited
            catch (InvalidOperationException) { return 0; }
        }
        return 0;
    }

    private static Dictionary<uint, uint> ParentMap()
    {
        var map = new Dictionary<uint, uint>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == -1) return map;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                map[entry.th32ProcessID] = entry.th32ParentProcessID;
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }
}
