using SpawnDev.Minecraft.Protocol;

namespace SpawnDev.Minecraft.World;

/// <summary>
/// A 16x16x16 block section. States live in a flat ushort array (index y&lt;&lt;8 | z&lt;&lt;4 | x) because physics and
/// pathfinding read blocks millions of times; a section that is one state throughout (air above the ground)
/// keeps no array at all.
/// </summary>
public sealed class ChunkSection
{
    private ushort[]? _states;
    private ushort _single;
    private byte[]? _biomes; // 4x4x4 cells; null = single
    private byte _singleBiome;

    public int NonAirCount { get; internal set; }

    public bool IsUniform => _states == null;

    public int Get(int x, int y, int z) => _states == null ? _single : _states[(y << 8) | (z << 4) | x];

    public void Set(int x, int y, int z, int state)
    {
        if (_states == null)
        {
            if (state == _single) return;
            _states = new ushort[4096];
            Array.Fill(_states, _single);
        }
        _states[(y << 8) | (z << 4) | x] = (ushort)state;
    }

    /// <summary>The biome (registry id) of the 4x4x4 cell containing block x,y,z (section-local).</summary>
    public int Biome(int x, int y, int z) => _biomes == null ? _singleBiome : _biomes[((y >> 2) << 4) | ((z >> 2) << 2) | (x >> 2)];

    /// <summary>
    /// Reads one section as sent in a chunk packet (1.21.5: block count, states, biomes). Adds to padding the
    /// bytes the server reserved for each container but did not write (see ReadContainer).
    /// </summary>
    internal static ChunkSection Read(ref PacketReader r, ref int padding)
    {
        var section = new ChunkSection { NonAirCount = r.ReadShort() };
        padding += ReadContainer(ref r, 4096, maxIndirectBits: 8, out section._states, out var single);
        // Raw ids, as the server sent them: GameWorld maps them through the current table on every read, so what Vertex
        // learns about a modded block (from a modpack, or by touching it) applies at once to blocks already loaded.
        section._single = (ushort)single;
        padding += ReadContainer(ref r, 64, maxIndirectBits: 3, out var biomes, out var singleBiome);
        if (biomes != null)
        {
            section._biomes = new byte[64];
            for (var i = 0; i < 64; i++) section._biomes[i] = (byte)biomes[i];
        }
        section._singleBiome = (byte)singleBiome;
        return section;
    }

    /// <summary>
    /// A paletted container (PalettedContainer.write, read from the 1.21.5 bytecode): bits byte; 0 = one value
    /// (VarInt); 1..maxIndirect = a VarInt palette, entries are palette indexes; more = global ids. Then the
    /// packed longs, a FIXED-size array since 1.21.5 (no length prefix), entries never straddle two longs.
    /// Returns the size of the VarInt length the 1.21.5 server still RESERVES per container (its
    /// PalettedContainer$Data.getSerializedSize adds VarInt.getByteSize(longs) although write no longer writes it),
    /// so the chunk buffer ends in that many zero bytes.
    /// </summary>
    private static int ReadContainer(ref PacketReader r, int entries, int maxIndirectBits, out ushort[]? values, out int single)
    {
        int bits = r.ReadByte();
        values = null;
        single = 0;
        if (bits == 0)
        {
            single = r.ReadVarInt();
            return 1; // VarInt.getByteSize(0)
        }
        int[]? palette = null;
        if (bits <= maxIndirectBits)
        {
            palette = new int[r.ReadVarInt()];
            for (var i = 0; i < palette.Length; i++) palette[i] = r.ReadVarInt();
        }
        var perLong = 64 / bits;
        var longs = (entries + perLong - 1) / perLong;
        var data = r.ReadBytes(longs * 8);
        var mask = (1UL << bits) - 1;
        values = new ushort[entries];
        var index = 0;
        for (var l = 0; l < longs && index < entries; l++)
        {
            var word = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data.Slice(l * 8, 8));
            for (var k = 0; k < perLong && index < entries; k++, index++)
            {
                var v = (int)(word >> (k * bits) & mask);
                if (palette != null)
                {
                    if (v >= palette.Length) throw new InvalidDataException($"palette index {v} of {palette.Length}");
                    v = palette[v];
                }
                values[index] = (ushort)v;
            }
        }
        if (palette is { Length: 1 })
        {
            single = palette[0];
            values = null;
        }
        return Net.MinecraftConnection.VarIntSize(longs);
    }
}
