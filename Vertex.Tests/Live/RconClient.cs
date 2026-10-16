using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Vertex.Tests.Live;

/// <summary>
/// A minimal Minecraft RCON client (the Source RCON protocol) for tests against a server somebody else started, such as the AubsCraft dev server: the server's OWN answer about its world
/// ("execute if block x y z minecraft:stone" answers "Test passed"), not what Vertex believes.
/// </summary>
public sealed class RconClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream _stream = null!;
    private int _nextId = 1;

    public static async Task<RconClient> ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        var rcon = new RconClient();
        await rcon._tcp.ConnectAsync(host, port, ct);
        rcon._stream = rcon._tcp.GetStream();
        var id = rcon._nextId++;
        await rcon.WriteAsync(id, 3, password, ct);
        var (replyId, _, _) = await rcon.ReadAsync(ct);
        if (replyId == -1) throw new InvalidOperationException("RCON: wrong password");
        return rcon;
    }

    public async Task<string> CommandAsync(string command, CancellationToken ct = default)
    {
        var id = _nextId++;
        await WriteAsync(id, 2, command, ct);
        var (_, _, body) = await ReadAsync(ct);
        return body;
    }

    private async Task WriteAsync(int id, int type, string body, CancellationToken ct)
    {
        var text = Encoding.UTF8.GetBytes(body);
        var packet = new byte[4 + 4 + 4 + text.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(packet, 4 + 4 + text.Length + 2);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        text.CopyTo(packet, 12);
        await _stream.WriteAsync(packet, ct);
    }

    private async Task<(int Id, int Type, string Body)> ReadAsync(CancellationToken ct)
    {
        var head = new byte[4];
        await _stream.ReadExactlyAsync(head, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(head);
        var rest = new byte[length];
        await _stream.ReadExactlyAsync(rest, ct);
        return (BinaryPrimitives.ReadInt32LittleEndian(rest), BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4)), Encoding.UTF8.GetString(rest, 8, length - 10));
    }

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
