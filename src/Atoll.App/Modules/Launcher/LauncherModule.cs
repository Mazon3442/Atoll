using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Launcher;

/// <summary>One Start Menu shortcut. The path is a .lnk or .url opened through the shell.</summary>
public sealed record LauncherApp(string Name, string LaunchPath, string Source)
{
    /// <summary>The shortcut's own icon, read while scanning; null shows the initial instead.</summary>
    public System.Windows.Media.ImageSource? Icon { get; init; }

    public string Initial => Name.Length > 0 ? Name[..1].ToUpper(CultureInfo.CurrentCulture) : "?";
}

/// <summary>Reads the Start Menu without opening anything. Runs on a background thread.</summary>
internal static class LauncherScanner
{
    private static readonly string[] Noise =
    [
        "uninstall", "readme", "read me", "release notes", "documentation", "website", "help",
    ];

    public static IReadOnlyList<LauncherApp> Scan()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        var apps = new Dictionary<string, LauncherApp>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var programs = Path.Combine(root, "Programs");
            var baseDirectory = Directory.Exists(programs) ? programs : root;
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories)
                    .Where(file => file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(baseDirectory, file);
                if (relative.StartsWith("Startup" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || relative.Contains("Administrative Tools", StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = Path.GetFileNameWithoutExtension(file).Trim();
                if (name.Length == 0 || Noise.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
                var folder = Path.GetDirectoryName(relative);
                var source = string.IsNullOrEmpty(folder) || folder == "."
                    ? "Start Menu"
                    : folder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)[0];
                apps.TryAdd(name, new LauncherApp(name, file, source) { Icon = Atoll.Shell.ShellIcons.For(file) });
            }
        }
        return apps.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

/// <summary>Searchable Start Menu apps. The Games module supplies the second section of the Launcher tab.</summary>
public sealed partial class LauncherViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;

    public LauncherViewModel(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public ObservableCollection<LauncherApp> Apps { get; } = [];
    public ObservableCollection<LauncherApp> FilteredApps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch))]
    private string _searchText = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _status = "Looking for Start Menu apps…";

    public bool HasSearch => SearchText.Length > 0;
    public bool HasApps => Apps.Count > 0;
    public bool ShowEmpty => !IsScanning && FilteredApps.Count == 0;
    public string EmptyMessage => Apps.Count == 0 ? "No Start Menu apps found." : "No apps match that search.";

    partial void OnSearchTextChanged(string value) => Filter();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        Status = "Looking for Start Menu apps…";
        OnPropertyChanged(nameof(ShowEmpty));
        try
        {
            var apps = await Task.Run(LauncherScanner.Scan);
            Apps.Clear();
            foreach (var app in apps) Apps.Add(app);
            Status = Apps.Count == 0 ? "No Start Menu apps found." : $"{Apps.Count} apps";
            Filter();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the Start Menu", ex);
            Status = "Could not read the Start Menu.";
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(HasApps));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    private void Filter()
    {
        var query = SearchText.Trim();
        var wanted = Apps.Where(app => query.Length == 0
            || app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || app.Source.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        FilteredApps.Clear();
        foreach (var app in wanted) FilteredApps.Add(app);
        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>Fills the list without touching the Start Menu; used by rendered template tests.</summary>
    internal void SetAppsForTest(IEnumerable<LauncherApp> apps)
    {
        Apps.Clear();
        foreach (var app in apps) Apps.Add(app);
        Status = $"{Apps.Count} apps";
        Filter();
        OnPropertyChanged(nameof(HasApps));
    }

    [RelayCommand]
    private void Launch(LauncherApp? app)
    {
        if (app is null) return;
        try { using var process = Process.Start(new ProcessStartInfo(app.LaunchPath) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Could not launch {app.Name}", ex);
        }
    }

    [RelayCommand]
    private void OpenFirst() => Launch(FilteredApps.FirstOrDefault());
}

public sealed class LauncherModule : IIslandModule
{
    private LauncherViewModel? _vm;

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _vm = new LauncherViewModel(context.Dispatcher);
        _vm.RefreshCommand.Execute(null);
    }

    public void Dispose() { }
}
