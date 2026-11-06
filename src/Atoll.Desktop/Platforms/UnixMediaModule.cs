#if !WINDOWS
using System.Globalization;
using System.Text.Json;
using Atoll.Activities;
using Atoll.Desktop;
using Atoll.Modules.Clock;
using Avalonia.Threading;

namespace Atoll.Modules.Media;

public sealed partial class MediaModule : IIslandModule
{
    private readonly MediaViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ModuleContext? _context;
    private bool _busy;
    private bool _disposed;
    private long _revision;
    private string? _player;
    private string _artKey = "";
    private double _duration;
    private readonly SemaphoreSlim _seekGate = new(1, 1);
    private long _seekVersion;
    private readonly Dictionary<string, string> _audioOnly = [];
    public MediaModule()
    {
        _vm = new MediaViewModel(() => Control("play-pause"), () => Control("next"), () => Control("previous"), Seek);
        _vm.SourceChanged += _ => { _revision++; _vm.CanSeek = false; _vm.CanControl = false; OnTick(null, EventArgs.Empty); };
        _vm.VisualizerNote = "Live audio visualization is unavailable on this desktop; colors come from the cover.";
    }
    public object Panel => _vm;
    public void Start(ModuleContext context)
    {
        _context = context; context.Settings.Changed += ThemeChanged; ThemeChanged();
        _timer.Tick += OnTick; _timer.Start(); OnTick(null, EventArgs.Empty);
    }
    private void ThemeChanged() => _vm.ApplyTheme(_context!.Settings.Current.Theme);
    private static string MacScript(string player, string body)
    {
        if (player is not ("Spotify" or "Music")) throw new InvalidOperationException("Unknown player.");
        return $"tell application \"{player}\"\n{body}\nend tell";
    }
    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy || _disposed) return; _busy = true;
        var revision = _revision;
        try
        {
            var players = OperatingSystem.IsMacOS()
                ? (await Commands.RunAsync("osascript", "-e", "set names to {}\nif application \"Spotify\" is running then set end of names to \"Spotify\"\nif application \"Music\" is running then set end of names to \"Music\"\nset AppleScript's text item delimiters to linefeed\nreturn names as text")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                : (await Optional(() => Commands.RunAsync("playerctl", "--list-all"))).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (_disposed || revision != _revision) return;
            var choices = players.Select(p => new MediaSourceChoice(p, p.Split('.')[0])).ToList();
            _audioOnly.Clear();
            if (OperatingSystem.IsLinux())
            {
                var json = await Optional(() => Commands.RunAsync("pactl", "--format=json", "list", "sink-inputs"));
                if (json.Length > 0)
                {
                    using var document = JsonDocument.Parse(json);
                    foreach (var input in document.RootElement.EnumerateArray())
                    {
                        var properties = input.GetProperty("properties");
                        var name = properties.TryGetProperty("application.name", out var n) ? n.GetString() ?? "Audio app" : "Audio app";
                        if (players.Any(p => p.Contains(name, StringComparison.OrdinalIgnoreCase))) continue;
                        var id = "audio:" + input.GetProperty("index").GetRawText(); _audioOnly[id] = name;
                        choices.Add(new MediaSourceChoice(id, name + " (audio)"));
                    }
                }
            }
            if (_disposed || revision != _revision) return;
            _vm.SetSources(choices);
            var selection = _vm.SelectedSource?.Id ?? "auto";
            if (_audioOnly.TryGetValue(selection, out var audioName))
            {
                _player = null; _artKey = ""; _vm.Title = audioName + " audio"; _vm.Artist = ""; _vm.Art = null; _vm.HasSession = true; _vm.IsPlaying = false;
                _vm.CanControl = false; _vm.CanSeek = false; _vm.HasTimeline = false;
                _vm.Status = "This audio stream does not expose track metadata or playback controls.";
                _context?.Post("now", _vm, ActivityPriority.Ambient); return;
            }
            var player = selection == "auto" ? players.FirstOrDefault() : players.FirstOrDefault(p => p == selection);
            if (selection == "auto")
                foreach (var candidate in players.Take(12))
                {
                    var state = OperatingSystem.IsMacOS() ? await Optional(() => Commands.RunAsync("osascript", "-e", MacScript(candidate, "return player state as text")))
                        : await Optional(() => Commands.RunAsync("playerctl", "--player", candidate, "status"));
                    if (state.Equals("playing", StringComparison.OrdinalIgnoreCase)) { player = candidate; break; }
                }
            if (player is null) { Clear(); return; }
            string title, artist, status, art;
            double duration, position;
            var canSeek = true;
            if (OperatingSystem.IsMacOS())
            {
                var raw = await Commands.RunAsync("osascript", "-e", MacScript(player, "if player state is stopped then return \"\"\nreturn (name of current track) & linefeed & (artist of current track) & linefeed & (player state as text) & linefeed & (duration of current track as text) & linefeed & (player position as text)"));
                var fields = raw.Split('\n'); title = fields.ElementAtOrDefault(0) ?? ""; artist = fields.ElementAtOrDefault(1) ?? ""; status = fields.ElementAtOrDefault(2) ?? "";
                duration = Number(fields.ElementAtOrDefault(3)) / (player == "Spotify" ? 1000 : 1); position = Number(fields.ElementAtOrDefault(4));
                art = player == "Spotify" ? await Optional(() => Commands.RunAsync("osascript", "-e", MacScript(player, "return artwork url of current track"))) : "";
            }
            else
            {
                title = await Optional(() => Commands.RunAsync("playerctl", "--player", player, "metadata", "xesam:title"));
                artist = await Optional(() => Commands.RunAsync("playerctl", "--player", player, "metadata", "xesam:artist"));
                status = await Optional(() => Commands.RunAsync("playerctl", "--player", player, "status"));
                duration = Number(await Optional(() => Commands.RunAsync("playerctl", "--player", player, "metadata", "mpris:length"))) / 1_000_000;
                position = Number(await Optional(() => Commands.RunAsync("playerctl", "--player", player, "position")));
                art = await Optional(() => Commands.RunAsync("playerctl", "--player", player, "metadata", "mpris:artUrl"));
                canSeek = (await Optional(() => Commands.RunAsync("gdbus", "call", "--session", "--dest", "org.mpris.MediaPlayer2." + player,
                    "--object-path", "/org/mpris/MediaPlayer2", "--method", "org.freedesktop.DBus.Properties.Get", "org.mpris.MediaPlayer2.Player", "CanSeek"))).Contains("<true>", StringComparison.Ordinal);
            }
            if (_disposed || revision != _revision) return;
            if (_player != player) _vm.InvalidatePlayback();
            _player = player; _duration = duration;
            _vm.Title = title; _vm.Artist = artist; _vm.AppName = player.Split('.')[0]; _vm.HasSession = title.Length > 0;
            _vm.CanControl = _vm.HasSession; _vm.HasTimeline = duration > 0; _vm.CanSeek = duration > 0 && canSeek;
            _vm.IsPlaying = status.Equals("playing", StringComparison.OrdinalIgnoreCase);
            if (!_vm.IsScrubbing) _vm.Progress = duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0;
            _vm.PositionText = TimerViewModel.Format(TimeSpan.FromSeconds(Math.Max(0, position))); _vm.DurationText = TimerViewModel.Format(TimeSpan.FromSeconds(Math.Max(0, duration)));
            _vm.Status = _vm.HasSession ? "" : "Play a track in the selected player.";
            var key = $"{player}\n{title}\n{artist}\n{art}";
            if (key != _artKey)
            {
                _artKey = key; _vm.Art = null;
                var cover = player == "Music" && OperatingSystem.IsMacOS() ? await PlayerArtwork.LoadMusicAsync() : await PlayerArtwork.LoadAsync(art);
                if (_disposed || revision != _revision) { cover?.Dispose(); return; }
                _vm.Art = cover;
            }
            if (_vm.HasSession && _vm.IsPlaying) _context?.Post("now", _vm, ActivityPriority.Normal);
            else _context?.Remove("now");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        {
            if (!_disposed && revision == _revision) { Clear(); _vm.Status = "The source is unavailable. Check the selected app and desktop media permissions."; }
        }
        finally { _busy = false; if (!_disposed && revision != _revision) OnTick(null, EventArgs.Empty); }
    }
    private void Clear() { _player = null; _artKey = ""; _vm.HasSession = false; _vm.CanControl = false; _vm.CanSeek = false; _vm.HasTimeline = false; _vm.Title = ""; _vm.Art = null; _context?.Remove("now"); }
    private static double Number(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static async Task<string> Optional(Func<Task<string>> read) { try { return await read(); } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return ""; } }
    private async Task Control(string action)
    {
        if (_player is not { } player) return;
        try
        {
            if (OperatingSystem.IsMacOS()) await Commands.RunAsync("osascript", "-e", MacScript(player, action switch { "play-pause" => "playpause", "next" => "next track", _ => "previous track" }));
            else await Commands.RunAsync("playerctl", "--player", player, action);
            OnTick(null, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { _vm.Status = "The selected source could not complete that command."; }
    }
    private async Task Seek(double progress)
    {
        var player = _player; var revision = _vm.PlaybackRevision; var version = ++_seekVersion;
        if (player is null || !_vm.CanSeek || !double.IsFinite(progress)) return;
        await _seekGate.WaitAsync();
        try
        {
            if (_disposed || version != _seekVersion || revision != _vm.PlaybackRevision || player != _player) return;
            _revision++;
            var seconds = Math.Clamp(progress, 0, 1) * _duration;
            if (OperatingSystem.IsMacOS()) await Commands.RunAsync("osascript", "-e", MacScript(player, "set player position to " + seconds.ToString(CultureInfo.InvariantCulture)));
            else await Commands.RunAsync("playerctl", "--player", player, "position", seconds.ToString(CultureInfo.InvariantCulture));
            if (version == _seekVersion && revision == _vm.PlaybackRevision) { _vm.Progress = Math.Clamp(progress, 0, 1); _vm.Status = ""; }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { _vm.Status = "This player did not accept the seek. Try again or choose another source."; }
        finally { _revision++; _seekGate.Release(); }
    }
    public void Dispose() { _disposed = true; _revision++; _timer.Stop(); _timer.Tick -= OnTick; if (_context is not null) _context.Settings.Changed -= ThemeChanged; }
}
#endif
