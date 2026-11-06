namespace Island;

/// <summary>Minimal append-only log at %AppData%\Island\island.log.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "island.log");

    public static void Error(string message, Exception? ex = null) => Write("ERROR", $"{message}{(ex is null ? "" : $": {ex}")}");

    public static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} {level} {text}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* logging must never crash the app */ }
        catch (UnauthorizedAccessException) { }
    }
}
