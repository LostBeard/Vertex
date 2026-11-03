using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// "Build me a house" on ground that is nowhere flat, against a REAL Paper server. Live on AubsCraft 2026-11-03 every project he chose for himself for two hours (castle, house, platform, pen, eight
/// in a row) ended in two seconds with "I can't find a flat, clear spot 5 by 5 blocks around here", and he never moved: the build routine looked for ground that was already level, within 14 blocks,
/// and refused otherwise. TJ: "the entire game world is destructible and buildable; there is zero reason for something to need to be anything when you can build it". The tests before this one all
/// built on a flat floor. Here the ground is stripes of different heights with trees on it: no 7 by 7 anywhere is level, and he has to cut it down and fill it in.
/// </summary>
[NonParallelizable]
public class LiveRoughGroundTests
{
    private const int Port = 25994;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("rough", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful", ["view-distance"] = "8" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("gamerule randomTickSpeed 0");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer() { if (_server != null) await _server.DisposeAsync(); }

    /// <summary>Height above the base (0 to 4) of the 2-wide stripe at column x: a different one every 2 blocks, so no stretch of 7 is level.</summary>
    private static int StripeHeight(int x, int ax) => ((x - ax + 40) / 2 * 7) % 5;

    /// <summary>Rolling ground (a base of stone at y 197 to 199 and dirt stripes up to 4 above it) 61 by 61 around the spawn, three trees, forceloaded first (a fill into unloaded chunks does nothing).</summary>
    private async Task<(MinecraftClient Client, int X, int Z)> JoinOnRoughGroundAsync(string name)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"forceload add {ax - 36} {az - 36} {ax + 36} {az + 36}");
        await Task.Delay(4000);
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        for (var z0 = az - 30; z0 <= az + 30; z0 += 15)
        {
            var z1 = Math.Min(z0 + 14, az + 30);
            await _server.CommandAsync($"fill {ax - 30} 197 {z0} {ax + 30} 199 {z1} minecraft:stone");
            await _server.CommandAsync($"fill {ax - 30} 200 {z0} {ax + 30} 215 {z1} minecraft:air");
        }
        for (var x = ax - 30; x <= ax + 30; x += 2)
        {
            var h = StripeHeight(x, ax);
            if (h > 0) await _server.CommandAsync($"fill {x} 200 {az - 30} {x + 1} {199 + h} {az + 30} minecraft:dirt");
        }
        // Three trees (a four-high trunk, leaves round the top) on the ground as it stands there.
        foreach (var (tx, tz) in new[] { (ax + 3, az + 3), (ax - 6, az + 8), (ax + 9, az - 5) })
        {
            var top = 199 + StripeHeight(tx, ax);
            await _server.CommandAsync($"fill {tx} {top + 1} {tz} {tx} {top + 4} {tz} minecraft:oak_log");
            await _server.CommandAsync($"fill {tx - 1} {top + 3} {tz - 1} {tx + 1} {top + 5} {tz + 1} minecraft:oak_leaves keep");
        }
        var stand = 200 + StripeHeight(ax, ax);
        await _server.CommandAsync($"clear {name}");
        await _server.CommandAsync($"tp {name} {ax + 0.5} {stand} {az + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - stand) < 1.5 && client.OnGround && client.World.IsLoaded(ax + 20, az + 20) && client.World.IsLoaded(ax - 20, az - 20), Timeout, () => $"{client.Position}");
        await client.WaitTicksAsync(20);
        return (client, ax, az);
    }

