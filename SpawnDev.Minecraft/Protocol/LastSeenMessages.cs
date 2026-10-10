namespace SpawnDev.Minecraft.Protocol;

/// <summary>
/// The client half of vanilla's last-seen-messages window (LastSeenMessagesTracker): every SIGNED player chat
/// message received goes into a 20-slot window, and each chat we send acknowledges the window. The server's
/// LastSeenMessagesValidator rejects a chat whose offset/bitset disagree with what it sent us, so this must
/// mirror the vanilla client exactly. Unsigned messages are never tracked.
/// </summary>
public sealed class LastSeenMessages
{
    public const int WindowSize = 20;

    private readonly byte[]?[] _window = new byte[]?[WindowSize];
    private int _tail;
    private int _offset;

    /// <summary>Messages received since the last update was sent; vanilla sends a chat_ack above 64.</summary>
    public int PendingCount => _offset;

    public void Add(ReadOnlySpan<byte> signature)
    {
        _window[_tail] = signature.ToArray();
        _tail = (_tail + 1) % WindowSize;
        _offset++;
    }

    /// <summary>
    /// The update to attach to an outgoing chat: how far the window advanced, and which of the 20 slots
    /// (oldest first) hold a message. Resets the pending count.
    /// </summary>
    public (int Offset, byte[] Acknowledged) TakeUpdate()
    {
        var bits = new byte[(WindowSize + 7) / 8];
        for (var i = 0; i < WindowSize; i++)
            if (_window[(_tail + i) % WindowSize] != null) bits[i >> 3] |= (byte)(1 << (i & 7)); // java.util.BitSet byte order
        var offset = _offset;
        _offset = 0;
        return (offset, bits);
    }

    /// <summary>For chat_ack (message_acknowledgement): just the offset, then reset.</summary>
    public int TakeOffset()
    {
        var offset = _offset;
        _offset = 0;
        return offset;
    }
}
