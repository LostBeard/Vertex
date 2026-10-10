using System.Diagnostics;
using System.Text.Json;
using SpawnDev.Minecraft.Net;
using SpawnDev.Minecraft.Protocol;

namespace SpawnDev.Minecraft;

/// <summary>The server list ping: what the multiplayer screen shows for a server.</summary>
public sealed record ServerStatus(
    string VersionName,
    int Protocol,
    int PlayersOnline,
    int PlayersMax,
    string Description,
    TimeSpan Latency,
    string RawJson)
{
    public static async Task<ServerStatus> PingAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await using var connection = await MinecraftConnection.ConnectAsync(host, port, cancellationToken);
        using (var hello = new PacketWriter(PacketIds.Handshake.Serverbound.Intention))
        {
            hello.WriteVarInt(MinecraftClient.ProtocolVersion).WriteString(host).WriteUShort((ushort)port).WriteVarInt(1);
            await connection.SendAsync(hello, cancellationToken);
        }
        using (var request = new PacketWriter(PacketIds.Status.Serverbound.StatusRequest))
            await connection.SendAsync(request, cancellationToken);

        string json;
        using (var response = await connection.ReceiveAsync(cancellationToken) ?? throw new EndOfStreamException("server closed during status"))
        {
            if (response.Id != PacketIds.Status.Clientbound.StatusResponse)
                throw new InvalidDataException($"expected status response, got packet 0x{response.Id:X2}");
            json = ReadString(response);
        }

        var stopwatch = Stopwatch.StartNew();
        using (var ping = new PacketWriter(PacketIds.Status.Serverbound.PingRequest))
        {
            ping.WriteLong(42);
            await connection.SendAsync(ping, cancellationToken);
        }
        using (var pong = await connection.ReceiveAsync(cancellationToken) ?? throw new EndOfStreamException("server closed before pong"))
        {
            if (pong.Id != PacketIds.Status.Clientbound.PongResponse) throw new InvalidDataException($"expected pong, got 0x{pong.Id:X2}");
        }
        var latency = stopwatch.Elapsed;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.GetProperty("version");
        var players = root.TryGetProperty("players", out var p) ? p : default;
        return new ServerStatus(
            version.GetProperty("name").GetString() ?? "",
            version.GetProperty("protocol").GetInt32(),
            players.ValueKind == JsonValueKind.Object ? players.GetProperty("online").GetInt32() : 0,
            players.ValueKind == JsonValueKind.Object ? players.GetProperty("max").GetInt32() : 0,
            root.TryGetProperty("description", out var d) ? JsonText(d) : "",
            latency,
            json);
    }

    private static string ReadString(ReceivedPacket packet)
    {
        var r = packet.Reader();
        return r.ReadString();
    }

    // The status response is JSON (not NBT): a string or a component object with text/extra.
    private static string JsonText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Array => string.Concat(e.EnumerateArray().Select(JsonText)),
        JsonValueKind.Object =>
            (e.TryGetProperty("text", out var t) ? t.GetString() : "") +
            (e.TryGetProperty("extra", out var x) ? JsonText(x) : ""),
        _ => "",
    };
}