    /// <summary>The corners of the box that holds every block of a kind within 25 blocks of (ax, az).</summary>
    private static (int X0, int X1, int Z0, int Z1, int MinY, int Count)? Extent(MinecraftClient client, int ax, int az, string block)
    {
        int x0 = int.MaxValue, x1 = int.MinValue, z0 = int.MaxValue, z1 = int.MinValue, minY = int.MaxValue, n = 0;
        for (var x = ax - 25; x <= ax + 25; x++)
        for (var z = az - 25; z <= az + 25; z++)
            for (var y = 197; y < 215; y++)
                if (client.World.GetBlock(x, y, z)?.Name == block) { n++; x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z); minY = Math.Min(minY, y); }
        return n == 0 ? null : (x0, x1, z0, z1, minY, n);
    }

    /// <summary>
    /// "Please mine diamonds" (Aubri, live 2026-11-03): two seconds later "I couldn't find any diamond ore I could reach nearby", without a step. The ore was buried deep (diamonds lie below y 16 and he
    /// stood at y 62): the search was a sphere of 48 blocks, which at that height reaches the diamond level only at its very centre. Here the ore is 36 blocks sideways and 40 down (54 away: outside
    /// the old sphere, inside a 64 block search at any depth) with solid rock between, and he has the pickaxe for it: he must tunnel there and bring the diamond back.
    /// </summary>
    [Test]
    public async Task AskedForBuriedDiamonds_HeDigsDownToThem_InsteadOfSayingHeCannotFindAny()
    {
        const string name = "VertexMiner";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        // Solid rock under the whole stage, down to y 150, in slices (a fill may touch 32,768 blocks), and a vein of diamond ore buried in it.
        for (var z0 = az - 30; z0 <= az + 30; z0 += 8)
            await _server.CommandAsync($"fill {ax - 30} 150 {z0} {ax + 30} 196 {Math.Min(z0 + 7, az + 30)} minecraft:stone");
        var (ox, oy, oz) = (ax + 26, 160, az + 20);
        await _server.CommandAsync($"fill {ox} {oy} {oz} {ox + 1} {oy + 1} {oz} minecraft:diamond_ore");
        await _server.CommandAsync($"give {name} minecraft:iron_pickaxe 1");
        await Wait.UntilAsync(() => client.Inventory.Count("iron_pickaxe") >= 1 && client.World.GetBlock(ox, oy, oz)?.Name == "minecraft:diamond_ore", Timeout, () => client.Inventory.ToString());
        var skills = new Skills(client);
        var (px, py, pz) = client.Position;
        Assert.That(client.World.FindBlocks(s => s > 0 && client.World.Data.BlockOf(s).Name == "minecraft:diamond_ore", (px, py, pz), 48, 8), Is.Empty, "the setup: the old search (a sphere of 48) finds nothing");
        Assert.That(client.World.FindBlocksTall(s => s > 0 && client.World.Data.BlockOf(s).Name == "minecraft:diamond_ore", (px, py, pz), 64, 8), Is.Not.Empty, "but it is within 64 blocks sideways, at depth");

        (bool ok, string text) result;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var c = new CancellationTokenSource(TimeSpan.FromMinutes(6))) result = await skills.CollectAsync("diamond ore", 1, c.Token);
        TestContext.Out.WriteLine($"{result.ok}: {result.text}  ({sw.Elapsed.TotalSeconds:0} s, now at {client.Position})");
        Assert.That(result.ok, Is.True, result.text);
        Assert.That(client.Inventory.Count("diamond"), Is.GreaterThanOrEqualTo(1), "a diamond is in his pockets: " + client.Inventory);
    }

    [Test]
    public async Task ABuild_OnGroundThatIsNowhereFlat_LevelsItAndBuilds_InsteadOfRefusing()
    {
        const string name = "VertexRough";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        var skills = new Skills(client);
        var (px, py, pz) = client.Position;
        // The setup that made him refuse: not a 5 by 5 level and clear place within 14 blocks.
        Assert.That(skills.FindFlatRect(px, py, pz, 5, 5, 8, null), Is.Null, "the setup: nowhere flat and clear within 14 blocks (this is what the old code refused on)");
        await _server.CommandAsync($"give {name} minecraft:cobblestone 300");
        await Wait.UntilAsync(() => client.Inventory.Count("cobblestone") >= 300, Timeout, () => client.Inventory.ToString());

        (bool ok, string text) result;
        using (var c = new CancellationTokenSource(TimeSpan.FromMinutes(5))) result = await skills.BuildShapeAsync("house", 0, 0, 0, "cobblestone", null, null, null, c.Token);
        TestContext.Out.WriteLine($"{result.ok}: {result.text}");
        Assert.That(result.ok, Is.True, result.text);
        Assert.That(result.text, Does.Contain("levelled"), "it says it made the ground level first: " + result.text);

        // The house stands on solid, level ground: its lowest layer is at one height and everything under that layer, over its whole footprint, is solid.
        var e = Extent(client, ax, az, "minecraft:cobblestone");
        Assert.That(e, Is.Not.Null, "the cobblestone is in the world");
        var (x0, x1, z0, z1, minY, count) = e!.Value;
        TestContext.Out.WriteLine($"cobblestone box x {x0}..{x1}, z {z0}..{z1}, lowest y {minY}, {count} blocks");
        Assert.That(x1 - x0, Is.LessThanOrEqualTo(7), "one house, not a smear of blocks");
        Assert.That(z1 - z0, Is.LessThanOrEqualTo(7));
        // Some of the fill is cobblestone from his pockets: the house's own base is the lowest layer with a ring of walls (12 or more blocks), not the lowest block.
        var baseY = Enumerable.Range(minY, 12).First(y => Enumerable.Range(x0, x1 - x0 + 1).SelectMany(x => Enumerable.Range(z0, z1 - z0 + 1).Select(z => (x, z))).Count(p => client.World.GetBlock(p.x, y, p.z)?.Name == "minecraft:cobblestone") >= 12);
        TestContext.Out.WriteLine($"the walls start at y {baseY}");
        var hollows = new List<string>();
        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
        {
            var below = client.World.GetState(x, baseY - 1, z);
            if (below <= 0 || !client.World.Data.HasCollision(below)) hollows.Add($"{x},{z}");
        }
        Assert.That(hollows, Is.Empty, "nothing hollow under the house: " + string.Join(" ", hollows));
        Assert.That(client.World.GetBlock(x0, baseY, z0)?.Name, Is.EqualTo("minecraft:cobblestone"), "the corner of the walls stands on the level");
        // Inside: no hill, no trunk, no leaves left in the middle of the house.
        int mx = (x0 + x1) / 2, mz = (z0 + z1) / 2;
        var leftovers = new List<string>();
        for (var x = mx - 1; x <= mx + 1; x++)
        for (var z = mz - 1; z <= mz + 1; z++)
            for (var y = baseY; y < baseY + 3; y++)
            {
                var n = client.World.GetBlock(x, y, z)?.Name ?? "?";
                if (n is "minecraft:dirt" or "minecraft:oak_log" or "minecraft:oak_leaves" or "minecraft:stone") leftovers.Add($"{n} at {x},{y},{z}");
            }
        Assert.That(leftovers, Is.Empty, "the hill and the trees are cut away inside: " + string.Join("; ", leftovers));

        // And he is not shut in his own house (live 2026-11-03: 8 seconds after "I built it" the stuck-on-a-small-patch reflex dug him out through the wall: he was inside, with 9 blocks of floor).
        var (bx, by, bz) = client.Position;
        var inside = bx > x0 && bx < x1 + 1 && bz > z0 && bz < z1 + 1 && by < baseY + 4;
        TestContext.Out.WriteLine($"he ends at {bx:0.0} {by:0.0} {bz:0.0}; the house is x {x0}..{x1}, z {z0}..{z1}");
        Assert.That(inside, Is.False, "he finished the house and walked out of it");
        Assert.That(skills.WalkablePatch().Count, Is.GreaterThanOrEqualTo(Skills.TinyPatch), "and stands on open ground, not in a small closed space");
    }
}
