using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atoll.Modules.Games;

public sealed record ServerStatus(int Players, int MaxPlayers, string Version, string Motd, long PingMs);

/// <summary>
/// Asks a Minecraft Java server for its status with the Server List Ping, the request the multiplayer screen sends.
/// shortcut: no SRV record lookup, so addresses that rely on one need their port written out.
/// </summary>
public static partial class MinecraftServer
{
    public static (string Host, int Port) ParseAddress(string address)
    {
        var a = address.Trim();
        var colon = a.LastIndexOf(':');
        return colon > 0 && int.TryParse(a[(colon + 1)..], out var port) && port is > 0 and < 65536 ? (a[..colon], port) : (a, 25565);
    }

    public static async Task<ServerStatus> QueryAsync(string address, CancellationToken ct)
    {
        var (host, port) = ParseAddress(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var tcp = new TcpClient();
        var watch = Stopwatch.StartNew();
        await tcp.ConnectAsync(host, port, timeout.Token);
        var ping = watch.ElapsedMilliseconds;
        var stream = tcp.GetStream();

        var handshake = new MemoryStream();
        WriteVarInt(handshake, 0x00);
        WriteVarInt(handshake, -1); // protocol version: any, for a status request
        var hostBytes = Encoding.UTF8.GetBytes(host);
        WriteVarInt(handshake, hostBytes.Length);
        handshake.Write(hostBytes);
        handshake.WriteByte((byte)(port >> 8));
        handshake.WriteByte((byte)port);
        WriteVarInt(handshake, 1); // next state: status
        await WritePacketAsync(stream, handshake.ToArray(), timeout.Token);
        await WritePacketAsync(stream, [0x00], timeout.Token);

        await ReadVarIntAsync(stream, timeout.Token); // packet length
        if (await ReadVarIntAsync(stream, timeout.Token) != 0) throw new InvalidDataException("Unexpected reply");
        var length = await ReadVarIntAsync(stream, timeout.Token);
        if (length is <= 0 or > 1 << 20) throw new InvalidDataException("Reply too large");
        var json = new byte[length];
        await stream.ReadExactlyAsync(json, timeout.Token);
        return Parse(Encoding.UTF8.GetString(json), ping);
    }

    internal static ServerStatus Parse(string json, long ping)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        int online = 0, max = 0;
        if (r.TryGetProperty("players", out var p))
        {
            if (p.TryGetProperty("online", out var o)) o.TryGetInt32(out online);
            if (p.TryGetProperty("max", out var m)) m.TryGetInt32(out max);
        }
        var version = r.TryGetProperty("version", out var v) && v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var motd = r.TryGetProperty("description", out var d) ? Flatten(d) : "";
        return new ServerStatus(online, max, FormatCode().Replace(version, ""), FormatCode().Replace(motd, "").Trim(), ping);
    }

    /// <summary>The description is plain text or a chat component with nested "extra" parts.</summary>
    private static string Flatten(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Object => (e.TryGetProperty("text", out var t) ? t.GetString() : "") + (e.TryGetProperty("extra", out var x) ? Flatten(x) : ""),
        JsonValueKind.Array => string.Concat(e.EnumerateArray().Select(Flatten)),
        _ => "",
    };

    [GeneratedRegex("§.")]
    private static partial Regex FormatCode();

    private static void WriteVarInt(Stream s, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            s.WriteByte(v != 0 ? (byte)(b | 0x80) : b);
        } while (v != 0);
    }

    private static async Task WritePacketAsync(Stream s, byte[] body, CancellationToken ct)
    {
        var packet = new MemoryStream();
        WriteVarInt(packet, body.Length);
        packet.Write(body);
        await s.WriteAsync(packet.ToArray(), ct);
    }

    private static async Task<int> ReadVarIntAsync(Stream s, CancellationToken ct)
    {
        int value = 0, shift = 0;
        var one = new byte[1];
        while (true)
        {
            await s.ReadExactlyAsync(one, ct);
            value |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) return value;
            if ((shift += 7) > 28) throw new InvalidDataException("VarInt too long");
        }
    }
}
