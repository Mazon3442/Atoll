using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Island.Settings;

/// <summary>Reads and writes settings JSON. Writes go to a temp file first so a crash never leaves a half-written file.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "settings.json");

    public string Path { get; } = path;

    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            // Keep the broken file for inspection instead of silently overwriting it on next save.
            File.Copy(Path, Path + ".bad", overwrite: true);
            Log.Error($"Settings file was invalid; backed up to {Path}.bad and reset to defaults", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>Live settings shared by the app. Call <see cref="NotifyChanged"/> after editing <see cref="Current"/>; saves are debounced.</summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _saveTimer;

    public SettingsService(SettingsStore store)
    {
        _store = store;
        Current = store.Load();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => Flush();
    }

    public AppSettings Current { get; }

    public event Action? Changed;

    public void NotifyChanged()
    {
        Changed?.Invoke();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Flush()
    {
        _saveTimer.Stop();
        try
        {
            _store.Save(Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}
