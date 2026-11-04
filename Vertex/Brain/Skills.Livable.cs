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
        var biome = BiomeHere;
        if (biome != _biomeNow) { _biomeNow = biome; _biomeSince = DateTime.UtcNow; }
        if (biome == null || !client.OnGround || InWaterNow()) return;
        // Only once he has been here a while: the number takes some seconds to follow him, so a reading on arrival belongs to where he came from. Walking from the savanna into the plains wrote 47 against land that reads 25, and he
        // then avoided the one place he could live (2026-11-04).
        if (DateTime.UtcNow - _biomeSince < BiomeSettles) return;
        lock (_landHeat)
            if (!_landHeat.TryGetValue(biome, out var known) || degrees >= known.Degrees || DateTime.UtcNow - known.At > TimeSpan.FromMinutes(10))
                _landHeat[biome] = (degrees, DateTime.UtcNow);
        if (HotAt?.Invoke() is double hotAt && degrees < hotAt)
        {
            var (x, _, z) = client.FeetBlock;
            lock (_coolLand) _coolLand[(x >> 4, z >> 4)] = DateTime.UtcNow;
        }
    }

    /// <summary>How long he must have been in a biome before a reading counts for it.</summary>
    public static TimeSpan BiomeSettles { get; set; } = TimeSpan.FromSeconds(15);

    private string? _biomeNow;
    private DateTime _biomeSince = DateTime.MinValue;
    private readonly Dictionary<(int X, int Z), DateTime> _coolLand = [];

    /// <summary>The nearest land he has stood on and found cool, however far (he has gone 300 blocks from the island and found it): where he comes back to, not a random direction over the sea. Null when he knows none.</summary>
    public (int X, int Z)? NearestCoolLandKnown()
    {
        var (x, _, z) = client.FeetBlock;
        lock (_coolLand)
            return _coolLand.Keys.Select(c => (X: (c.X << 4) + 8, Z: (c.Z << 4) + 8)).OrderBy(c => (c.X - x) * (c.X - x) + (c.Z - z) * (c.Z - z)).Cast<(int X, int Z)?>().FirstOrDefault();
    }

    /// <summary>Has he found this biome's land too hot to live on?</summary>
    public bool KnownTooHot(string? biome, double hotAt)
    {
        if (biome == null) return false;
        lock (_landHeat) return _landHeat.TryGetValue(biome, out var known) && known.Degrees >= hotAt;
    }

    /// <summary>
    /// In the water with no land near: nothing a player does at this stage of the game can be done here. It comes up by itself: the open sea is cool and safe by day, so nothing hurts him and nothing moves him on, and live 2026-11-04 he spent 21
    /// minutes unharmed, 650 blocks from anywhere, digging up the sea floor and walking search legs that brought him back where he started.
    /// </summary>
    public bool AtSea
    {
        get
        {
            if (!InWaterNow()) return false;
            var (x, y, z) = client.FeetBlock;
            foreach (var d in new[] { 8, 16, 24 })
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) })
                    if (LandAt(x + dx * d, z + dz * d, y) != null) return false;
            return true;
        }
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

    private readonly Dictionary<(int X, int Z), DateTime> _searchedLand = [];

    /// <summary>
    /// Goes and looks for a land animal where land animals are: the nearest land he can see and has not been over lately, not known to be too hot. The plain search walks fixed compass legs whatever is under him, and he spent 19 minutes
    /// at sea looking for sheep with plains in sight (2026-11-04). Nothing of the kind in sight, it does what the plain search does.
    /// </summary>
    private async Task<(bool Found, int Legs, double Walked)> LookOverLandAsync(Func<bool> found, int maxLegs, CancellationToken ct)
    {
        var legs = 0;
        double walked = 0;
        while (!found() && legs < maxLegs && !ct.IsCancellationRequested)
        {
            legs++;
            var (bx, by, bz) = client.FeetBlock;
            var now = DateTime.UtcNow;
            (int X, int Z)? best = null;
            var bestD = double.MaxValue;
            lock (_searchedLand)
            {
                foreach (var old in _searchedLand.Where(c => now - c.Value > TimeSpan.FromMinutes(20)).Select(c => c.Key).ToList()) _searchedLand.Remove(old);
                const int reach = 160;
                for (var dx = -reach; dx <= reach; dx += 8)
                for (var dz = -reach; dz <= reach; dz += 8)
                {
                    var d = Math.Sqrt(dx * dx + dz * dz);
                    if (d >= bestD || d < 24) continue;       // what is closer than that he can already see the animals on
                    int x = bx + dx, z = bz + dz;
                    if (_searchedLand.ContainsKey((x >> 5, z >> 5)) || LandAt(x, z, by) is not int y) continue;
                    if (HotAt?.Invoke() is double h && KnownTooHot(BiomeAt(x, y, z), h)) continue;
                    best = (x, z);
                    bestD = d;
                }
            }
            if (best is not { } goal)
            {
                var (_, plainLegs, plainWalked) = await LookFurtherAsync(found, 1, ct);
                walked += plainWalked;
                if (plainLegs == 0) break;
                continue;
            }
            Trace?.Invoke($"looking on the land at {goal.X} {goal.Z}");
            var before = client.Position;
            for (var hop = 0; hop < 6 && !found(); hop++)
            {
                var (px, _, pz) = client.Position;
                using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
                step.CancelAfter(TimeSpan.FromSeconds(6));
                var there = false;
                try { there = await client.GoToAsync(Goal.XZ(goal.X, goal.Z), new PathOptions { CanDig = true, MaxNodes = 20_000 }, step.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                var (nx, _, nz) = client.Position;
                if (there || Math.Sqrt((nx - px) * (nx - px) + (nz - pz) * (nz - pz)) < 1) break;
            }
            var after = client.Position;
            walked += Math.Sqrt((after.X - before.X) * (after.X - before.X) + (after.Z - before.Z) * (after.Z - before.Z));
            lock (_searchedLand) _searchedLand[(goal.X >> 5, goal.Z >> 5)] = DateTime.UtcNow;
        }
        return (found(), legs, walked);
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
                // (On a server that shows no temperature there is no heat to wait for: any land will do, and this search is just getting ashore.)
                if (client.OnGround && !InWaterNow() && (t is not double now || now < hotAt) && !KnownTooHot(here, hotAt))
                {
                    if (settledSince == DateTime.MinValue) settledSince = DateTime.UtcNow;
                    if (t == null || DateTime.UtcNow - settledSince >= TimeSpan.FromSeconds(30)) { _heading = null; return (true, t is double read ? $"I can live here: {read:0.#} degrees in the {here?.Replace('_', ' ')}." : $"I'm ashore, in the {here?.Replace('_', ' ')}."); }
                    await client.WaitTicksAsync(20, ct);
                    continue;
                }
                settledSince = DateTime.MinValue;
                var (fx, _, fz) = client.Position;
                var land = NextLivableLand(hotAt, given);
                (int X, int Z) goal;
                if (land is { } l) goal = l;
                else if (NearestCoolLandKnown() is { } home) goal = home;
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
