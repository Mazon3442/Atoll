using System.Net.Http;

namespace Atoll.Modules.Tide.Listen;

/// <summary>
/// Finds, verifies and downloads the ggml Whisper model. There is no model in the repository: the first
/// Listen run fetches it once into %LOCALAPPDATA%\Atoll\listen.
/// </summary>
internal static class ListenModel
{
    public const string DefaultName = "base.en";

    /// <summary>ggml-base.en.bin as published by whisper.cpp. The exact length catches truncated downloads.</summary>
    public const long BaseEnBytes = 147_964_211;

    // Any usable ggml model is far larger than this; it keeps a custom path from accepting an HTML error page.
    private const long MinimumBytes = 10 * 1024 * 1024;
    private const string Source = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atoll", "listen");

    public static string FileName(string model) => $"ggml-{Sanitize(model)}.bin";

    /// <summary>Empty custom path means the managed model directory; otherwise the user's own .bin.</summary>
    public static string Resolve(string customPath, string model) =>
        string.IsNullOrWhiteSpace(customPath)
            ? Path.Combine(Directory, FileName(model))
            : Environment.ExpandEnvironmentVariables(customPath.Trim());

    public static bool IsCoreModel(string customPath) => string.IsNullOrWhiteSpace(customPath);

    /// <summary>
    /// A model is ready when its length matches what whisper.cpp publishes for base.en, or, for other
    /// models and user-chosen files, when it is at least plausibly a model rather than a partial file.
    /// </summary>
    public static bool IsComplete(string path, string model, bool exactSize)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return false;
        if (exactSize && Sanitize(model).Equals(DefaultName, StringComparison.OrdinalIgnoreCase))
            return file.Length == BaseEnBytes;
        return file.Length >= MinimumBytes;
    }

    public static Uri Url(string model) =>
        new(Source + FileName(model) + "?download=true", UriKind.Absolute);

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Downloads to a .part file and only moves it into place when the length checks out, so a dropped
    /// connection never leaves a half model that Whisper would try to load.
    /// </summary>
    public static async Task DownloadAsync(string path, string model, IProgress<double>? progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Directory);
        var temp = path + ".part";
        Delete(temp);
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, UseCookies = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Atoll/1.0 (+local speech model download)");
            using var response = await client.GetAsync(Url(model), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The model server answered {(int)response.StatusCode} {response.StatusCode}.");

            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    if (total is > 0) progress?.Report(Math.Clamp(written / (double)total.Value, 0, 1));
                }
                await output.FlushAsync(ct);
                if (total is > 0 && written != total.Value)
                    throw new IOException($"The model download stopped after {written:N0} of {total.Value:N0} bytes.");
            }

            var downloaded = new FileInfo(temp);
            if (downloaded.Length < MinimumBytes)
                throw new IOException($"The downloaded model is only {downloaded.Length:N0} bytes, which is not a model.");

            File.Move(temp, path, overwrite: true);
            progress?.Report(1);
        }
        catch
        {
            Delete(temp);
            throw;
        }
    }

    /// <summary>Model names become file names, so keep them to the characters whisper.cpp publishes.</summary>
    private static string Sanitize(string model)
    {
        var name = string.IsNullOrWhiteSpace(model) ? DefaultName : model.Trim();
        var cleaned = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return cleaned.Length == 0 ? DefaultName : cleaned;
    }
}
