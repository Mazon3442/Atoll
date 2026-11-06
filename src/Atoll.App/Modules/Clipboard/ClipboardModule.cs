using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfClipboard = System.Windows.Clipboard;

namespace Atoll.Modules.Clipboard;

public enum ClipKind
{
    Text,
    Image,
    Files,
}

public sealed partial class ClipItem(ClipKind kind, Action<ClipItem> copy, Action<ClipItem> remove) : ObservableObject
{
    public ClipKind Kind { get; } = kind;
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
    public string[]? Files { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>One-line preview for the list.</summary>
    public string Preview => Kind switch
    {
        ClipKind.Text => Collapse(Text ?? ""),
        ClipKind.Image => $"Image · {Image?.PixelWidth}×{Image?.PixelHeight}",
        _ => Files is { Length: 1 } ? Path.GetFileName(Files[0]) : $"{Files?.Length} files",
    };

    public string Glyph => Kind switch { ClipKind.Text => "", ClipKind.Image => "", _ => "" };
    public string TimeText => Time.ToString("t");
    public bool IsImage => Kind == ClipKind.Image;

    /// <summary>Identity used to merge repeats: copying the same thing again moves it to the top.</summary>
    public string Key => Kind switch
    {
        ClipKind.Text => "t:" + Text,
        ClipKind.Files => "f:" + string.Join("|", Files ?? []),
        _ => $"i:{Image?.PixelWidth}x{Image?.PixelHeight}:{ImageHash(Image)}",
    };

    [RelayCommand]
    private void Copy() => copy(this);

    [RelayCommand]
    private void Remove() => remove(this);

    private static string Collapse(string s)
    {
        var line = string.Join(" ", s.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 200 ? line[..199] + "…" : line;
    }

    private static int ImageHash(BitmapSource? image)
    {
        if (image is null) return 0;
        // Sample one row from the middle: cheap and good enough to spot the same screenshot copied twice.
        var stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
        var row = new byte[stride];
        image.CopyPixels(new Int32Rect(0, image.PixelHeight / 2, image.PixelWidth, 1), row, stride, 0);
        var hash = new HashCode();
        hash.AddBytes(row);
        return hash.ToHashCode();
    }
}

/// <summary>Most-recent-first list with repeats merged and a size cap. Pure logic; UI thread only.</summary>
public sealed class ClipboardHistory
{
    public const int MaxItems = 25;
    public const int MaxImages = 3; // full-size images are kept so they can be pasted again; keep few

    public ObservableCollection<ClipItem> Items { get; } = [];

    public void Add(ClipItem item)
    {
        if (Items.FirstOrDefault(i => i.Key == item.Key) is { } same) Items.Remove(same);
        Items.Insert(0, item);
        foreach (var extra in Items.Where(i => i.IsImage).Skip(MaxImages).ToList()) Items.Remove(extra);
        while (Items.Count > MaxItems) Items.RemoveAt(Items.Count - 1);
    }
}

public sealed partial class ClipboardViewModel(ClipboardHistory history) : ObservableObject
{
    public ObservableCollection<ClipItem> Items => history.Items;
    public bool IsEmpty => Items.Count == 0;

    public void Changed() => OnPropertyChanged(nameof(IsEmpty));

    [RelayCommand]
    private void Clear()
    {
        Items.Clear();
        Changed();
    }
}

/// <summary>
/// Recent copies (text, images, files), in memory only. Skips content that the source marks as
/// private: password managers set "ExcludeClipboardContentFromMonitorProcessing" or
/// "CanIncludeInClipboardHistory" = 0.
/// </summary>
public sealed class ClipboardModule : IIslandModule
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly nint HWND_MESSAGE = -3;

    private readonly ClipboardHistory _history = new();
    private readonly ClipboardViewModel _vm;
    private HwndSource? _listener;
    private ModuleContext? _context;
    private bool _readQueued;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    public ClipboardModule()
    {
        _vm = new ClipboardViewModel(_history);
        _history.Items.CollectionChanged += (_, _) => _vm.Changed();
    }

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        // Message-only window: receives clipboard notifications, never shown.
        _listener = new HwndSource(new HwndSourceParameters("ATOLL clipboard") { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
        _listener.AddHook(WndProc);
        if (!AddClipboardFormatListener(_listener.Handle))
            Log.Error($"Clipboard history unavailable (error {Marshal.GetLastWin32Error()})");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && !_readQueued)
        {
            // Let the copying app finish putting all its formats on the clipboard first.
            _readQueued = true;
            _context?.Dispatcher.BeginInvoke(DispatcherPriority.Background, ReadClipboard);
        }
        return 0;
    }

    private void ReadClipboard()
    {
        _readQueued = false;
        var item = Retry(() =>
        {
            var data = WpfClipboard.GetDataObject();
            if (data is null || IsPrivate(data)) return null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                return new ClipItem(ClipKind.Files, Copy, Remove) { Files = files };
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string { Length: > 0 } text
                && !string.IsNullOrWhiteSpace(text))
                return new ClipItem(ClipKind.Text, Copy, Remove) { Text = text };
            if (WpfClipboard.ContainsImage() && WpfClipboard.GetImage() is { } image)
            {
                image.Freeze();
                return new ClipItem(ClipKind.Image, Copy, Remove) { Image = image };
            }
            return null;
        });
        if (item is not null) _history.Add(item);
    }

    private static bool IsPrivate(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore"))
            return true;
        return data.GetDataPresent("CanIncludeInClipboardHistory")
               && data.GetData("CanIncludeInClipboardHistory") is MemoryStream { Length: >= 4 } flag
               && BitConverter.ToInt32(flag.ToArray(), 0) == 0;
    }

    private void Copy(ClipItem item) => Retry<object?>(() =>
    {
        switch (item.Kind)
        {
            case ClipKind.Text: WpfClipboard.SetText(item.Text!); break;
            case ClipKind.Image: WpfClipboard.SetImage(item.Image!); break;
            default:
                var list = new StringCollection();
                list.AddRange(item.Files!);
                WpfClipboard.SetFileDropList(list);
                break;
        }
        return null; // the clipboard update brings it back to the top of the list
    });

    private void Remove(ClipItem item) => _history.Items.Remove(item);

    /// <summary>The clipboard is briefly locked while another app writes it; try a few times.</summary>
    private static T? Retry<T>(Func<T?> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(40);
            }
            catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
            {
                Log.Error("Clipboard access failed", ex);
                return default;
            }
        }
    }

    public void Dispose()
    {
        if (_listener is null) return;
        RemoveClipboardFormatListener(_listener.Handle);
        _listener.RemoveHook(WndProc);
        _listener.Dispose();
    }
}
