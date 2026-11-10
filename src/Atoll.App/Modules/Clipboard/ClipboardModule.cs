using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atoll.Settings;
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

    /// <summary>PNG bytes for images, kept so the key and the saved copy share one encoding.</summary>
    public byte[]? Png { get; init; }

    public string[]? Files { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>One-line preview for the list.</summary>
    public string Preview => Kind switch
    {
        ClipKind.Text => Collapse(Text ?? ""),
        ClipKind.Image => $"Image · {Image?.PixelWidth}×{Image?.PixelHeight}",
        _ => Files is { Length: 1 } ? Path.GetFileName(Files[0]) : $"{Files?.Length} files",
    };

    public string Glyph => Kind switch { ClipKind.Text => "\uE8C8", ClipKind.Image => "\uEB9F", _ => "\uE8B7" };
    public string TimeText => Time.ToString("t");
    public bool IsImage => Kind == ClipKind.Image;

    /// <summary>Identity used to merge repeats: copying the same thing again moves it to the top.</summary>
    public string Key => _key ??= Kind switch
    {
        ClipKind.Text => "t:" + Text,
        ClipKind.Files => "f:" + string.Join("|", Files ?? []),
        // Hash the PNG the user actually copied, not a sampled row: two screenshots of the same size differ.
        _ => "i:" + (Png is { Length: > 0 } png
            ? Convert.ToHexString(SHA256.HashData(png))
            : ImageHash(Image).ToString(System.Globalization.CultureInfo.InvariantCulture)),
    };

    private string? _key;

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
        // Only for images captured before PNG bytes were kept (tests, embedded hosts). Real captures use the PNG hash.
        var stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
        var row = new byte[stride];
        image.CopyPixels(new Int32Rect(0, image.PixelHeight / 2, image.PixelWidth, 1), row, stride, 0);
        var hash = new HashCode();
        hash.AddBytes(row);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Most-recent-first list with repeats merged and the same caps as the saved file: 100 rows or 25 MB.
/// Pure logic; UI thread only. Images and text are saved when a store is attached; file-drop rows stay
/// in this session (the shelf already keeps paths).
/// </summary>
public sealed class ClipboardHistory
{
    public const int MaxItems = ClipboardStore.MaxItems;
    public const long MaxBytes = ClipboardStore.MaxBytes;

    public ObservableCollection<ClipItem> Items { get; } = [];

    private ClipboardStore? _store;

    public bool IsPersistent => _store is not null;

    public void Add(ClipItem item)
    {
        if (Items.FirstOrDefault(i => i.Key == item.Key) is { } same) Items.Remove(same);
        Items.Insert(0, item);
        Trim();
        Persist();
    }

    public void Remove(ClipItem item)
    {
        if (Items.Remove(item)) Persist();
    }

    /// <summary>Clear: drops the in-memory rows and the encrypted file.</summary>
    public void Clear()
    {
        Items.Clear();
        _store?.Wipe();
    }

    /// <summary>Turns saving on and brings back the previous session's rows, newest first.</summary>
    public void Attach(ClipboardStore store, Func<ClipboardRecord, ClipItem?> restore)
    {
        if (ReferenceEquals(_store, store)) return;
        _store?.Dispose();
        _store = store;
        foreach (var record in store.Load())
        {
            if (restore(record) is not { } item || Items.Any(i => i.Key == item.Key)) continue;
            Items.Add(item);
        }
        Trim();
    }

    /// <summary>Turns saving off after a flush; the file is left as it was. Used when the module stops.</summary>
    public void Detach()
    {
        _store?.Dispose();
        _store = null;
    }

    /// <summary>The user turned the setting off: wipe both the file and this session's rows.</summary>
    public void Reset()
    {
        Items.Clear();
        _store?.Wipe();
        _store?.Dispose();
        _store = null;
    }

    private void Trim()
    {
        long total = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            var size = ByteSize(Items[i]);
            if (i >= MaxItems || size > MaxBytes || total + size > MaxBytes)
            {
                Items.RemoveAt(i--);
                continue;
            }
            total += size;
        }
    }

    private static long ByteSize(ClipItem item) => ToRecord(item)?.ByteSize ?? 0;

    private void Persist() =>
        _store?.Save(Items.Select(ToRecord).OfType<ClipboardRecord>().ToList());

    /// <summary>Only text and images are saved. Null means this row is not part of the saved history.</summary>
    private static ClipboardRecord? ToRecord(ClipItem item) => item.Kind switch
    {
        ClipKind.Text when !string.IsNullOrEmpty(item.Text) => new ClipboardRecord
        {
            Kind = ClipboardRecordKind.Text,
            Text = item.Text,
            Time = item.Time,
        },
        ClipKind.Image when item.Png is { Length: > 0 } png => new ClipboardRecord
        {
            Kind = ClipboardRecordKind.Image,
            Png = png,
            Time = item.Time,
        },
        _ => null,
    };
}

public sealed partial class ClipboardViewModel(ClipboardHistory history) : ObservableObject
{
    public ObservableCollection<ClipItem> Items => history.Items;
    public bool IsEmpty => Items.Count == 0;

    /// <summary>True only while the user's setting is on; the panel explains what is (not) happening.</summary>
    public bool CaptureEnabled { get; private set; }
    public bool SavedOnDisk => history.IsPersistent;

    public string EmptyTitle => CaptureEnabled ? "Things you copy show up here" : "Clipboard history is off";

