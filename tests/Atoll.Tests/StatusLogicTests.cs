using Atoll.Modules.Indicators;

namespace Atoll.Tests;

public class StatusLogicTests
{
    [Theory]
    [InlineData("WireGuard Tunnel", "WireGuard Tunnel", false, true)]
    [InlineData("Ethernet 2", "TAP-Windows Adapter V9", false, true)]
    [InlineData("Cloudflare WARP", "Cloudflare WARP Interface Tunnel", false, true)]
    [InlineData("Work VPN", "WAN Miniport (IKEv2)", true, true)]    // a built-in connection
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", false, false)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", false, false)]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter", false, false)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", false, false)]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", false, false)]
    public void Adapters_are_told_apart(string name, string description, bool pointToPoint, bool expected) =>
        Assert.Equal(expected, VpnAdapters.IsVpn(name, description, pointToPoint));

    private const string Manifest = """
        "AppState"
        {
        	"appid"		"570"
        	"name"		"Dota 2"
        	"StateFlags"		"1026"
        	"BytesToDownload"		"10737418240"
        	"BytesDownloaded"		"2684354560"
        }
        """;

    [Fact]
    public void A_manifest_that_is_still_changing_gives_progress()
    {
        var download = SteamDownloads.Parse(Manifest, TimeSpan.FromSeconds(5));

        Assert.NotNull(download);
        Assert.Equal("Dota 2", download.Name);
        Assert.Equal(0.25, download.Progress, 6);
        Assert.Equal("2.5 GB", SteamDownloads.Size(download.Downloaded));
        Assert.Equal("10.0 GB", SteamDownloads.Size(download.Total));
    }

    [Fact]
    public void A_paused_complete_or_empty_manifest_is_not_a_download()
    {
        Assert.Null(SteamDownloads.Parse(Manifest, TimeSpan.FromMinutes(10)));                      // not touched for ages
        Assert.Null(SteamDownloads.Parse(Manifest.Replace("2684354560", "10737418240"), TimeSpan.Zero)); // done
        Assert.Null(SteamDownloads.Parse(Manifest.Replace("10737418240", "0"), TimeSpan.Zero));         // nothing to download
        Assert.Null(SteamDownloads.Parse("\"AppState\" { \"appid\" \"1\" }", TimeSpan.Zero));
    }

    [Theory]
    [InlineData(820L * 1024 * 1024, "820 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void Sizes_read_naturally(long bytes, string expected) => Assert.Equal(expected, SteamDownloads.Size(bytes));
}

public class VpnAddressTests
{
    private static System.Net.IPAddress[] Ips(params string[] a) => a.Select(System.Net.IPAddress.Parse).ToArray();

    [Fact]
    public void A_stopped_tunnel_with_only_self_assigned_addresses_is_not_connected() =>
        Assert.False(VpnAdapters.HasUsableAddress(Ips("169.254.83.107", "fe80::45e3:2ec3:87e0:5592")));

    [Fact]
    public void A_tunnel_with_a_real_address_is_connected()
    {
        Assert.True(VpnAdapters.HasUsableAddress(Ips("100.101.102.103")));
        Assert.True(VpnAdapters.HasUsableAddress(Ips("fe80::1", "10.8.0.2")));
    }
}
