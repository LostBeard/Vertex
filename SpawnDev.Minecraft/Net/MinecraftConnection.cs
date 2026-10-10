using System.Buffers;
using System.IO.Compression;
using System.Net.Sockets;
using SpawnDev.Minecraft.Protocol;

namespace SpawnDev.Minecraft.Net;

/// <summary>
/// One received packet. The body is either a slice of the connection's receive buffer (no copy) or a pooled
/// buffer holding the inflated packet. It is valid until Dispose or the next ReceiveAsync, whichever is first.
/// </summary>
public readonly struct ReceivedPacket : IDisposable
{
    private readonly byte[]? _owner;
    private readonly int _offset;
    private readonly bool _pooled;

    public int Id { get; }
    public int Length { get; }

    internal ReceivedPacket(int id, byte[]? owner, int offset, int length, bool pooled)
    {
        Id = id;
        _owner = owner;
        _offset = offset;
        Length = length;
        _pooled = pooled;
    }

    /// <summary>The fields after the packet id.</summary>
    public ReadOnlySpan<byte> Body => _owner == null ? default : _owner.AsSpan(_offset, Length);

    public PacketReader Reader() => new(Body);

    public void Dispose()
    {
        if (_pooled && _owner != null) ArrayPool<byte>.Shared.Return(_owner);
    }
}

/// <summary>
/// A Minecraft Java connection: VarInt-length framing, zlib compression above the negotiated threshold, and
/// AES/CFB8 once encryption is enabled. Reads are single-reader (one receive loop); sends are serialized.
/// </summary>
public sealed class MinecraftConnection : IAsyncDisposable
{
    /// <summary>Vanilla's limits: a frame is at most 2^21-1 bytes, an uncompressed packet at most 2^23.</summary>
    public const int MaxFrame = (1 << 21) - 1;
    public const int MaxUncompressed = 1 << 23;

    private readonly Socket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Cfb8Cipher? _encrypt;
    private Cfb8Cipher? _decrypt;
    private int _threshold = -1;

    // Receive buffer: [_rxStart, _rxEnd) holds received (already decrypted) bytes not yet consumed.
    private byte[] _rx = new byte[64 * 1024];
    private int _rxStart, _rxEnd;

    private MinecraftConnection(Socket socket) => _socket = socket;

