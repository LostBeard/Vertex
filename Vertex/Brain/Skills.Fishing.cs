using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Entities;
using SpawnDev.Minecraft.Items;
using SpawnDev.Minecraft.Pathing;

namespace Vertex.Brain;

/// <summary>Fishing: find open water, stand at its edge facing it, cast, wait for the bite, reel in, repeat.</summary>
public sealed partial class Skills
{
    private bool IsWaterAt(int x, int y, int z) => client.World.GetBlock(x, y, z)?.Name == "minecraft:water";

    /// <summary>
    /// Finds a place to fish: a standing cell with open water straight ahead in one of the four directions (water for the next three blocks, the block above the first one open).
    /// Returns the cell to stand in and the yaw that faces the water, nearest first; null when there is none within 16 blocks.
    /// </summary>
    public (int X, int Y, int Z, float Yaw)? FindFishingSpot()
    {
        var w = client.World;
        var (px, py, pz) = client.Position;
        int bx = (int)Math.Floor(px), bz = (int)Math.Floor(pz), by = (int)Math.Floor(py + 1e-6);
        // (dx, dz, yaw): minecraft yaw 0 faces south (+z), 90 west (-x), 180 north, 270 east.
        (int Dx, int Dz, float Yaw)[] dirs = [(0, 1, 0f), (-1, 0, 90f), (0, -1, 180f), (1, 0, 270f)];
        (int X, int Y, int Z, float Yaw, int D2)? best = null;
        for (var dx = -16; dx <= 16; dx++)
        for (var dz = -16; dz <= 16; dz++)
        {
            int x = bx + dx, z = bz + dz;
            if (!w.IsLoaded(x, z)) continue;
            for (var y = by + 3; y >= by - 4; y--)
            {
                if (!Ground(w.GetState(x, y - 1, z)) || !Open(w.GetState(x, y, z)) || !Open(w.GetState(x, y + 1, z))) continue;
                foreach (var (ddx, ddz, yaw) in dirs)
                {
                    if (!w.IsLoaded(x + 4 * ddx, z + 4 * ddz)) continue;
                    // The first block ahead is water one below the standing level (a shore), and so are the next three.
                    var ok = true;
                    for (var k = 1; k <= 4 && ok; k++)
                        ok = IsWaterAt(x + k * ddx, y - 1, z + k * ddz) && Open(w.GetState(x + k * ddx, y, z + k * ddz));
                    // and deep enough to count as open water for a bobber: water below the surface too
                    ok = ok && IsWaterAt(x + 2 * ddx, y - 2, z + 2 * ddz);
                    if (!ok) continue;
                    var d2 = dx * dx + dz * dz;
                    if (best == null || d2 < best.Value.D2) best = (x, y, z, yaw, d2);
                }
                break;      // the first standable level from the top down is the surface here
            }
        }
        return best is { } b ? (b.X, b.Y, b.Z, b.Yaw) : null;
    }

    private Entity? MyBobber() => client.Entities.Values.FirstOrDefault(e => e.Name == "minecraft:fishing_bobber" && e.Data == client.EntityId);

