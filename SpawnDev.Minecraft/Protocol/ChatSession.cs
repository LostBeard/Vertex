using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SpawnDev.Minecraft.Auth;

namespace SpawnDev.Minecraft.Protocol;

/// <summary>
/// A signed chat session (vanilla LocalChatSession + SignedMessageChain.Encoder): a random session id and a
/// message index that counts up, signing each message with the account's profile key.
/// </summary>
public sealed class ChatSession : IDisposable
{
    private readonly RSA _key;
    private int _index;

    public ChatSession(ProfileKeys keys)
    {
        _key = keys.CreatePrivateKey();
    }

    public Guid SessionId { get; } = Guid.NewGuid();

    /// <summary>
    /// SHA256withRSA over exactly what PlayerChatMessage.updateSignature feeds (read from the 1.21.5 server's
    /// bytecode): int 1; link = sender UUID, session UUID, int index; body = long salt, long epoch SECONDS,
    /// int UTF-8 length + bytes, int last-seen count + each last-seen signature. All big-endian.
    /// </summary>
    public byte[] Sign(Guid sender, string content, DateTimeOffset timestamp, long salt, IReadOnlyList<byte[]> lastSeen)
    {
        var text = Encoding.UTF8.GetBytes(content);
        var data = new byte[4 + 16 + 16 + 4 + 8 + 8 + 4 + text.Length + 4 + lastSeen.Sum(s => s.Length)];
        var span = data.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span, 1);
        sender.TryWriteBytes(span[4..], bigEndian: true, out _);
        SessionId.TryWriteBytes(span[20..], bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(span[36..], _index++);
        BinaryPrimitives.WriteInt64BigEndian(span[40..], salt);
        BinaryPrimitives.WriteInt64BigEndian(span[48..], timestamp.ToUnixTimeSeconds());
        BinaryPrimitives.WriteInt32BigEndian(span[56..], text.Length);
        text.CopyTo(span[60..]);
        var p = 60 + text.Length;
        BinaryPrimitives.WriteInt32BigEndian(span[p..], lastSeen.Count);
        p += 4;
        foreach (var s in lastSeen)
        {
            s.CopyTo(span[p..]);
            p += s.Length;
        }
        return _key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public void Dispose() => _key.Dispose();
}
