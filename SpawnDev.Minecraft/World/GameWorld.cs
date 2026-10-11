using System.Collections.Concurrent;
using SpawnDev.Minecraft.Data;

namespace SpawnDev.Minecraft.World;

/// <summary>
/// The blocks the client knows about: the chunk columns the server has sent for the current dimension.
/// Written by the receive loop, read by anything (physics, pathfinding, the brain).
/// </summary>
public sealed class GameWorld
{
    /// <summary>GetState's answer for a block in a chunk the server has not sent (or has unloaded).</summary>
    public const int Unloaded = -1;

    private readonly ConcurrentDictionary<long, ChunkColumn> _columns = new();

    /// <summary>The block tables for this server: vanilla, or vanilla plus a modded server's blocks (set on joining).</summary>
    public GameData Data { get; internal set; } = GameData.Vanilla;
    public string Dimension { get; internal set; } = "minecraft:overworld";
    public int MinY { get; internal set; } = -64;
    public int Height { get; internal set; } = 384;
    public int MaxY => MinY + Height - 1;

    public long DayTime { get; internal set; }
    public long GameTime { get; internal set; }
    public (int X, int Y, int Z) Spawn { get; internal set; }

    public int LoadedChunks => _columns.Count;
    public IEnumerable<ChunkColumn> Columns => _columns.Values;

    /// <summary>Raised on the receive loop when a block changes (not for whole chunk loads).</summary>
    public event Action<int, int, int, int, int>? BlockChanged; // x, y, z, old, new

    private static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;

    public ChunkColumn? Column(int chunkX, int chunkZ) => _columns.GetValueOrDefault(Key(chunkX, chunkZ));

    public bool IsLoaded(int x, int z) => _columns.ContainsKey(Key(x >> 4, z >> 4));

    /// <summary>The block state at a world position; air outside the world's height, Unloaded where unknown.</summary>
    public int GetState(int x, int y, int z)
    {
        if (y < MinY || y > MaxY) return 0;
        return _columns.TryGetValue(Key(x >> 4, z >> 4), out var c) ? c.Get(x, y, z) : Unloaded;
    }

    public BlockType? GetBlock(int x, int y, int z)
    {
        var s = GetState(x, y, z);
        return s < 0 ? null : Data.BlockOf(s);
    }

    internal void SetColumn(ChunkColumn column) => _columns[Key(column.X, column.Z)] = column;

    internal void Unload(int chunkX, int chunkZ) => _columns.TryRemove(Key(chunkX, chunkZ), out _);

    internal void Clear() => _columns.Clear();

    internal void SetState(int x, int y, int z, int state)
    {
        state = Data.Known(state); // modded states -> the solid unknown state
        if (!_columns.TryGetValue(Key(x >> 4, z >> 4), out var c)) return;
        var old = c.Get(x, y, z);
        if (old == state) return;
        c.Set(x, y, z, state);
        BlockChanged?.Invoke(x, y, z, old, state);
    }

    /// <summary>
    /// Every loaded position of a block (by name), nearest first, within maxDistance of a point. A whole-world
    /// scan of the sections that can contain it (uniform sections are skipped unless they are that block).
    /// </summary>
    public List<(int X, int Y, int Z)> FindBlocks(Func<int, bool> stateMatches, (double X, double Y, double Z) from, double maxDistance, int maxCount = 64)
    {
        var found = new List<(int X, int Y, int Z, double D)>();
        var max2 = maxDistance * maxDistance;
        foreach (var column in _columns.Values)
        {
            var cx = column.X << 4;
            var cz = column.Z << 4;
            // Skip whole columns out of range.
            var dx = Math.Max(Math.Max(cx - from.X, from.X - (cx + 16)), 0);
            var dz = Math.Max(Math.Max(cz - from.Z, from.Z - (cz + 16)), 0);
            if (dx * dx + dz * dz > max2) continue;
            for (var s = 0; s < column.Sections.Length; s++)
            {
                var section = column.Sections[s];
                var sy = column.MinY + (s << 4);
                var dy = Math.Max(Math.Max(sy - from.Y, from.Y - (sy + 16)), 0);
                if (dx * dx + dy * dy + dz * dz > max2) continue;
                if (section.IsUniform && !stateMatches(section.Get(0, 0, 0))) continue;
                for (var y = 0; y < 16; y++)
                for (var z = 0; z < 16; z++)
                for (var x = 0; x < 16; x++)
                {
                    if (!stateMatches(section.Get(x, y, z))) continue;
                    double ex = cx + x + 0.5 - from.X, ey = sy + y + 0.5 - from.Y, ez = cz + z + 0.5 - from.Z;
                    var d = ex * ex + ey * ey + ez * ez;
                    if (d <= max2) found.Add((cx + x, sy + y, cz + z, d));
                }
            }
        }
        found.Sort((a, b) => a.D.CompareTo(b.D));
        return found.Take(maxCount).Select(f => (f.X, f.Y, f.Z)).ToList();
    }
}
