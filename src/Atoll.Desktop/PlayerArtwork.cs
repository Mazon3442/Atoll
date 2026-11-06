using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Atoll.Desktop;

internal static class PlayerArtwork
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private const int MaxBytes = 4 * 1024 * 1024;
    public static async Task<Bitmap?> LoadMusicAsync()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var directory = Path.Combine(Path.GetTempPath(), "atoll-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "cover");
        try
        {
            const string script = """
                on run argv
                    tell application "Music"
                        if not (exists artwork 1 of current track) then return ""
                        set coverData to raw data of artwork 1 of current track
                    end tell
                    set target to open for access POSIX file (item 1 of argv) with write permission
                    try
                        set eof target to 0
                        write coverData to target
                        close access target
                    on error
                        try
                            close access target
                        end try
                    end try
                end run
                """;
            await Commands.RunAsync("osascript", "-e", script, path);
            return await LoadAsync(new Uri(path).AbsoluteUri);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return null; }
        finally
        {
            try { File.Delete(path); Directory.Delete(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    public static async Task<Bitmap?> LoadAsync(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("file" or "https" or "http")) return null;
        try
        {
            byte[] bytes;
            if (uri.IsFile)
            {
                if (!File.Exists(uri.LocalPath) || new FileInfo(uri.LocalPath).Length > MaxBytes) return null;
                bytes = await File.ReadAllBytesAsync(uri.LocalPath);
            }
            else
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
                using var input = await response.Content.ReadAsStreamAsync(); using var memory = new MemoryStream();
                var buffer = new byte[8192]; int read;
                while ((read = await input.ReadAsync(buffer)) > 0) { if (memory.Length + read > MaxBytes) return null; memory.Write(buffer, 0, read); }
                bytes = memory.ToArray();
            }
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 25_000_000) return null;
            using var encoded = new MemoryStream(bytes); return Bitmap.DecodeToWidth(encoded, 240);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or ArgumentException) { return null; }
    }
}
