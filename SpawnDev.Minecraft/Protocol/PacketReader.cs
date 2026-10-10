using System.Buffers.Binary;
using System.Text;
using SpawnDev.Minecraft.Nbt;

namespace SpawnDev.Minecraft.Protocol;

/// <summary>
/// Reads one packet's fields in place from the receive buffer (no copy). Valid only while the packet is being
/// handled: the buffer goes back to the pool afterwards, so anything kept must be parsed out first.
/// </summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public PacketReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public readonly int Position => _pos;
    public readonly int Remaining => _data.Length - _pos;
    public readonly ReadOnlySpan<byte> Rest => _data[_pos..];

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _pos + count > _data.Length)
            throw new InvalidDataException($"packet underflow: need {count} at {_pos} of {_data.Length}");
        var s = _data.Slice(_pos, count);
        _pos += count;
        return s;
    }

    public int ReadVarInt()
    {
        int value = 0, shift = 0;
        while (true)
        {
            var b = Take(1)[0];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift >= 35) throw new InvalidDataException("VarInt too long");
        }
    }

    public long ReadVarLong()
    {
        long value = 0;
        int shift = 0;
        while (true)
        {
            var b = Take(1)[0];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift >= 70) throw new InvalidDataException("VarLong too long");
        }
    }

    public bool ReadBool() => Take(1)[0] != 0;
    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSByte() => (sbyte)Take(1)[0];
    public short ReadShort() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
    public ushort ReadUShort() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public int ReadInt() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public long ReadLong() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
    public float ReadFloat() => BinaryPrimitives.ReadSingleBigEndian(Take(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleBigEndian(Take(8));

    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    /// <summary>A VarInt length followed by that many bytes.</summary>
    public ReadOnlySpan<byte> ReadByteArray() => Take(ReadVarInt());

    public ReadOnlySpan<byte> ReadRest() => Take(Remaining);

    public string ReadString(int maxChars = 32767)
    {
        var count = ReadVarInt();
        if (count > maxChars * 3) throw new InvalidDataException($"string of {count} bytes exceeds {maxChars} chars");
        return Encoding.UTF8.GetString(Take(count));
    }

    public Guid ReadUuid() => new(Take(16), bigEndian: true);

    /// <summary>A packed block position: x 26 bits, z 26 bits, y 12 bits, all signed.</summary>
    public (int X, int Y, int Z) ReadPosition()
    {
        var v = ReadLong();
        return ((int)(v >> 38), (int)(v << 52 >> 52), (int)(v << 26 >> 38));
    }

    /// <summary>Network NBT (no root name). Returns null for an End tag (absent).</summary>
    public NbtTag? ReadNbt() => NbtReader.ReadNetwork(ref this);

    /// <summary>Skips one network NBT value without building it.</summary>
    public void SkipNbt() => NbtReader.SkipNetwork(ref this);
}
