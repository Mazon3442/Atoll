namespace Atoll;

/// <summary>Minimal append-only log at %AppData%\Atolltoll.log; rolls over to atoll.log.old at 5 MB.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 5 * 1024 * 1024;
    private static int _writes;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Atoll", "atoll.log");

    public static void Error(string message, Exception? ex = null) => Write("ERROR", $"{message}{(ex is null ? "" : $": {ex}")}");

    public static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // A repeating error once grew this file to gigabytes; keep one previous file instead.
                if (_writes++ % 100 == 0 && new FileInfo(FilePath) is { Exists: true, Length: > MaxBytes })
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} {level} {text}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* logging must never crash the app */ }
        catch (UnauthorizedAccessException) { }
    }
}
