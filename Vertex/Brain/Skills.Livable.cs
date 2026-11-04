using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Pathing;

namespace Vertex.Brain;

/// <summary>
/// Deciding that a place cannot be lived in, and leaving. On mc.spawndev.com he respawns on an island where the server's temperature plugin reads 56.6 by day and takes a heart every 6 seconds, and nothing he does there lasts a minute
/// (14 deaths in 15 minutes, 2026-11-04). A player would not keep trying: he would notice it is the place, and go. Nobody tells him the plugin's rules. What he has is his own readings, and the biome of every chunk he can see: he learns how
/// hot each biome's land is by standing on it, and picks where to go from what he has learned.
/// </summary>
public sealed partial class Skills
{
    /// <summary>The temperature from which land counts as too hot (the brain: what he has learned so far), or null on a server that shows none.</summary>
    public Func<double?>? HotAt { get; set; }

    private readonly Dictionary<string, (double Degrees, DateTime At)> _landHeat = [];
    private (int X, int Z)? _heading;

    /// <summary>The biome at a position, without its namespace ("desert"), or null where nothing is loaded.</summary>
    public string? BiomeAt(int x, int y, int z)
    {
        var id = client.World.GetBiome(x, y, z);
        return id < 0 ? null : client.Registries.Get("minecraft:worldgen/biome", id)?.Key.Bare();
    }

    /// <summary>The biome he stands in.</summary>
    public string? BiomeHere { get { var (x, y, z) = client.FeetBlock; return BiomeAt(x, y, z); } }

    /// <summary>What he has found out about each biome's land, hottest first: for /state, and for saying so.</summary>
    public IReadOnlyList<(string Biome, double Degrees)> BiomeHeat()
    {
        lock (_landHeat) return _landHeat.OrderByDescending(b => b.Value.Degrees).Select(b => (b.Key, b.Value.Degrees)).ToList();
    }

    /// <summary>
    /// Takes one reading for the biome he is standing in. On land only: water reads about ten lower. The hottest reading of the last ten minutes stands, because the number climbs for a while after he arrives (it starts from 25 at a respawn), and
    /// the hours change it: an old one gives way.
    /// </summary>
    private void NoteBiomeHeat(double degrees)
    {
        if (BiomeHere is not { } biome || !client.OnGround || InWaterNow()) return;
        lock (_landHeat)
            if (!_landHeat.TryGetValue(biome, out var known) || degrees >= known.Degrees || DateTime.UtcNow - known.At > TimeSpan.FromMinutes(10))
                _landHeat[biome] = (degrees, DateTime.UtcNow);
    }

    /// <summary>Has he found this biome's land too hot to live on?</summary>
    public bool KnownTooHot(string? biome, double hotAt)
    {
        if (biome == null) return false;
        lock (_landHeat) return _landHeat.TryGetValue(biome, out var known) && known.Degrees >= hotAt;
    }

    /// <summary>The surface at a column when it is land he could stand on: its height, or null for water, nothing, or what is not loaded.</summary>
    private int? LandAt(int x, int z, int aroundY)
    {
        if (!client.World.IsLoaded(x, z)) return null;
        for (var y = aroundY + 24; y >= aroundY - 24; y--)
        {
            var s = client.World.GetState(x, y, z);
            if (s <= 0 || D.IsAir(s)) continue;
            if ((D.StateFlags[s] & (GameData.FlagWater | GameData.FlagLava)) != 0) return null;
            if (!D.HasCollision(s) || (D.StateFlags[s] & GameData.FlagReplaceable) != 0) continue;
            return y;
        }
        return null;
    }

    /// <summary>
    /// The land to try next: the nearest he can see in a biome not known to be too hot, and one he has found cool long before one he knows nothing about. Null when all the land in sight is too hot.
    /// </summary>
    private (int X, int Z)? NextLivableLand(double hotAt, HashSet<(int, int)> given)
    {
        var (bx, by, bz) = client.FeetBlock;
        (int X, int Z)? best = null;
        var bestScore = double.MaxValue;
        const int reach = 160;
        for (var dx = -reach; dx <= reach; dx += 8)
        for (var dz = -reach; dz <= reach; dz += 8)
        {
            int x = bx + dx, z = bz + dz;
            if (given.Contains((x >> 4, z >> 4)) || LandAt(x, z, by) is not int y || BiomeAt(x, y, z) is not { } biome || KnownTooHot(biome, hotAt)) continue;
            var score = Math.Sqrt(dx * dx + dz * dz);
            lock (_landHeat) if (!_landHeat.ContainsKey(biome)) score += 200;
            if (score < bestScore) { bestScore = score; best = (x, z); }
        }
        return best;
    }

    /// <summary>
    /// Leaves for somewhere he can live: walks and swims to land in a biome not known to be too hot, and stays there half a minute to read it. No such land in sight, he keeps going one way over the sea until there is. Up to ten minutes; the
    /// job he was on carries on from there.
    /// </summary>
    public async Task<(bool, string)> FindLivableLandAsync(Func<double?> temperature, double hotAt, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var given = new HashSet<(int, int)>();
        var settledSince = DateTime.MinValue;
        try
        {
            while (DateTime.UtcNow - started < TimeSpan.FromMinutes(10))
            {
                ct.ThrowIfCancellationRequested();
                var here = BiomeHere;
                var t = temperature();
                if (client.OnGround && !InWaterNow() && t is double now && now < hotAt && !KnownTooHot(here, hotAt))
                {
                    if (settledSince == DateTime.MinValue) settledSince = DateTime.UtcNow;
                    if (DateTime.UtcNow - settledSince >= TimeSpan.FromSeconds(30)) { _heading = null; return (true, $"I can live here: {now:0.#} degrees in the {here?.Replace('_', ' ')}."); }
                    await client.WaitTicksAsync(20, ct);
                    continue;
                }
                settledSince = DateTime.MinValue;
                var (fx, _, fz) = client.Position;
                var land = NextLivableLand(hotAt, given);
                (int X, int Z) goal;
                if (land is { } l) goal = l;
                else
                {
                    // Nothing in sight: one way, and stay on it, or he goes in circles round the island. Off the map of what he has loaded is fine: the path goes as far as it knows.
                    _heading ??= new[] { (0, -1), (1, 0), (0, 1), (-1, 0) }[Random.Shared.Next(4)];
                    goal = ((int)fx + _heading.Value.X * 96, (int)fz + _heading.Value.Z * 96);
                }
                Trace?.Invoke($"too hot to live here ({t:0.#} degrees, {here?.Replace('_', ' ')}): heading for {goal.X} {goal.Z}{(land == null ? ", no land in sight" : "")}");
                using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
                step.CancelAfter(TimeSpan.FromSeconds(8));
                try { await client.GoToAsync(Goal.XZ(goal.X, goal.Z), new PathOptions { MaxNodes = 15_000, Sprint = true }, step.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                var (ax, _, az) = client.Position;
                if (Math.Sqrt((ax - fx) * (ax - fx) + (az - fz) * (az - fz)) < 2)
                {
                    // Getting nowhere that way: not that land, or not that direction.
                    if (land != null) given.Add((goal.X >> 4, goal.Z >> 4));
                    else _heading = (-_heading!.Value.Z, _heading.Value.X);
                }
            }
        }
        finally { client.Controls.Clear(); }
        return (false, "I looked for ten minutes and found nowhere I can live.");
    }
}
