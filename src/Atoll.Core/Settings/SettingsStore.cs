using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atoll.Settings;

/// <summary>Reads and writes settings JSON. Writes go to a temp file first so a crash never leaves a half-written file.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Atoll", "settings.json");

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
        => SaveJson(Serialize(settings));

    internal static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Json);

    internal void SaveJson(string json)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>Live settings shared by the app. Call <see cref="NotifyChanged"/> after editing <see cref="Current"/>; saves are debounced.</summary>
public sealed class SettingsService : IDisposable
{
    private readonly SettingsStore _store;
    private readonly Timer _saveTimer;
    private readonly object _gate = new();
    private string? _pending;

    public SettingsService(SettingsStore store)
    {
        _store = store;
        Current = store.Load();
        _saveTimer = new Timer(_ => SavePending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppSettings Current { get; }

    public event Action? Changed;

    public void NotifyChanged()
    {
        Changed?.Invoke();
        lock (_gate)
        {
            // Capture on the caller's UI thread; the writer never reads mutable settings.
            _pending = SettingsStore.Serialize(Current);
            _saveTimer.Change(500, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pending = SettingsStore.Serialize(Current);
            SavePending();
        }
    }

    private void SavePending()
    {
        lock (_gate)
        {
            if (_pending is null) return;
            try
            {
                _store.SaveJson(_pending);
                _pending = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not save settings", ex);
            }
        }
    }

    public void Dispose() { Flush(); _saveTimer.Dispose(); }
}