    public static async Task<MinecraftConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new MinecraftConnection(socket);
    }

    public int CompressionThreshold => _threshold;
    public bool Encrypted => _encrypt != null;

    /// <summary>Applies Set Compression: from the next packet on, both directions use the new framing.</summary>
    public void EnableCompression(int threshold) => _threshold = threshold;

    /// <summary>Turns on AES/CFB8 both ways. Bytes already buffered but unread are decrypted too.</summary>
    public void EnableEncryption(ReadOnlySpan<byte> sharedSecret)
    {
        _encrypt = new Cfb8Cipher(sharedSecret, encrypt: true);
        _decrypt = new Cfb8Cipher(sharedSecret, encrypt: false);
        _decrypt.Transform(_rx, _rxStart, _rxEnd - _rxStart);
    }

    // ---------------------------------------------------------------- receive

    /// <summary>Reads the next packet. Returns null when the server closed the connection.</summary>
    public async ValueTask<ReceivedPacket?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        // Frame length: up to 3 VarInt bytes for 2^21.
        int frameLength, used;
        while (!TryReadVarInt(_rx.AsSpan(_rxStart, _rxEnd - _rxStart), out frameLength, out used))
        {
            if (_rxEnd - _rxStart >= 3) throw new InvalidDataException("frame length VarInt too long");
            if (!await FillAsync(cancellationToken)) return null;
        }
        _rxStart += used;
        if (frameLength <= 0 || frameLength > MaxFrame) throw new InvalidDataException($"bad frame length {frameLength}");
        await EnsureAsync(frameLength, cancellationToken);

        var frameStart = _rxStart;
        var frame = _rx.AsSpan(frameStart, frameLength);
        _rxStart += frameLength;

        if (_threshold < 0) return InPlace(frame, frameStart);

        if (!TryReadVarInt(frame, out var dataLength, out var dlSize)) throw new InvalidDataException("bad data length");
        var compressed = frame[dlSize..];
        if (dataLength == 0) return InPlace(compressed, frameStart + dlSize);
        if (dataLength < _threshold) throw new InvalidDataException($"compressed packet of {dataLength} bytes is below the threshold {_threshold}");
        if (dataLength > MaxUncompressed) throw new InvalidDataException($"packet of {dataLength} bytes exceeds 2^23");

        var inflated = ArrayPool<byte>.Shared.Rent(dataLength);
        try
        {
            Inflate(compressed, inflated.AsSpan(0, dataLength));
            if (!TryReadVarInt(inflated.AsSpan(0, dataLength), out var id, out var idSize)) throw new InvalidDataException("bad packet id");
            return new ReceivedPacket(id, inflated, idSize, dataLength - idSize, pooled: true);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(inflated);
            throw;
        }
    }

    // An uncompressed packet is handed out as a slice of the receive buffer: no copy. The next ReceiveAsync may
    // compact or replace that buffer, which is why a packet is only valid until then.
    private ReceivedPacket InPlace(ReadOnlySpan<byte> data, int offset)
    {
        if (!TryReadVarInt(data, out var id, out var idSize)) throw new InvalidDataException("bad packet id");
        return new ReceivedPacket(id, _rx, offset + idSize, data.Length - idSize, pooled: false);
    }

    private static unsafe void Inflate(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        fixed (byte* p = source)
        {
            using var input = new UnmanagedMemoryStream(p, source.Length);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            var total = 0;
            while (total < destination.Length)
            {
                var n = z.Read(destination[total..]);
                if (n == 0) throw new InvalidDataException($"compressed packet inflated to {total} of {destination.Length} bytes");
                total += n;
            }
        }
    }

    private async ValueTask EnsureAsync(int count, CancellationToken cancellationToken)
    {
        if (_rxStart + count > _rx.Length)
        {
            // Move the unread bytes to the front, growing if the frame is bigger than the buffer.
            var unread = _rxEnd - _rxStart;
            var target = count > _rx.Length ? new byte[Math.Max(count, _rx.Length * 2)] : _rx;
            Buffer.BlockCopy(_rx, _rxStart, target, 0, unread);
            _rx = target;
            _rxStart = 0;
            _rxEnd = unread;
        }
        while (_rxEnd - _rxStart < count)
            if (!await FillAsync(cancellationToken)) throw new EndOfStreamException("connection closed mid-packet");
    }

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_rxEnd == _rx.Length)
        {
            var unread = _rxEnd - _rxStart;
            Buffer.BlockCopy(_rx, _rxStart, _rx, 0, unread);
            _rxStart = 0;
            _rxEnd = unread;
        }
        var n = await _socket.ReceiveAsync(_rx.AsMemory(_rxEnd), SocketFlags.None, cancellationToken);
        if (n == 0) return false;
        _decrypt?.Transform(_rx, _rxEnd, n);
        _rxEnd += n;
        return true;
    }

    // ---------------------------------------------------------------- send

    /// <summary>Frames, compresses, encrypts and sends one packet built with a PacketWriter.</summary>
    public async ValueTask SendAsync(PacketWriter packet, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        byte[]? frame = null;
        try
        {
            var length = BuildFrame(packet.Written, out frame);
            _encrypt?.Transform(frame, 0, length);
            var sent = 0;
            while (sent < length)
                sent += await _socket.SendAsync(frame.AsMemory(sent, length - sent), SocketFlags.None, cancellationToken);
        }
        finally
        {
            if (frame != null) ArrayPool<byte>.Shared.Return(frame);
            _sendLock.Release();
        }
    }

    private int BuildFrame(ReadOnlySpan<byte> body, out byte[] frame)
    {
        if (_threshold < 0)
        {
            frame = ArrayPool<byte>.Shared.Rent(body.Length + 5);
            var n = WriteVarInt(frame, 0, body.Length);
            body.CopyTo(frame.AsSpan(n));
            return n + body.Length;
        }
        if (body.Length < _threshold)
        {
            frame = ArrayPool<byte>.Shared.Rent(body.Length + 6);
            var n = WriteVarInt(frame, 0, body.Length + 1);
            frame[n++] = 0; // data length 0 = not compressed
            body.CopyTo(frame.AsSpan(n));
            return n + body.Length;
        }

        // Compress after a 10-byte gap (room for both VarInts), then slide the headers in front of it.
        // zlib's compressBound is n + n/4096 + n/16384 + n/2^25 + 13; this covers it.
        var compressed = ArrayPool<byte>.Shared.Rent(body.Length + (body.Length >> 10) + 64 + 10);
        int compressedLength;
        unsafe
        {
            fixed (byte* p = compressed)
            {
                using var output = new UnmanagedMemoryStream(p + 10, 0, compressed.Length - 10, FileAccess.Write);
                using (var z = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    z.Write(body);
                compressedLength = (int)output.Length;
            }
        }
        Span<byte> header = stackalloc byte[10];
        var dataLengthSize = WriteVarInt(header, body.Length);
        Span<byte> prefix = stackalloc byte[5];
        var prefixSize = WriteVarInt(prefix, dataLengthSize + compressedLength);
        var start = 10 - dataLengthSize - prefixSize;
        prefix[..prefixSize].CopyTo(compressed.AsSpan(start));
        header[..dataLengthSize].CopyTo(compressed.AsSpan(start + prefixSize));
        if (start != 0) Buffer.BlockCopy(compressed, start, compressed, 0, prefixSize + dataLengthSize + compressedLength);
        frame = compressed;
        return prefixSize + dataLengthSize + compressedLength;
    }

    // ---------------------------------------------------------------- VarInt helpers

    public static int VarIntSize(int value)
    {
        var v = (uint)value;
        var n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }

    private static int WriteVarInt(byte[] buffer, int offset, int value) => WriteVarInt(buffer.AsSpan(offset), value);

    private static int WriteVarInt(Span<byte> span, int value)
    {
        var v = (uint)value;
        var n = 0;
        while (v >= 0x80)
        {
            span[n++] = (byte)(v | 0x80);
            v >>= 7;
        }
        span[n++] = (byte)v;
        return n;
    }

    public static bool TryReadVarInt(ReadOnlySpan<byte> span, out int value, out int size)
    {
        value = 0;
        for (size = 0; size < 5 && size < span.Length; size++)
        {
            var b = span[size];
            value |= (b & 0x7F) << (7 * size);
            if ((b & 0x80) == 0)
            {
                size++;
                return true;
            }
        }
        if (size >= 5) throw new InvalidDataException("VarInt too long");
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        _socket.Dispose();
        _encrypt?.Dispose();
        _decrypt?.Dispose();
        await Task.CompletedTask;
    }
}
