using System.Text;
using SpawnDev.Minecraft.Protocol;

namespace SpawnDev.Minecraft.Nbt;

/// <summary>Reads network NBT (1.20.2+: the root tag has a type byte but no name).</summary>
public static class NbtReader
{
    private const int MaxDepth = 512; // vanilla's NbtAccounter depth limit

    public static NbtTag? ReadNetwork(ref PacketReader r)
    {
        var type = (NbtType)r.ReadByte();
        return type == NbtType.End ? null : ReadPayload(ref r, type, 0);
    }

    public static void SkipNetwork(ref PacketReader r)
    {
        var type = (NbtType)r.ReadByte();
        if (type != NbtType.End) SkipPayload(ref r, type, 0);
    }

    private static NbtTag ReadPayload(ref PacketReader r, NbtType type, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("NBT nested too deep");
        switch (type)
        {
            case NbtType.Byte: return new NbtByte(r.ReadSByte());
            case NbtType.Short: return new NbtShort(r.ReadShort());
            case NbtType.Int: return new NbtInt(r.ReadInt());
            case NbtType.Long: return new NbtLong(r.ReadLong());
            case NbtType.Float: return new NbtFloat(r.ReadFloat());
            case NbtType.Double: return new NbtDouble(r.ReadDouble());
            case NbtType.ByteArray: return new NbtByteArray(r.ReadBytes(Count(ref r)).ToArray());
            case NbtType.String: return new NbtString(ReadModifiedUtf8(ref r));
            case NbtType.List:
            {
                var element = (NbtType)r.ReadByte();
                var count = Count(ref r);
                var items = new List<NbtTag>(Math.Min(count, 4096));
                for (var i = 0; i < count; i++) items.Add(ReadPayload(ref r, element, depth + 1));
                return new NbtList(element, items);
            }
            case NbtType.Compound:
            {
                var entries = new Dictionary<string, NbtTag>();
                while (true)
                {
                    var t = (NbtType)r.ReadByte();
                    if (t == NbtType.End) return new NbtCompound(entries);
                    var name = ReadModifiedUtf8(ref r);
                    entries[name] = ReadPayload(ref r, t, depth + 1);
                }
            }
            case NbtType.IntArray:
            {
                var count = Count(ref r);
                var a = new int[count];
                for (var i = 0; i < count; i++) a[i] = r.ReadInt();
                return new NbtIntArray(a);
            }
            case NbtType.LongArray:
            {
                var count = Count(ref r);
                var a = new long[count];
                for (var i = 0; i < count; i++) a[i] = r.ReadLong();
                return new NbtLongArray(a);
            }
            default: throw new InvalidDataException($"unknown NBT tag type {(byte)type}");
        }
    }

    private static void SkipPayload(ref PacketReader r, NbtType type, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("NBT nested too deep");
        switch (type)
        {
            case NbtType.Byte: r.ReadBytes(1); break;
            case NbtType.Short: r.ReadBytes(2); break;
            case NbtType.Int: case NbtType.Float: r.ReadBytes(4); break;
            case NbtType.Long: case NbtType.Double: r.ReadBytes(8); break;
            case NbtType.ByteArray: r.ReadBytes(Count(ref r)); break;
            case NbtType.String: r.ReadBytes(r.ReadUShort()); break;
            case NbtType.IntArray: r.ReadBytes(checked(Count(ref r) * 4)); break;
            case NbtType.LongArray: r.ReadBytes(checked(Count(ref r) * 8)); break;
            case NbtType.List:
            {
                var element = (NbtType)r.ReadByte();
                var count = Count(ref r);
                for (var i = 0; i < count; i++) SkipPayload(ref r, element, depth + 1);
                break;
            }
            case NbtType.Compound:
                while (true)
                {
                    var t = (NbtType)r.ReadByte();
                    if (t == NbtType.End) break;
                    r.ReadBytes(r.ReadUShort());
                    SkipPayload(ref r, t, depth + 1);
                }
                break;
            default: throw new InvalidDataException($"unknown NBT tag type {(byte)type}");
        }
    }

    private static int Count(ref PacketReader r)
    {
        var n = r.ReadInt();
        if (n < 0 || n > r.Remaining) throw new InvalidDataException($"NBT length {n} exceeds the {r.Remaining} bytes left");
        return n;
    }

    /// <summary>
    /// Java's modified UTF-8 (DataInput.readUTF): NUL is 0xC0 0x80 and characters outside the BMP are two
    /// 3-byte surrogates. Plain ASCII/BMP text takes the UTF-8 fast path.
    /// </summary>
    internal static string ReadModifiedUtf8(ref PacketReader r)
    {
        var bytes = r.ReadBytes(r.ReadUShort());
        var plain = true;
        foreach (var b in bytes)
        {
            // 0xC0 0x80 (NUL) and 0xED (surrogate halves) are the only forms standard UTF-8 decodes differently.
            if (b == 0xC0 || b == 0xED) { plain = false; break; }
        }
        if (plain) return Encoding.UTF8.GetString(bytes);

        var sb = new StringBuilder(bytes.Length);
        for (var i = 0; i < bytes.Length;)
        {
            int c = bytes[i];
            if (c < 0x80) { sb.Append((char)c); i++; }
            else if ((c & 0xE0) == 0xC0 && i + 1 < bytes.Length)
            {
                sb.Append((char)(((c & 0x1F) << 6) | (bytes[i + 1] & 0x3F)));
                i += 2;
            }
            else if ((c & 0xF0) == 0xE0 && i + 2 < bytes.Length)
            {
                sb.Append((char)(((c & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F)));
                i += 3;
            }
            else throw new InvalidDataException("malformed modified UTF-8 in NBT string");
        }
        return sb.ToString();
    }
}
