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
        _server = await PaperServer.StartAsync("rough", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "easy", ["view-distance"] = "8" });       // not peaceful: that heals him at once and removes the zombies (mob spawning is off below)
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

    /// <summary>Earth four deep under where he stands and two blocks round it (the base everywhere else is stone, which bare hands take 7.5 seconds a block to break).</summary>
    private async Task EarthUnderHimAsync(MinecraftClient client, int ax, int az)
    {
        await _server.CommandAsync($"fill {ax - 2} 196 {az - 2} {ax + 2} 199 {az + 2} minecraft:dirt");
        await Wait.UntilAsync(() => client.World.GetBlock(ax, 199, az)?.Name == "minecraft:dirt" && client.World.GetBlock(ax, 196, az)?.Name == "minecraft:dirt", Timeout, () => $"the earth under him; at {client.Position}");
    }

    /// <summary>
    /// Three blocks of stone with bare hands are 22 seconds of standing still: no way to hide from something that is on him. Digging in is then not on offer (the choice goes to running, fighting,
    /// the water), and with any pickaxe, or on earth, it is. At nightfall, with nothing on him yet, he has 12 seconds for it, which bare hands in stone do not make either.
    /// </summary>
    [Test]
    public async Task DiggingIn_IsOnOffer_OnlyWhereHeCanGetUnderInTime()
    {
        const string name = "VertexHard";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        await _server.CommandAsync($"fill {ax - 2} 190 {az - 2} {ax + 2} 196 {az + 2} minecraft:stone");
        await Wait.UntilAsync(() => client.World.GetBlock(ax, 196, az)?.Name == "minecraft:stone" && client.World.GetBlock(ax, 199, az)?.Name == "minecraft:stone", Timeout, () => $"stone under him; at {client.Position}");
        var skills = new Skills(client);
        Assert.That(skills.DigInProblem(), Does.Contain("too hard"), "stone, bare hands, something on him");
        Assert.That(skills.DigInProblem(Skills.DigInAtNightfallTicks), Does.Contain("too hard"), "nor at nightfall");
        await _server.CommandAsync($"give {name} minecraft:wooden_pickaxe 1");
        await Wait.UntilAsync(() => client.Inventory.Count("wooden_pickaxe") == 1, Timeout, () => client.Inventory.ToString());
        Assert.That(skills.DigInProblem(), Is.Null, "stone with a wooden pickaxe: 3.5 seconds");
        await _server.CommandAsync($"clear {name}");
        await Wait.UntilAsync(() => client.Inventory.Count("wooden_pickaxe") == 0, Timeout, () => client.Inventory.ToString());
        await EarthUnderHimAsync(client, ax, az);
        Assert.That(skills.DigInProblem(), Is.Null, "earth with bare hands: 2.3 seconds");
        // A gap beside where his head would be (a slope, a cave): live 2026-11-16 he was shot in such a hole, sealed in for the night, by a skeleton 4 blocks away.
        await _server.CommandAsync($"setblock {ax + 1} 198 {az} minecraft:air");
        await Wait.UntilAsync(() => client.World.GetBlock(ax + 1, 198, az)?.Name == "minecraft:air", Timeout, () => "the gap");
        Assert.That(skills.DigInProblem(), Does.Contain("open at the side"));
        Assert.That(skills.DigInProblem(Skills.DigInAtNightfallTicks), Does.Contain("open at the side"), "at nightfall too");
        await _server.CommandAsync($"setblock {ax + 1} 198 {az} minecraft:dirt");
        await Wait.UntilAsync(() => client.World.GetBlock(ax + 1, 198, az)?.Name == "minecraft:dirt", Timeout, () => "the gap closed");
        Assert.That(skills.DigInProblem(), Is.Null);
    }

    /// <summary>
    /// Live 2026-11-16, 17:37: he went through a night at 0.3 hearts with a food bar of 5 and two rotten flesh in his pockets, and the first arrow of the morning killed him. Starving with nothing
    /// better, he eats it, as a player does; with his food bar above that he leaves it alone.
    /// </summary>
    [Test]
    public async Task Starving_WithOnlyRottenFlesh_HeEatsIt_AndNotBeforeHeIsStarving()
    {
        const string name = "VertexStarve";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        await _server.CommandAsync($"give {name} minecraft:rotten_flesh 4");
        await Wait.UntilAsync(() => client.Inventory.Count("rotten_flesh") == 4, Timeout, () => client.Inventory.ToString());
        var skills = new Skills(client);
        // Hungry, not starving: he has "no food".
        await _server.CommandAsync($"effect give {name} minecraft:hunger 30 60 true");       // about a point of food every 13 ticks
        await Wait.UntilAsync(() => client.Food <= 14, Timeout, () => $"food {client.Food}");
        await _server.CommandAsync($"effect clear {name}");
        await client.WaitTicksAsync(10);
        if (client.Food > Skills.StarvingAt)
        {
            Assert.That(skills.HasSomethingToEat, Is.False, $"food {client.Food}: not starving, so rotten flesh is not food");
            var (ate, said) = await skills.EatAsync(CancellationToken.None);
            Assert.That(ate, Is.False, said);
            Assert.That(client.Inventory.Count("rotten_flesh"), Is.EqualTo(4));
        }
        // Starving.
        await _server.CommandAsync($"effect give {name} minecraft:hunger 30 60 true");
        await Wait.UntilAsync(() => client.Food <= Skills.StarvingAt, Timeout, () => $"food {client.Food}");
        await _server.CommandAsync($"effect clear {name}");
        await client.WaitTicksAsync(10);
        var before = client.Food;
        Assert.That(skills.HasFood, Is.False, "it is still not what he counts as food to have");
        Assert.That(skills.HasSomethingToEat, Is.True, $"food {before}: starving");
        using var c = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (ok, text) = await skills.EatAsync(c.Token);
        TestContext.Out.WriteLine($"{ok}: {text}; food {before} -> {client.Food}");
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("rotten flesh"));
        Assert.That(client.Inventory.Count("rotten_flesh"), Is.EqualTo(3));
        Assert.That(client.Food, Is.GreaterThan(before), "and it fed him");
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

    /// <summary>
    /// "Shear the sheep" / "milk the cow" (TJ's kids, live): "I don't see any sheep with wool on near me", "I don't see a cow to milk", within 24 blocks only. A sheep and a cow 28 blocks off, with the
    /// shears and the bucket in his pockets: he walks there and does it.
    /// </summary>
    [Test]
    public async Task AnimalsABitFarOff_AreWalkedToAndShearedAndMilked_InsteadOfNotSeen()
    {
        const string name = "VertexFarm";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        var sx = ax + 28;
        var y = 200 + StripeHeight(sx, ax);
        await _server.CommandAsync($"summon minecraft:sheep {sx + 0.5} {y} {az + 0.5} {{NoAI:1b,Tags:[\"vfar\"]}}");
        await _server.CommandAsync($"summon minecraft:cow {sx + 0.5} {y} {az + 3.5} {{NoAI:1b,Tags:[\"vfar\"]}}");
        await _server.CommandAsync($"give {name} minecraft:shears 1");
        await _server.CommandAsync($"give {name} minecraft:bucket 1");
        await Wait.UntilAsync(() => client.Inventory.Count("shears") >= 1 && client.Inventory.Count("bucket") >= 1 && client.Entities.Values.Any(e => e.Name == "minecraft:sheep") && client.Entities.Values.Any(e => e.Name == "minecraft:cow"), Timeout, () => client.Inventory.ToString());
        var skills = new Skills(client);
        try
        {
            (bool ok, string text) shear, milk;
            using (var c = new CancellationTokenSource(TimeSpan.FromMinutes(3))) shear = await skills.ShearSheepAsync(1, c.Token);
            TestContext.Out.WriteLine($"shear: {shear.ok}: {shear.text}");
            Assert.That(shear.ok, Is.True, shear.text);
            using (var c = new CancellationTokenSource(TimeSpan.FromMinutes(3))) milk = await skills.MilkCowAsync(c.Token);
            TestContext.Out.WriteLine($"milk: {milk.ok}: {milk.text}");
            Assert.That(milk.ok, Is.True, milk.text);
        }
        finally { await _server.CommandAsync("kill @e[tag=vfar]"); }
    }

    /// <summary>"Let's go fishing!" with no water within 16 blocks: "I can't find open water to fish in near here". A pond 25 blocks off: he walks there and casts.</summary>
    [Test]
    public async Task Fishing_WithTheWaterFarOff_HeWalksToThePond_InsteadOfSayingThereIsNone()
    {
        const string name = "VertexFisher";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        // A pond 25 blocks east: a 9 by 9 hollow cut 3 deep into the base, filled with water, level with the ground at its edge.
        int px0 = ax + 24, pz0 = az - 4;
        await _server.CommandAsync($"fill {px0} 199 {pz0} {px0 + 8} 215 {pz0 + 8} minecraft:air");
        await _server.CommandAsync($"fill {px0} 196 {pz0} {px0 + 8} 199 {pz0 + 8} minecraft:water");
        await _server.CommandAsync($"fill {px0 - 1} 197 {pz0 - 1} {px0 + 9} 199 {pz0 - 1} minecraft:stone");
        await _server.CommandAsync($"give {name} minecraft:fishing_rod 1");
        await Wait.UntilAsync(() => client.Inventory.Count("fishing_rod") >= 1 && client.World.GetBlock(px0 + 4, 198, pz0 + 4)?.Name == "minecraft:water", Timeout, () => client.Inventory.ToString());
        var skills = new Skills(client);
        Assert.That(skills.FindFishingSpotNear(), Is.False, "the setup: no fishing spot within 16 blocks");
        using var c = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        string text;
        try { text = (await skills.FishAsync(1, c.Token)).Item2; } catch (OperationCanceledException) { text = "(still fishing after 40 s)"; }
        var (x, y, z) = client.Position;
        TestContext.Out.WriteLine($"{text}; at {x:0} {y:0} {z:0}; pond x {px0}..{px0 + 8} z {pz0}..{pz0 + 8}");
        Assert.That(text, Does.Not.Contain("didn't find any"), text);
        Assert.That(Math.Abs(x - (px0 + 4)) + Math.Abs(z - (pz0 + 4)), Is.LessThan(12), "he is at the pond");
    }

    /// <summary>
    /// Live 2026-11-03, 18:04: "HereticSpawn was slain by Phantom", 70 seconds after god mode was turned off: phantoms cannot be walked away from and a swarm cannot be out-fought. At five hearts or
    /// fewer with monsters close he digs three blocks down, seals the hole and waits. Here: three zombies chase him at 6 health points; he ends underground, alive, with the zombies still outside.
    /// </summary>
    [Test]
    public async Task LowOnHealthWithMonstersCloseIn_HeDigsInAndLives()
    {
        const string name = "VertexCover";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        for (var z0 = az - 30; z0 <= az + 30; z0 += 8)
            await _server.CommandAsync($"fill {ax - 30} 150 {z0} {ax + 30} 196 {Math.Min(z0 + 7, az + 30)} minecraft:stone");
        var stand = 200 + StripeHeight(ax, ax);
        await EarthUnderHimAsync(client, ax, az);
        var deaths = 0;
        client.Died += _ => Interlocked.Increment(ref deaths);
        await _server.CommandAsync($"effect give {name} minecraft:instant_damage 1 1");        // 12 points: 20 down to 8
        await Wait.UntilAsync(() => client.Health is > 0 and <= 10, Timeout, () => $"health {client.Health}");
        for (var i = 0; i < 3; i++)
            await _server.CommandAsync($"summon minecraft:zombie {ax + 6.5 + i} {stand} {az + 0.5 + i} {{Tags:[\"vcover\"],active_effects:[{{id:\"minecraft:fire_resistance\",duration:1000000}}]}}");
        await Wait.UntilAsync(() => client.Entities.Values.Count(e => e.Name == "minecraft:zombie") >= 3, Timeout, () => "the zombies");
        var skills = new Skills(client);
        var tasks = new TaskRunner();
        var jobs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        tasks.Starting += jobs.Enqueue;
        using var reflexes = new Reflexes(client, skills, tasks, () => new PlayStyle { Bravery = 0.0 }) { Enabled = true };
        try
        {
            await Wait.UntilAsync(() => client.Position.Y <= stand - 2 || client.Health <= 0, TimeSpan.FromSeconds(40), () => $"at {client.Position}, health {client.Health}; jobs {string.Join(" | ", jobs)}");
            Assert.That(client.Health, Is.GreaterThan(0), "he is alive; jobs " + string.Join(" | ", jobs));
            await Task.Delay(TimeSpan.FromSeconds(8));
            TestContext.Out.WriteLine($"at {client.Position}, health {client.Health}, zombies {client.Entities.Values.Count(e => e.Name == "minecraft:zombie")}; jobs {string.Join(" | ", jobs)}");
            Assert.That(client.Health, Is.GreaterThan(0), "and still alive eight seconds later, underground");
            Assert.That(Volatile.Read(ref deaths), Is.Zero, "and he did not die on the way: health after a respawn is health too");
            Assert.That(client.Position.Y, Is.LessThanOrEqualTo(stand - 2), "under the ground");
            Assert.That(jobs.Any(j => j.StartsWith("taking cover")), Is.True, string.Join(" | ", jobs));
        }
        finally { reflexes.Enabled = false; tasks.Stop(); await _server.CommandAsync("kill @e[tag=vcover]"); }
    }

    /// <summary>
    /// Live 2026-11-06, 12 jobs: "I need a crafting table (and could not make or reach one)", with a table of his own close by and too few planks for another. A table three across, two along and three up
    /// is within reach (3.2 blocks to its nearest point) while its centre is 4.07 away: the old check called it out of reach. Here he makes a stone pickaxe at that table, with 3 planks in his pockets.
    /// </summary>
    [Test]
    public async Task ATableUpAndAcross_InReachButWithItsCentreFurther_IsUsed_NotCalledOutOfReach()
    {
        const string name = "VertexTable";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        await _server.CommandAsync($"fill {ax - 2} 200 {az - 2} {ax + 5} 207 {az + 4} minecraft:air");
        await _server.CommandAsync($"setblock {ax + 3} 203 {az + 2} minecraft:crafting_table");
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5}");
        await _server.CommandAsync($"give {name} minecraft:cobblestone 3");
        await _server.CommandAsync($"give {name} minecraft:stick 2");
        await _server.CommandAsync($"give {name} minecraft:oak_planks 3");
        await Wait.UntilAsync(() => client.OnGround && client.FeetBlock == (ax, 200, az) && client.Inventory.Count("cobblestone") >= 3 && client.Inventory.Count("stick") >= 2 && client.Inventory.Count("oak_planks") >= 3
            && client.World.GetBlock(ax + 3, 203, az + 2)?.Name == "minecraft:crafting_table", Timeout, () => $"at {client.Position}; {client.Inventory}");
        var eye = client.EyePosition;
        var toCentre = Math.Sqrt(Math.Pow(ax + 3.5 - eye.X, 2) + Math.Pow(203.5 - eye.Y, 2) + Math.Pow(az + 2.5 - eye.Z, 2));
        Assert.That(toCentre, Is.GreaterThan(4.0), "the setup: its centre is further than the old check allowed");
        Assert.That(MinecraftClient.WithinReach(eye, ax + 3, 203, az + 2, 3.5), Is.True, "the setup: and it is where a walk to it would stop");
        using var c = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (ok, why) = await client.CraftItemAsync("minecraft:stone_pickaxe", 1, c.Token);
        TestContext.Out.WriteLine($"{ok}: {why}; {client.Inventory}");
        Assert.That(ok, Is.True, why);
        Assert.That(client.Inventory.Count("stone_pickaxe"), Is.EqualTo(1));
        Assert.That(client.Inventory.Count("oak_planks"), Is.EqualTo(3), "at the table that was there: no planks went into another");
    }

    /// <summary>
    /// Live 2026-11-06 and 07: 106 of 151 deaths in 19 hours were at night; he dug in 115 times, waited two minutes at most and came back up into the same night. And the island he comes back to life on is
    /// mostly sand, which cannot be dug into at all. Here: night, standing on sand, earth four blocks away. He goes to the earth, digs in, seals the hole, is still down there while it is night, and comes
    /// up when the sun does.
    /// </summary>
    [Test]
    public async Task AtNight_OnSand_HeGoesToGroundHeCanDigInto_AndStaysBelowUntilMorning()
    {
        const string name = "VertexNight";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        // A level stage: sand four deep where he stands, earth four deep along its east side, stone under both.
        await _server.CommandAsync($"fill {ax - 8} 200 {az - 8} {ax + 8} 215 {az + 8} minecraft:air");
        await _server.CommandAsync($"fill {ax - 8} 190 {az - 8} {ax + 8} 195 {az + 8} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 8} 196 {az - 8} {ax + 3} 199 {az + 8} minecraft:sand");
        await _server.CommandAsync($"fill {ax + 4} 196 {az - 8} {ax + 8} 199 {az + 8} minecraft:dirt");
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.OnGround && client.FeetBlock.Y == 200 && client.World.GetBlock(ax + 5, 199, az)?.Name == "minecraft:dirt" && client.World.GetBlock(ax, 199, az)?.Name == "minecraft:sand", Timeout, () => $"at {client.Position}");
        var skills = new Skills(client);
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        skills.Trace += m => { trace.Enqueue(m); TestContext.Out.WriteLine("trace: " + m); };
        Assert.That(skills.DigInProblem(), Does.Contain("Sand"), "the setup: he stands on sand");
        await _server.CommandAsync("time set 22400");       // 1,600 ticks of night left: 80 seconds
        await Wait.UntilAsync(() => skills.NightNow, Timeout, () => $"day time {client.World.DayTime}");
        try
        {
            using var c = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var night = skills.ShelterForTheNightAsync(c.Token);
            await Wait.UntilAsync(() => client.FeetBlock.Y <= 197 || night.IsCompleted, TimeSpan.FromSeconds(60), () => $"at {client.Position}; {string.Join(" | ", trace)}");
            Assert.That(night.IsCompleted, Is.False, "he is down, not done: " + (night.IsCompleted ? night.Result.Item2 : ""));
            Assert.That(client.FeetBlock.X, Is.GreaterThanOrEqualTo(ax + 4), "in the earth, not the sand");
            // Nothing hostile anywhere and full health: the old dig-in came straight back up. He stays, because it is night.
            await Task.Delay(TimeSpan.FromSeconds(20));
            Assert.That(skills.NightNow, Is.True, "the setup: still night 20 seconds on");
            Assert.That(night.IsCompleted, Is.False, "still down there while it is night");
            Assert.That(client.FeetBlock.Y, Is.LessThanOrEqualTo(197), "and under the ground");
            var (ok, text) = await night.WaitAsync(TimeSpan.FromMinutes(3));
            TestContext.Out.WriteLine($"{ok}: {text}; at {client.Position}, day time {client.World.DayTime % 24000}");
            Assert.That(ok, Is.True, text);
            Assert.That(text, Does.Contain("until morning").And.Contain("came back up"));
            Assert.That(skills.NightNow, Is.False, "he came up by day");
            Assert.That(client.FeetBlock.Y, Is.GreaterThanOrEqualTo(200), "and is on the surface again");
            Assert.That(client.Health, Is.EqualTo(20), "unhurt");
        }
        finally { await _server.CommandAsync("time set day"); }
    }

    /// <summary>
    /// Live 2026-11-03 18:38 and 18:39: killed twice in two minutes, both lessons saying "I had no weapon. I had no food." After a death nothing is left; he went out into the night with his hands. From
    /// empty pockets on rough ground with three trees and a cow: he punches logs, makes a table, a sword, a pickaxe and an axe, and kills the cow for food.
    /// </summary>
    [Test]
    public async Task WithEmptyPockets_HeMakesAWoodenKit()
    {
        const string name = "VertexKit";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        var skills = new Skills(client);
        Assert.That(skills.HasWeapon, Is.False, "the setup: nothing in his pockets");
        (bool ok, string text) result;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var c = new CancellationTokenSource(TimeSpan.FromMinutes(4))) result = await skills.GetKitAsync(c.Token, hunt: false);
        TestContext.Out.WriteLine($"{result.ok}: {result.text}  ({sw.Elapsed.TotalSeconds:0} s); inventory: {skills.InventorySummary()}");
        Assert.That(client.Inventory.Find(s => s.Name == "minecraft:wooden_sword" || s.Name == "minecraft:stone_sword").Any(), Is.True, "a sword: " + result.text);
        Assert.That(client.Inventory.Find(s => s.Name.EndsWith("_pickaxe")).Any(), Is.True, "a pickaxe: " + result.text);
        Assert.That(client.Inventory.Find(s => s.Name.EndsWith("_axe")).Any(), Is.True, "an axe: " + result.text);
    }

    /// <summary>Hungry with a sword in his hand and a cow close by on the same ground: he kills it and picks up the meat.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task WithACowClose_HeKillsItForFood_WithASword_OrBareHanded(bool withSword)
    {
        var name = withSword ? "VertexHuntSword" : "VertexHuntBare";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        if (withSword) await _server.CommandAsync($"give {name} minecraft:iron_sword 1");
        await _server.CommandAsync($"summon minecraft:cow {ax + 0.5} {200 + StripeHeight(ax, ax)} {az + 5.5} {{NoAI:1b,Tags:[\"vkit\"]}}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:cow") && (!withSword || client.Inventory.Count("iron_sword") >= 1), Timeout, () => client.Inventory.ToString());
        var skills = new Skills(client);
        Assert.That(skills.HasFood, Is.False, "the setup: nothing to eat");
        (bool ok, string text) result;
        using (var c = new CancellationTokenSource(TimeSpan.FromMinutes(2))) result = await skills.GetFoodThenSummaryAsync([], hunt: true, c.Token);
        TestContext.Out.WriteLine($"{result.ok}: {result.text}; inventory: {skills.InventorySummary()}");
        Assert.That(skills.HasFood, Is.True, "meat from the cow: " + result.text);
        await _server.CommandAsync("kill @e[tag=vkit]");
    }

    /// <summary>TJ, 2026-11-03: "unexplained [deaths] may have been skeletons: they are very good at shooting you from a distance." A skeleton with a bow, 12 blocks off, in the open.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task ShotAtByASkeleton_HeKillsItWithASword_OrHidesUndergroundWithout(bool withSword)
    {
        var name = withSword ? "VertexArchSword" : "VertexArchBare";
        var (client, ax, az) = await JoinOnRoughGroundAsync(name);
        await using var _ = client;
        for (var z0 = az - 30; z0 <= az + 30; z0 += 8)
            await _server.CommandAsync($"fill {ax - 30} 150 {z0} {ax + 30} 196 {Math.Min(z0 + 7, az + 30)} minecraft:stone");
        var stand = 200 + StripeHeight(ax, ax);
        // Earth under him: what bare hands get into in two seconds. In stone it is 22, and the test "passed" on his health after he had died and come back (2026-11-16).
        await EarthUnderHimAsync(client, ax, az);
        var deaths = 0;
        client.Died += _ => Interlocked.Increment(ref deaths);
        if (withSword) await _server.CommandAsync($"give {name} minecraft:iron_sword 1");
        await _server.CommandAsync($"summon minecraft:skeleton {ax + 0.5} {stand} {az + 12.5} {{Tags:[\"varcher\"],HandItems:[{{id:\"minecraft:bow\",count:1}},{{}}],active_effects:[{{id:\"minecraft:fire_resistance\",duration:1000000}}]}}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:skeleton") && (!withSword || client.Inventory.Count("iron_sword") >= 1), Timeout, () => client.Inventory.ToString());
        var skills = new Skills(client);
        var tasks = new TaskRunner();
        var jobs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        tasks.Starting += jobs.Enqueue;
        using var reflexes = new Reflexes(client, skills, tasks, () => new PlayStyle { Bravery = 0.5 }) { Enabled = true };
        try
        {
            if (withSword)
            {
                await Wait.UntilAsync(() => !client.Entities.Values.Any(e => e.Name == "minecraft:skeleton") || client.Health <= 0, TimeSpan.FromSeconds(60), () => $"health {client.Health}; jobs {string.Join(" | ", jobs)}");
                Assert.That(client.Health, Is.GreaterThan(0), "he lived: " + string.Join(" | ", jobs));
                Assert.That(jobs.Any(j => j.StartsWith("fighting the skeleton")), Is.True, string.Join(" | ", jobs));
            }
            else
            {
                await Wait.UntilAsync(() => client.Position.Y <= stand - 2 || client.Health <= 0, TimeSpan.FromSeconds(40), () => $"at {client.Position}, health {client.Health}; jobs {string.Join(" | ", jobs)}");
                Assert.That(client.Health, Is.GreaterThan(0), "he lived: " + string.Join(" | ", jobs));
                Assert.That(client.Position.Y, Is.LessThanOrEqualTo(stand - 2), "under the ground: " + string.Join(" | ", jobs));
                Assert.That(jobs.Any(j => j.StartsWith("taking cover")), Is.True, string.Join(" | ", jobs));
            }
            Assert.That(Volatile.Read(ref deaths), Is.Zero, "and he did not die on the way: health after a respawn is health too");
            TestContext.Out.WriteLine($"health {client.Health} at {client.Position}; jobs {string.Join(" | ", jobs)}");
        }
        finally { reflexes.Enabled = false; tasks.Stop(); await _server.CommandAsync("kill @e[tag=varcher]"); }
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
