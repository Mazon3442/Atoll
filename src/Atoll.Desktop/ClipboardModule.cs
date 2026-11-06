using System.Collections.ObjectModel;
using Atoll.Modules;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Desktop;

public sealed partial class ClipboardEntry(string text, Action<ClipboardEntry> copy, Action<ClipboardEntry> remove) : ObservableObject
{
    public string Text { get; } = text;
    public Bitmap? Image { get; init; }
    public string[]? Files { get; init; }
    public string Key { get; init; } = "text:" + text;
    public string Preview => Image is { } bitmap ? $"Image · {bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}"
        : Files is { } files ? string.Join(", ", files.Select(Path.GetFileName))
        : string.Join(" ", Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    [RelayCommand] private void Copy() => copy(this);
    [RelayCommand] private void Remove() => remove(this);

    internal Bitmap CopyImageForClipboard()
    {
        using var bytes = new MemoryStream();
        (Image ?? throw new InvalidOperationException("This entry is not an image.")).Save(bytes);
        bytes.Position = 0;
        return new Bitmap(bytes);
    }
}

public sealed partial class ClipboardModule : ObservableObject, IIslandModule
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;
    private bool _disposed;
    private string? _last;
    [ObservableProperty] private string _status = "History stays in memory. Private clipboard formats are skipped. Desktop permissions may limit access.";
    public ObservableCollection<ClipboardEntry> Items { get; } = [];
    public object Panel => this;
    private static Avalonia.Input.Platform.IClipboard? Clipboard => (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
    public void Start(ModuleContext context) { _timer.Tick += OnTick; _timer.Start(); }
    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy || _disposed || Clipboard is not { } clipboard) return;
        _busy = true;
        try
        {
            var formats = await clipboard.GetDataFormatsAsync();
            if (IsPrivate(formats.Select(f => f.Identifier))) return;
            ClipboardEntry? entry = null;
            if (formats.Contains(DataFormat.File))
            {
                var files = (await clipboard.TryGetFilesAsync())?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
                if (files is { Length: > 0 }) entry = new ClipboardEntry("", Copy, Remove) { Files = files, Key = "files:" + string.Join("\n", files) };
            }
            else if (formats.Contains(DataFormat.Bitmap))
            {
                var bitmap = await clipboard.TryGetBitmapAsync();
                if (bitmap is not null)
                {
                    if ((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height > 8_000_000) { bitmap.Dispose(); return; }
                    using var bytes = new MemoryStream(); bitmap.Save(bytes);
                    entry = new ClipboardEntry("", Copy, Remove) { Image = bitmap, Key = "image:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))) };
                }
            }
            else
            {
                var text = await clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(text) && text.Length <= 1_000_000) entry = new ClipboardEntry(text, Copy, Remove);
            }
            if (entry is null) return;
            if (_disposed || entry.Key == _last) { entry.Image?.Dispose(); return; }
            _last = entry.Key;
            if (Items.FirstOrDefault(i => i.Key == entry.Key) is { } existing) Remove(existing);
            Items.Insert(0, entry);
            foreach (var extra in Items.Where(i => i.Image is not null).Skip(3).ToList()) Remove(extra);
            while (Items.Count > 25) Remove(Items[^1]);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException) { Status = "Clipboard is unavailable. Some desktops allow clipboard access only while ATOLL is focused."; }
        finally { _busy = false; }
    }
    private async void Copy(ClipboardEntry entry)
    {
        try
        {
            if (Clipboard is not { } clipboard) return;
            _last = entry.Key;
            // Clipboard providers can read lazily, after this history entry has been removed.
            if (entry.Image is not null) await clipboard.SetBitmapAsync(entry.CopyImageForClipboard());
            else if (entry.Files is { } paths)
            {
                var provider = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.StorageProvider;
                if (provider is null) return;
                var files = new List<IStorageItem>();
                foreach (var path in paths)
                {
                    IStorageItem? item = Directory.Exists(path) ? await provider.TryGetFolderFromPathAsync(new Uri(path)) : await provider.TryGetFileFromPathAsync(new Uri(path));
                    if (item is not null) files.Add(item);
                }
                await clipboard.SetFilesAsync(files);
            }
            else await clipboard.SetTextAsync(entry.Text);
        }
        catch (Exception ex) { Status = "Could not copy this item. Try again while ATOLL is focused."; Log.Error("Clipboard copy failed", ex); }
    }
    internal static bool IsPrivate(IEnumerable<string> formats) => formats.Any(f => f.Contains("ExcludeClipboardContentFromMonitorProcessing", StringComparison.OrdinalIgnoreCase) || f.Contains("CanIncludeInClipboardHistory", StringComparison.OrdinalIgnoreCase) || f.Contains("password", StringComparison.OrdinalIgnoreCase) || f.Contains("concealed", StringComparison.OrdinalIgnoreCase) || f.Contains("transient", StringComparison.OrdinalIgnoreCase));
    private void Remove(ClipboardEntry entry) { Items.Remove(entry); entry.Image?.Dispose(); }
    [RelayCommand] private void Clear() { foreach (var entry in Items.ToList()) Remove(entry); }
    public void Dispose() { _disposed = true; _timer.Stop(); _timer.Tick -= OnTick; Clear(); }
}
