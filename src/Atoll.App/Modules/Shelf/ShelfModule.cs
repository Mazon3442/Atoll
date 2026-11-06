using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
#if AVALONIA
using ImageSource = Avalonia.Media.IImage;
#else
using System.Windows.Media;
#endif
using Atoll.Activities;
using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Shelf;

/// <summary>
/// The shelf's list of paths, saved to %AppData%\Atoll\shelf.json. Only paths are kept: files are never
/// copied, moved or deleted by ATOLL.
/// </summary>
public sealed class ShelfStore(string path)
{
    public const int MaxItems = 50;

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Atoll", "shelf.json");

    public List<string> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Shelf list could not be read; starting empty", ex);
            return [];
        }
    }

    public void Save(IEnumerable<string> paths)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(paths.ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Shelf list could not be saved", ex);
        }
    }

    /// <summary>Newest first, no duplicates (case-insensitive), capped at <see cref="MaxItems"/>.</summary>
    public static List<string> Merge(IEnumerable<string> existing, IEnumerable<string> added)
    {
        var result = new List<string>();
        foreach (var p in added.Concat(existing))
        {
            if (result.Count >= MaxItems) break;
            if (!result.Contains(p, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) result.Add(p);
        }
        return result;
    }
}

public sealed partial class ShelfItem(string path, Action<ShelfItem> remove) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;
    public ImageSource? Icon { get; } = ShellIcons.For(path);

    [ObservableProperty] private bool _exists = File.Exists(path) || Directory.Exists(path);

    public string Detail => !Exists ? "Missing (moved or deleted)"
        : Directory.Exists(Path) ? "Folder"
        : FormatSize(new FileInfo(Path).Length);

    public void Refresh()
    {
        Exists = File.Exists(Path) || Directory.Exists(Path);
        OnPropertyChanged(nameof(Detail));
    }

    [RelayCommand]
    private void Open()
    {
        if (Exists) Start(Path);
    }

    [RelayCommand]
    private void Reveal()
    {
        if (!Exists) return;
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path}\"") { UseShellExecute = true });
        else Start(Directory.Exists(Path) ? Path : System.IO.Path.GetDirectoryName(Path)!);
    }

    [RelayCommand]
    private void Remove() => remove(this);

    internal static void Start(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open {target}", ex); }
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}

public sealed partial class ShelfViewModel(Action<string[]> add, Action clear) : ObservableObject
{
    public ObservableCollection<ShelfItem> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;
    public string CountText => Items.Count == 1 ? "1 item" : $"{Items.Count} items";

    public void Changed()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand]
    private void AddFiles(string[] paths) => add(paths);

    [RelayCommand]
    private void Clear() => clear();
}

/// <summary>Drop files on ATOLL to keep them handy; drag them back out to any app.</summary>
public sealed class ShelfModule : IIslandModule
{
    private readonly ShelfStore _store = new(ShelfStore.DefaultPath);
    private readonly ShelfViewModel _vm;
    private ModuleContext? _context;

    public ShelfModule() => _vm = new ShelfViewModel(Add, Clear);

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        Show(_store.Load());
    }

    private void Add(string[] paths)
    {
        var merged = ShelfStore.Merge(_vm.Items.Select(i => i.Path), paths);
        Show(merged);
        _store.Save(merged);
        var n = paths.Length;
        _context?.Post("added", new IndicatorViewModel("", n == 1 ? "Added to shelf" : $"Added {n} items to shelf",
            n == 1 ? Path.GetFileName(paths[0]) : null), ActivityPriority.High, TimeSpan.FromSeconds(2));
    }

    private void Remove(ShelfItem item)
    {
        _vm.Items.Remove(item);
        _vm.Changed();
        _store.Save(_vm.Items.Select(i => i.Path));
    }

    private void Clear()
    {
        _vm.Items.Clear();
        _vm.Changed();
        _store.Save([]);
    }

    private void Show(IEnumerable<string> paths)
    {
        _vm.Items.Clear();
        foreach (var p in paths) _vm.Items.Add(new ShelfItem(p, Remove));
        _vm.Changed();
    }

    public void Dispose() { }
}