    /// <summary>
    /// Fishes until `count` fish (or anything else) have been caught, or about 90 seconds go by without a bite. Needs a fishing rod. Walks to open water first, casts, waits for the bobber to be
    /// pulled under, and reels in. Says what it caught.
    /// </summary>
    public async Task<(bool, string)> FishAsync(int count, CancellationToken ct)
    {
        count = count <= 0 ? 1 : Math.Min(count, 20);
        if (Have(s => s.Name == "minecraft:fishing_rod") == 0) return (false, "I need a fishing rod to fish. (It is three sticks and two string.)");
        if (FindFishingSpot() is not var (sx, sy, sz, yaw)) return (false, "I can't find open water to fish in near here.");
        await client.GoToAsync(Goal.Near(sx + 0.5, sy, sz + 0.5, 0.45), new PathOptions { MaxNodes = 10_000 }, ct);
        if (!await client.EquipAsync(s => s.Name == "minecraft:fishing_rod", ct)) return (false, "I couldn't get the fishing rod into my hand.");

        var caught = new Dictionary<string, int>();
        var cast = 0;
        var casts = 0;
        var pitch = 20f;       // looking a little down: a level cast flies about 8 blocks, so the first try aims for water 3 to 4 blocks away; adjusted by where the bobber actually lands
        while (cast < count && casts < count * 6 && !ct.IsCancellationRequested)
        {
            casts++;
            client.Look(yaw, pitch);
            await client.WaitTicksAsync(4, ct);
            var before = InventoryCounts();
            await client.UseItemAsync(Hand.Main, ct);
            // The bobber spawns, flies and settles.
            var bobber = await WaitForAsync(MyBobber, 40, ct);
            if (bobber == null) continue;
            await client.WaitTicksAsync(30, ct);
            // It has to land on water, or nothing can bite: reel in and cast again (a different pitch moves where it lands).
            bobber = MyBobber();
            if (bobber == null) continue;
            if (!IsWaterAt((int)Math.Floor(bobber.X), (int)Math.Floor(bobber.Y + 0.1), (int)Math.Floor(bobber.Z)) && !IsWaterAt((int)Math.Floor(bobber.X), (int)Math.Floor(bobber.Y - 0.3), (int)Math.Floor(bobber.Z)))
            {
                await client.UseItemAsync(Hand.Main, ct);       // reel in
                await client.WaitTicksAsync(10, ct);
                // Landed beyond the water: look further down; short of it: look up a little.
                var landed = Math.Sqrt(Math.Pow(bobber.X - client.Position.X, 2) + Math.Pow(bobber.Z - client.Position.Z, 2));
                pitch = Math.Clamp(landed > 4.5 ? pitch + 12f : pitch - 8f, -30f, 70f);
                continue;
            }
            // Wait for the bite: the hook's "biting" flag (metadata 9), or the bobber dragged down.
            var bit = await WaitUntilTicksAsync(() =>
            {
                var b = MyBobber();
                return b != null && (b.Metadata.TryGetValue(9, out var v) && v is true || b.VelocityY < -0.12);
            }, 1800, ct);                                       // 90 s
            if (!bit) { await client.UseItemAsync(Hand.Main, ct); await client.WaitTicksAsync(10, ct); break; }
            await client.UseItemAsync(Hand.Main, ct);           // reel in
            await client.WaitTicksAsync(40, ct);                // the catch flies to us
            var after = InventoryCounts();
            var got = after.Where(kv => kv.Value > before.GetValueOrDefault(kv.Key)).ToList();
            if (got.Count == 0) continue;
            cast++;
            foreach (var (name, n) in got) caught[name] = caught.GetValueOrDefault(name) + n - before.GetValueOrDefault(name);
        }
        if (caught.Count == 0) return (false, "I fished for a while but didn't catch anything.");
        var list = string.Join(", ", caught.Select(kv => $"{kv.Value} {kv.Key.Bare().Replace('_', ' ')}"));
        return (true, $"I caught {list}.");
    }

    private Dictionary<string, int> InventoryCounts()
    {
        var d = new Dictionary<string, int>();
        foreach (var s in client.Inventory.Slots.Skip(PlayerInventory.MainStart))
            if (!s.IsEmpty) d[s.Name] = d.GetValueOrDefault(s.Name) + s.Count;
        return d;
    }

    private async Task<T?> WaitForAsync<T>(Func<T?> get, int ticks, CancellationToken ct) where T : class
    {
        for (var i = 0; i < ticks && !ct.IsCancellationRequested; i++)
        {
            if (get() is { } v) return v;
            await client.WaitTicksAsync(1, ct);
        }
        return get();
    }

    private Task<bool> WaitUntilTicksAsync(Func<bool> condition, int ticks, CancellationToken ct) => client.WaitUntilAsync(condition, ticks, ct);
}