    public string EmptyNote => !CaptureEnabled
        ? "Turn it on in Settings › General. Nothing is captured until you do."
        : SavedOnDisk
            ? "Saved on this PC, encrypted for your Windows account. Password managers are skipped."
            : "Kept in memory only. Password managers are skipped.";

    public void Configure(bool enabled)
    {
        CaptureEnabled = enabled;
        OnPropertyChanged(nameof(CaptureEnabled));
        OnPropertyChanged(nameof(SavedOnDisk));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyNote));
    }

    public void Changed()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SavedOnDisk));
        OnPropertyChanged(nameof(EmptyNote));
    }

    [RelayCommand]
    private void Clear()
    {
        history.Clear();
        Changed();
    }
}

/// <summary>
/// Recent copies (text, images, files). Capture is opt-in: while the setting is off nothing is read or
/// written. With it on, text and images survive restarts in <see cref="ClipboardStore"/>; file drops stay
/// in memory. Content that the source marks as private is skipped: password managers set
/// "ExcludeClipboardContentFromMonitorProcessing" or "CanIncludeInClipboardHistory" = 0.
/// </summary>
public sealed class ClipboardModule : IIslandModule
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly nint HWND_MESSAGE = -3;
    private const int Attempts = 5;
    private const int RetryDelayMs = 40;
    private const int SelfWriteWindowMs = 2000;

    private readonly ClipboardHistory _history = new();
    private readonly ClipboardViewModel _vm;
    private HwndSource? _listener;
    private ModuleContext? _context;
    private SettingsService? _settings;
    private bool _capture;
    private bool _readQueued;
    private string? _selfWrittenKey;
    private long _selfWriteUntil;

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
        _vm.Configure(enabled: false);
    }

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _settings = context.Settings;
        _settings.Changed += ApplySettings;
        // Message-only window: receives clipboard notifications, never shown.
        _listener = new HwndSource(new HwndSourceParameters("ATOLL clipboard") { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
        _listener.AddHook(WndProc);
        if (!AddClipboardFormatListener(_listener.Handle))
            Log.Error($"Clipboard history unavailable (error {Marshal.GetLastWin32Error()})");
        ApplySettings();
    }

    /// <summary>Capture follows the setting live; turning it off wipes both the file and the open list.</summary>
    private void ApplySettings()
    {
        var enabled = _settings?.Current.Clipboard.HistoryEnabled == true;
        if (enabled != _capture)
        {
            _capture = enabled;
            if (enabled)
            {
                if (OperatingSystem.IsWindows()) _history.Attach(new ClipboardStore(), Restore);
            }
            else _history.Reset();
        }
        _vm.Configure(_capture);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && !_readQueued && _capture)
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
        if (!_capture) return;

        var item = Retry(ReadOne);
        if (item is null) return;

        // Our own write echoes back as a clipboard update. It is already in the list, so skip it
        // instead of moving it to the top every time the user re-copies from history.
        if (item.Key == _selfWrittenKey && Environment.TickCount64 < _selfWriteUntil)
        {
            _selfWrittenKey = null;
            return;
        }
        _selfWrittenKey = null;
        _history.Add(item);
    }

    private ClipItem? ReadOne()
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
            var png = EncodePng(image);
            // A single huge image would crowd out everything else in the history and its file.
            if (png is null || png.LongLength > ClipboardStore.MaxImageBytes) return null;
            image.Freeze();
            return new ClipItem(ClipKind.Image, Copy, Remove) { Image = image, Png = png };
        }
        return null;
    }

    private static byte[]? EncodePng(BitmapSource image)
    {
        try
        {
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            Log.Error($"Clipboard image could not be encoded as PNG ({ex.GetType().Name})");
            return null;
        }
    }

    private ClipItem? Restore(ClipboardRecord record)
    {
        try
        {
            if (record.Kind == ClipboardRecordKind.Text && !string.IsNullOrEmpty(record.Text))
                return new ClipItem(ClipKind.Text, Copy, Remove) { Text = record.Text, Time = record.Time.LocalDateTime };
            if (record.Kind == ClipboardRecordKind.Image && record.Png is { Length: > 0 } png)
            {
                using var stream = new MemoryStream(png);
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                image.Freeze();
                return new ClipItem(ClipKind.Image, Copy, Remove) { Image = image, Png = png, Time = record.Time.LocalDateTime };
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            Log.Error($"A saved clipboard item could not be restored ({ex.GetType().Name})");
        }
        return null;
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
        // The update that follows is our own; remember what we wrote so ReadClipboard ignores the echo.
        _selfWrittenKey = item.Key;
        _selfWriteUntil = Environment.TickCount64 + SelfWriteWindowMs;
        return null;
    });

    private void Remove(ClipItem item) => _history.Remove(item);

    /// <summary>
    /// The clipboard is briefly locked while another app writes it. Retries are bounded to
    /// <see cref="Attempts"/> opens so a stuck owner cannot stall the UI thread.
    /// </summary>
    private static T? Retry<T>(Func<T?> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException) when (attempt < Attempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (Exception ex) when (ex is ExternalException or OutOfMemoryException)
            {
                Log.Error("Clipboard access failed", ex);
                return default;
            }
        }
    }

    public void Dispose()
    {
        if (_settings is not null) _settings.Changed -= ApplySettings;
        _capture = false;
        _history.Detach();
        if (_listener is null) return;
        RemoveClipboardFormatListener(_listener.Handle);
        _listener.RemoveHook(WndProc);
        _listener.Dispose();
    }
}
