using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Atoll.Modules.Palette;

namespace Atoll.Shell;

/// <summary>
/// A memory-only list of file and folder names under a few personal folders, so the palette can find them by name.
/// It is built once, on a background thread, the first time the palette opens, and never written to disk.
/// </summary>
internal sealed class FileIndex
{
    private const int MaxEntries = 60_000, MaxDepth = 5;
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".vs", "bin", "obj", "AppData", "$Recycle.Bin", "System Volume Information", ".nuget",
    };

    private volatile IReadOnlyList<IndexedPath> _entries = [];
    private int _started;

    public bool Ready { get; private set; }

    /// <summary>The folders searched when the person has not chosen others.</summary>
    public static IEnumerable<string> DefaultFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
    ];

    public void EnsureStarted(IEnumerable<string> folders)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var roots = folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _ = Task.Run(() =>
        {
            try
            {
                var list = new List<IndexedPath>();
                foreach (var root in roots)
                {
                    if (list.Count >= MaxEntries) break;
                    Walk(root, 0, list);
                }
                _entries = list;
            }
            catch (Exception ex) { Log.Info($"The file index could not be built: {ex.Message}"); }
            finally { Ready = true; }
        });
    }

    public IReadOnlyList<IndexedPath> Search(string query, int max) => FileNameSearch.Rank(_entries, query, max);

    private static void Walk(string folder, int depth, List<IndexedPath> into)
    {
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if (into.Count >= MaxEntries) return;
                if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || Skipped.Contains(entry.Name)) continue;
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                if (!isDirectory && entry.Extension.Equals(".ini", StringComparison.OrdinalIgnoreCase)) continue;
                into.Add(new IndexedPath(entry.Name, entry.FullName, isDirectory));
                if (isDirectory && depth < MaxDepth && (entry.Attributes & FileAttributes.ReparsePoint) == 0) Walk(entry.FullName, depth + 1, into);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
    }
}

/// <summary>
/// Exchange rates for the palette's currency converter. One request to a free, key-less service the first time a
/// conversion is typed, then kept in memory for twelve hours. Only the base code is sent (never what was typed).
/// </summary>
internal sealed class CurrencyRates
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private IReadOnlyDictionary<string, double>? _rates;
    private DateTimeOffset _fetched;
    private int _fetching;

    public IReadOnlyDictionary<string, double>? Current => _rates is not null && DateTimeOffset.Now - _fetched < MaxAge ? _rates : null;

    /// <summary>Starts a fetch if there are no fresh rates; <paramref name="done"/> runs on a background thread after.</summary>
    public void EnsureFetched(Action done)
    {
        if (Current is not null || Interlocked.Exchange(ref _fetching, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await Http.GetAsync("https://open.er-api.com/v6/latest/USD");
                response.EnsureSuccessStatusCode();
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                var rates = json.RootElement.GetProperty("rates").EnumerateObject()
                    .Where(property => property.Value.ValueKind == JsonValueKind.Number)
                    .ToDictionary(property => property.Name.ToUpperInvariant(), property => property.Value.GetDouble());
                _rates = rates;
                _fetched = DateTimeOffset.Now;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException)
            {
                Log.Info($"Exchange rates could not be loaded: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _fetching, 0);
                done();
            }
        });
    }
}

/// <summary>Opens things for palette results. Kept apart from the window so each action is one obvious line.</summary>
internal static class PaletteActions
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open {target}", ex); }
    }

    /// <summary>The program a Start Menu shortcut points at, or the shortcut itself when it cannot be read.</summary>
    public static string ShortcutTarget(string shortcut)
    {
        if (!shortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return shortcut;
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } type) return shortcut;
            dynamic shell = Activator.CreateInstance(type)!;
            string target = shell.CreateShortcut(shortcut).TargetPath;
            return target.Length > 0 && File.Exists(target) ? target : shortcut;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return shortcut;
        }
    }

    public static void ShowInFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not show {path}", ex); }
    }

    public static void Elevated(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception) { /* the person declined the prompt */ }
    }

    public static void RunShell(ShellPhrase.Shell shell, string command)
    {
        var start = shell == ShellPhrase.Shell.Cmd
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/k", command } }
            : new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoExit", "-Command", command } };
        start.UseShellExecute = true;
        try { Process.Start(start); }
        catch (Exception ex) { Log.Error("Could not start the shell", ex); }
    }

    public static void WebSearch(string query) =>
        Open("https://duckduckgo.com/?q=" + Uri.EscapeDataString(query));
}
