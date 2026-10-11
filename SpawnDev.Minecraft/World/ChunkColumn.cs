using SpawnDev.Minecraft.Nbt;
using SpawnDev.Minecraft.Protocol;

namespace SpawnDev.Minecraft.World;

/// <summary>A block entity (chest, sign, furnace...) as sent with its chunk or by block_entity_data.</summary>
public sealed record BlockEntity(int X, int Y, int Z, int TypeId, NbtTag? Data);

/// <summary>A loaded 16-wide chunk column, MinY..MinY+Height-1 in sections of 16.</summary>
public sealed class ChunkColumn
{
    public ChunkColumn(int x, int z, int minY, ChunkSection[] sections)
    {
        X = x;
        Z = z;
        MinY = minY;
        Sections = sections;
    }

    public int X { get; }
    public int Z { get; }
    public int MinY { get; }
    public ChunkSection[] Sections { get; }
    public Dictionary<(int X, int Y, int Z), BlockEntity> BlockEntities { get; } = [];

    /// <summary>World coordinates; returns air (0) above or below the world.</summary>
    public int Get(int x, int y, int z)
    {
        var s = (y - MinY) >> 4;
        if ((uint)s >= (uint)Sections.Length) return 0;
        return Sections[s].Get(x & 15, (y - MinY) & 15, z & 15);
    }

    public void Set(int x, int y, int z, int state)
    {
        var s = (y - MinY) >> 4;
        if ((uint)s >= (uint)Sections.Length) return;
        Sections[s].Set(x & 15, (y - MinY) & 15, z & 15, state);
    }

    /// <summary>Parses level_chunk_with_light up to (not including) the light data, which Vertex does not use.</summary>
    internal static ChunkColumn Read(ref PacketReader r, int minY, int height, Data.GameData gameData)
    {
        var x = r.ReadInt();
        var z = r.ReadInt();
        // Heightmaps (1.21.5: VarInt count of (type VarInt, VarInt-counted longs)).
        var heightmaps = r.ReadVarInt();
        for (var i = 0; i < heightmaps; i++)
        {
            r.ReadVarInt();
            r.ReadBytes(checked(r.ReadVarInt() * 8));
        }
        var data = r.ReadByteArray();
        var sections = new ChunkSection[height >> 4];
        var sr = new PacketReader(data);
        var padding = 0;
        for (var i = 0; i < sections.Length; i++) sections[i] = ChunkSection.Read(ref sr, ref padding, gameData);
        // Exactly the reserved-but-unwritten bytes must remain, all zero: anything else is a misparse.
        if (sr.Remaining != padding || sr.Rest.IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException($"chunk {x},{z}: {sr.Remaining} bytes left after {sections.Length} sections (expected {padding} zero bytes)");

        var column = new ChunkColumn(x, z, minY, sections);
        var count = r.ReadVarInt();
        for (var i = 0; i < count; i++)
        {
            var xz = r.ReadByte();
            var y = r.ReadShort();
            var type = r.ReadVarInt();
            var nbt = r.ReadNbt();
            var bx = (x << 4) + (xz >> 4);
            var bz = (z << 4) + (xz & 15);
            column.BlockEntities[(bx, y, bz)] = new BlockEntity(bx, y, bz, type, nbt);
        }
        return column;
    }
}
