using System.Text;
using Atoll.Modules.Clipboard;

namespace Atoll.Tests;

/// <summary>A fact that needs Windows DPAPI. xUnit v2 skips it elsewhere before discovery.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI only";
    }
}

public class ClipboardStoreTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "atoll-clipboard-tests", Guid.NewGuid().ToString("N"));

    private static ClipboardRecord Text(string text, int age = 0) => new()
    {
        Kind = ClipboardRecordKind.Text,
        Text = text,
        Time = DateTimeOffset.Now.AddSeconds(-age),
    };

    private static ClipboardRecord Image(byte[] png, int age = 0) => new()
    {
        Kind = ClipboardRecordKind.Image,
        Png = png,
        Time = DateTimeOffset.Now.AddSeconds(-age),
    };

    private static void Cleanup(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public void Caps_match_the_documented_limits()
    {
        Assert.Equal(100, ClipboardStore.MaxItems);
        Assert.Equal(25L * 1024 * 1024, ClipboardStore.MaxBytes);
        Assert.Equal(8L * 1024 * 1024, ClipboardStore.MaxImageBytes);
        Assert.EndsWith(Path.Combine("Atoll", "clipboard.bin"), ClipboardStore.DefaultPath, StringComparison.Ordinal);
        Assert.DoesNotContain("Roaming", ClipboardStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public void Text_and_png_round_trip_newest_first()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "clipboard.bin");
        try
        {
            var png = new byte[] { 137, 80, 78, 71, 1, 2, 3 };
            using (var store = new ClipboardStore(file))
            {
                store.Save([Text("newer"), Image(png), Text("older", 5)]);
                store.Flush();
            }

            using var reopened = new ClipboardStore(file);
            var loaded = reopened.Load();
            Assert.Equal([ClipboardRecordKind.Text, ClipboardRecordKind.Image, ClipboardRecordKind.Text], loaded.Select(r => r.Kind));
            Assert.Equal(["newer", null, "older"], loaded.Select(r => r.Text));
            Assert.True(png.SequenceEqual(loaded[1].Png!));
        }
        finally { Cleanup(directory); }
    }

    [WindowsFact]
    public void The_file_is_encrypted_and_carries_the_envelope()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "clipboard.bin");
        try
        {
            const string secret = "correct horse battery staple";
            using (var store = new ClipboardStore(file))
            {
                store.Save([Text(secret)]);
                store.Flush();
            }

            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.AsSpan(0, 8).SequenceEqual("ATOLLCLP"u8));
            Assert.Equal(1, BitConverter.ToInt32(bytes, 8));
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0);
            Assert.True(bytes.AsSpan().IndexOf("Text"u8) < 0);
        }
        finally { Cleanup(directory); }
    }

    [WindowsFact]
    public void A_corrupt_file_is_deleted_and_loads_empty()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "clipboard.bin");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            using var store = new ClipboardStore(file);
            Assert.Empty(store.Load());
            Assert.False(File.Exists(file));
        }
        finally { Cleanup(directory); }
    }

    [WindowsFact]
    public void A_wrong_magic_is_deleted_and_loads_empty()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "clipboard.bin");
        try
        {
            Directory.CreateDirectory(directory);
            var bytes = new byte[64];
            "NOTATOLL"u8.CopyTo(bytes);
            File.WriteAllBytes(file, bytes);
            using var store = new ClipboardStore(file);
            Assert.Empty(store.Load());
            Assert.False(File.Exists(file));
        }
        finally { Cleanup(directory); }
    }

    [WindowsFact]
    public void A_damaged_ciphertext_is_deleted_and_loads_empty()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "clipboard.bin");
        try
        {
            using (var store = new ClipboardStore(file))
            {
                store.Save([Text("before damage")]);
                store.Flush();
            }

            var bytes = File.ReadAllBytes(file);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(file, bytes);

            using var reopened = new ClipboardStore(file);
            Assert.Empty(reopened.Load());
            Assert.False(File.Exists(file));
        }
        finally { Cleanup(directory); }
    }

    [WindowsFact]
    public void Evicts_the_oldest_rows_by_count()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "clipboard.bin");
        try
        {
            using (var store = new ClipboardStore(file, maxItems: 3))
            {
                store.Save([Text("one"), Text("two"), Text("three"), Text("four"), Text("five")]);
                store.Flush();
            }

            using var reopened = new ClipboardStore(file, maxItems: 3);
            Assert.Equal(["one", "two", "three"], reopened.Load().Select(r => r.Text));
        }
        finally { Cleanup(directory); }
    }

    [WindowsFact]
    public void Evicts_the_oldest_rows_by_size()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "clipboard.bin");
        try
        {
            // Six bytes of text each, a ten-byte budget: the two newest fit and the third is evicted.
            using (var store = new ClipboardStore(file, maxBytes: 10))
            {
                store.Save([Text("aaaaa"), Text("bbbbb"), Text("ccccc")]);
                store.Flush();
            }

            using var reopened = new ClipboardStore(file, maxBytes: 10);
            Assert.Equal(["aaaaa", "bbbbb"], reopened.Load().Select(r => r.Text));
        }
        finally { Cleanup(directory); }
    }

    [WindowsFact]
    public void A_row_larger_than_the_budget_is_skipped()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "clipboard.bin");
        try
        {
            using (var store = new ClipboardStore(file, maxBytes: 4))
            {
                store.Save([Text("too big"), Text("ok")]);
                store.Flush();
            }

            using var reopened = new ClipboardStore(file, maxBytes: 4);
            Assert.Equal(["ok"], reopened.Load().Select(r => r.Text));
        }
        finally { Cleanup(directory); }
    }

    [WindowsFact]
    public void Wipe_deletes_the_file_and_any_queued_save()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "clipboard.bin");
        try
        {
            using var store = new ClipboardStore(file);
            store.Save([Text("gone")]); // still inside the debounce window
            store.Wipe();
            Assert.False(File.Exists(file));
            Assert.Empty(store.Load());
        }
        finally { Cleanup(directory); }
    }

    [WindowsFact]
    public void Saving_nothing_removes_the_previous_file()
    {
        var directory = NewDirectory();
        var file = Path.Combine(directory, "clipboard.bin");
        try
        {
            using var store = new ClipboardStore(file);
            store.Save([Text("first")]);
            store.Flush();
            Assert.True(File.Exists(file));
            store.Save([]);
            store.Flush();
            Assert.False(File.Exists(file));
        }
        finally { Cleanup(directory); }
    }
}
