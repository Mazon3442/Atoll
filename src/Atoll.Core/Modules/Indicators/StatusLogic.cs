using System.Text.RegularExpressions;

namespace Atoll.Modules.Indicators;

/// <summary>Decides which network adapters are a VPN. Pure, so the list of known clients is testable.</summary>
public static class VpnAdapters
{
    private static readonly string[] Known =
    [
        "wireguard", "openvpn", "tap-windows", "wintun", "warp", "cloudflare", "mullvad", "nordlynx", "nordvpn", "protonvpn", "proton vpn",
        "expressvpn", "surfshark", "windscribe", "private internet access", "pia ", "tailscale", "zerotier", "happ", "v2ray", "karing", "tunnel", " vpn", "vpn ",
    ];

    /// <summary>True for a Windows built-in VPN connection (PPP) or an adapter whose name or description is a known VPN client.</summary>
    public static bool IsVpn(string name, string description, bool isPointToPoint)
    {
        if (isPointToPoint) return true;
        var text = (" " + name + " " + description + " ").ToLowerInvariant();
        // Ethernet and Wi-Fi adapters from the hardware makers must not match on a stray word.
        if (text.Contains("virtualbox") || text.Contains("vmware") || text.Contains("hyper-v") || text.Contains("bluetooth")) return false;
        return Known.Any(text.Contains);
    }
}

/// <summary>A Steam game that is downloading or updating right now.</summary>
public sealed record SteamDownload(string Name, double Progress, long Downloaded, long Total);

/// <summary>Reads progress from a Steam <c>appmanifest_*.acf</c> file, which Steam rewrites while it downloads.</summary>
public static partial class SteamDownloads
{
    /// <summary>
    /// The download described by the manifest, or null when the game is complete, idle, or the file has not been
    /// touched for <paramref name="idleAfter"/> (a paused download keeps its numbers but stops changing).
    /// </summary>
    public static SteamDownload? Parse(string acf, TimeSpan sinceLastWrite, TimeSpan? idleAfter = null)
    {
        if (sinceLastWrite > (idleAfter ?? TimeSpan.FromMinutes(2))) return null;
        var fields = Field().Matches(acf).GroupBy(m => m.Groups[1].Value.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        if (!fields.TryGetValue("name", out var name)) return null;
        if (!long.TryParse(fields.GetValueOrDefault("bytestodownload"), out var total) || total <= 0) return null;
        long.TryParse(fields.GetValueOrDefault("bytesdownloaded"), out var done);
        if (done >= total) return null;
        return new SteamDownload(name, Math.Clamp((double)done / total, 0, 1), done, total);
    }

    /// <summary>"4.2 GB", "820 MB".</summary>
    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    [GeneratedRegex("\"(name|BytesToDownload|BytesDownloaded)\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Field();
}
