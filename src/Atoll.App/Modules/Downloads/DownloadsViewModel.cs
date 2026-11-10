using Atoll.Modules.Shelf;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Downloads;

/// <summary>
/// One activity for every download in progress: the file's name alone, or how many are running, always
/// with the live size. It is posted once and mutated as sizes change, so the island does not rebuild.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(SizeText), nameof(Summary), nameof(Detail), nameof(IsSeveral))]
    private int _count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(Summary), nameof(Detail))]
    private long _bytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Summary))]
    private string _name = "";

    public bool IsSeveral => Count > 1;

    /// <summary>"report.pdf", or "2 downloading".</summary>
    public string Title => Count > 1 ? $"{Count} downloading" : Name;

    public string SizeText => ShelfItem.FormatSize(Bytes);

    /// <summary>One line for the dot's tooltip and for screen readers.</summary>
    public string Summary => Count > 1 ? $"{Count} downloading · {SizeText}" : $"{Name} · {SizeText}";

    public string Detail => $"Downloading · {SizeText}";

    public void Update(string name, int count, long bytes)
    {
        Name = name;
        Count = count;
        Bytes = bytes;
    }
}

/// <summary>The "download finished" alert: the file's name and size, plus opening it and showing its folder.</summary>
public sealed partial class DownloadCompleteViewModel(string path, long bytes) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public string SizeText { get; } = ShelfItem.FormatSize(bytes);
    public string Detail => $"Downloaded · {SizeText}";

    [RelayCommand] private void Open() => ShelfItem.Start(Path);
    [RelayCommand] private void Reveal() => ShelfItem.RevealInFolder(Path);
}
