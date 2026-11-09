using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Atoll.Modules.Games;

/// <summary>An installed game and the launcher link that starts it.</summary>
public sealed record GameEntry(string Name, string Source, string Launch, string? InstallDir = null, DateTimeOffset? LastPlayed = null);

/// <summary>Finds installed games from Steam, Epic and the Minecraft launchers, without asking any of them.</summary>
public static partial class GameLibrary
{
    public static IReadOnlyList<GameEntry> Scan()
    {
        var games = new List<GameEntry>();
        try { games.AddRange(Steam()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info($"Steam library unreadable: {ex.Message}"); }
        try { games.AddRange(Epic()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Log.Info($"Epic library unreadable: {ex.Message}"); }
        games.AddRange(Minecraft());
        return games.DistinctBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.LastPlayed ?? DateTimeOffset.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPath();

    [GeneratedRegex("\"(appid|name|installdir|LastPlayed)\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AcfField();

    private static readonly HashSet<string> SteamTools = ["228980", "1070560", "1391110", "1628350", "1493710"]; // redistributables, Linux runtimes, Proton

    internal static IEnumerable<GameEntry> Steam()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam) yield break;
        var folders = new List<string> { Path.Combine(steam, "steamapps") };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            folders.AddRange(VdfPath().Matches(File.ReadAllText(vdf)).Select(m => Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps")));
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
            foreach (var manifest in Directory.EnumerateFiles(folder, "appmanifest_*.acf"))
                if (ParseAcf(File.ReadAllText(manifest), folder) is { } game) yield return game;
    }

    internal static GameEntry? ParseAcf(string acf, string steamapps)
    {
        var fields = AcfField().Matches(acf).GroupBy(m => m.Groups[1].Value.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        if (!fields.TryGetValue("appid", out var id) || !fields.TryGetValue("name", out var name) || SteamTools.Contains(id)) return null;
        if (name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)) return null;
        DateTimeOffset? played = fields.TryGetValue("lastplayed", out var p) && long.TryParse(p, out var unix) && unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        var dir = fields.TryGetValue("installdir", out var d) ? Path.Combine(steamapps, "common", d) : null;
        return new GameEntry(name, "Steam", $"steam://rungameid/{id}", dir, played);
    }

    private static IEnumerable<GameEntry> Epic()
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) yield break;
        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (S("DisplayName") is not { } name || S("AppName") is not { } app) continue;
            if (r.TryGetProperty("bIsApplication", out var isApp) && isApp.ValueKind == JsonValueKind.False) continue; // add-ons, engines
            var ns = S("CatalogNamespace"); var item = S("CatalogItemId");
            var launch = ns is null || item is null ? $"com.epicgames.launcher://apps/{app}?action=launch&silent=true"
                : $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true";
            yield return new GameEntry(name, "Epic", launch, S("InstallLocation"));
        }
    }

    private static IEnumerable<GameEntry> Minecraft()
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(Path.Combine(packages, "Microsoft.4297127D64EC6_8wekyb3d8bbwe")))
            yield return new GameEntry("Minecraft Launcher", "Minecraft", @"shell:AppsFolder\Microsoft.4297127D64EC6_8wekyb3d8bbwe!Minecraft");
        else if (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "MinecraftLauncher.exe") is var exe && File.Exists(exe))
            yield return new GameEntry("Minecraft Launcher", "Minecraft", exe);
        if (Directory.Exists(Path.Combine(packages, "Microsoft.MinecraftUWP_8wekyb3d8bbwe")))
            yield return new GameEntry("Minecraft for Windows", "Minecraft", "minecraft:");
    }
}
