using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace SpawnDev.Minecraft.Protocol;

/// <summary>
/// Builds one packet body (id + fields) in a pooled buffer. Minecraft's wire format is big-endian with VarInt
/// lengths. Dispose returns the buffer to the pool.
/// </summary>
public sealed class PacketWriter : IDisposable
{
    private byte[] _buffer;
    private int _length;

    public PacketWriter(int packetId, int capacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 16));
        WriteVarInt(packetId);
    }

    /// <summary>The packet so far: VarInt id followed by the fields.</summary>
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    public int Length => _length;

    private Span<byte> Take(int count)
    {
        if (_length + count > _buffer.Length)
        {
            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + count));
            _buffer.AsSpan(0, _length).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }
        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public PacketWriter WriteVarInt(int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            Take(1)[0] = (byte)(v | 0x80);
            v >>= 7;
        }
        Take(1)[0] = (byte)v;
        return this;
    }

    public PacketWriter WriteVarLong(long value)
    {
        var v = (ulong)value;
        while (v >= 0x80)
        {
            Take(1)[0] = (byte)(v | 0x80);
            v >>= 7;
        }
        Take(1)[0] = (byte)v;
        return this;
    }

    public PacketWriter WriteBool(bool value) { Take(1)[0] = value ? (byte)1 : (byte)0; return this; }
    public PacketWriter WriteByte(byte value) { Take(1)[0] = value; return this; }
    public PacketWriter WriteSByte(sbyte value) { Take(1)[0] = (byte)value; return this; }
    public PacketWriter WriteShort(short value) { BinaryPrimitives.WriteInt16BigEndian(Take(2), value); return this; }
    public PacketWriter WriteUShort(ushort value) { BinaryPrimitives.WriteUInt16BigEndian(Take(2), value); return this; }
    public PacketWriter WriteInt(int value) { BinaryPrimitives.WriteInt32BigEndian(Take(4), value); return this; }
    public PacketWriter WriteLong(long value) { BinaryPrimitives.WriteInt64BigEndian(Take(8), value); return this; }
    public PacketWriter WriteFloat(float value) { BinaryPrimitives.WriteSingleBigEndian(Take(4), value); return this; }
    public PacketWriter WriteDouble(double value) { BinaryPrimitives.WriteDoubleBigEndian(Take(8), value); return this; }

    public PacketWriter WriteBytes(ReadOnlySpan<byte> bytes) { bytes.CopyTo(Take(bytes.Length)); return this; }

    /// <summary>A VarInt length followed by the bytes.</summary>
    public PacketWriter WriteByteArray(ReadOnlySpan<byte> bytes) => WriteVarInt(bytes.Length).WriteBytes(bytes);

    public PacketWriter WriteString(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        WriteVarInt(count);
        Encoding.UTF8.GetBytes(value, Take(count));
        return this;
    }

    /// <summary>A UUID as two big-endian longs (most significant first), as Java writes it.</summary>
    public PacketWriter WriteUuid(Guid value)
    {
        value.TryWriteBytes(Take(16), bigEndian: true, out _);
        return this;
    }

    /// <summary>A block position packed into one long: x 26 bits, z 26 bits, y 12 bits.</summary>
    public PacketWriter WritePosition(int x, int y, int z) =>
        WriteLong(((long)(x & 0x3FFFFFF) << 38) | ((long)(z & 0x3FFFFFF) << 12) | (long)(y & 0xFFF));

    public void Dispose()
    {
        var b = _buffer;
        _buffer = [];
        _length = 0;
        if (b.Length > 0) ArrayPool<byte>.Shared.Return(b);
    }
}
