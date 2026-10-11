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
    private byte[]? _lastTracked;
    private readonly object _lock = new(); // Add runs on the receive loop, TakeUpdate on the sender

    /// <summary>Messages received since the last update was sent; vanilla sends a chat_ack above 64.</summary>
    public int PendingCount => _offset;

    /// <summary>
    /// Tracks a received signature. The same message twice in a row counts once (LastSeenMessagesTracker
    /// .addPending, mirrored by the server's LastSeenMessagesValidator): a /msg to ourselves arrives as both the
    /// sent and the received copy, and counting both gets us kicked ("Advanced last seen window by 2 messages,
    /// but expected at most 1").
    /// </summary>
    public void Add(ReadOnlySpan<byte> signature)
    {
        lock (_lock)
        {
            if (_lastTracked != null && signature.SequenceEqual(_lastTracked)) return;
            var copy = signature.ToArray();
            _lastTracked = copy;
            _window[_tail] = copy;
            _tail = (_tail + 1) % WindowSize;
            _offset++;
        }
    }

    /// <summary>
    /// The update to attach to an outgoing chat: how far the window advanced, and which of the 20 slots
    /// (oldest first) hold a message. Resets the pending count.
    /// </summary>
    /// <remarks>Signatures are the acknowledged messages oldest first: a signed chat signs over exactly these.</remarks>
    public (int Offset, byte[] Acknowledged, List<byte[]> Signatures) TakeUpdate()
    {
        lock (_lock) return TakeUpdateLocked();
    }

    private (int Offset, byte[] Acknowledged, List<byte[]> Signatures) TakeUpdateLocked()
    {
        var bits = new byte[(WindowSize + 7) / 8];
        var signatures = new List<byte[]>();
        for (var i = 0; i < WindowSize; i++)
        {
            if (_window[(_tail + i) % WindowSize] is not byte[] signature) continue;
            bits[i >> 3] |= (byte)(1 << (i & 7)); // java.util.BitSet byte order
            signatures.Add(signature);
        }
        var offset = _offset;
        _offset = 0;
        return (offset, bits, signatures);
    }

    /// <summary>For chat_ack (message_acknowledgement): just the offset, then reset.</summary>
    public int TakeOffset()
    {
        lock (_lock)
        {
            var offset = _offset;
            _offset = 0;
            return offset;
        }
    }
}
