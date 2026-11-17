using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// From nothing to anything, on a real server: he reads the recipes and works out the chain himself. No trees or ore are handed to him: they stand or lie where a player would have to go and get them, and he starts with empty pockets.
/// </summary>
[NonParallelizable]
public class LiveObtainTests
{
    private const int Port = 25977;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;
    private int _stage;

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("obtain", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("gamerule randomTickSpeed 0");
        await _server.CommandAsync("time set day");
        await _server.CommandAsync("gamerule doDaylightCycle false");
    }

    [OneTimeTearDown]
    public async Task Stop() { if (_server != null) await _server.DisposeAsync(); }

    /// <summary>A stage of stone (his stone tools come from it), a tree, and a little ore, with nothing else in the world within reach.</summary>
    private async Task<(MinecraftClient Client, Skills Skills, int X, int Z)> StageAsync(string name, params Func<int, int, string>[] extra)
    {
        var client = new MinecraftClient(GameProfile.Offline(name));
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        await _server.CommandAsync($"effect give {name} minecraft:resistance 600 4 true");
        var ax = 2000 + 300 * _stage++;
        const int az = 0;
        await _server.CommandAsync($"forceload add {ax - 40} {az - 40} {ax + 40} {az + 40}");
        await Task.Delay(1500);
        await _server.CommandAsync($"fill {ax - 28} 190 {az - 28} {ax + 28} 199 {az + 28} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 28} 200 {az - 28} {ax + 28} 215 {az + 28} minecraft:air");
        // A tree 14 blocks off: six logs tall with a crown.
        await _server.CommandAsync($"fill {ax + 14} 200 {az} {ax + 14} 205 {az} minecraft:oak_log");
        await _server.CommandAsync($"fill {ax + 12} 204 {az - 2} {ax + 16} 206 {az + 2} minecraft:oak_leaves keep");
        // A second tree the other way: enough wood for tools, table, furnace fuel.
        await _server.CommandAsync($"fill {ax - 14} 200 {az + 3} {ax - 14} 205 {az + 3} minecraft:oak_log");
        foreach (var e in extra) await _server.CommandAsync(e(ax, az));
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax + 20, az + 20) && client.World.IsLoaded(ax - 20, az - 20) && client.OnGround && client.FeetBlock.Y == 200, Timeout, () => $"on the stage, at {client.FeetBlock}");
        await client.WaitTicksAsync(20);
        return (client, new Skills(client), ax, az);
    }

    [Test]
    public async Task ABedStandingNearby_OnlyCountsOnceItIsHis()
    {
        var (client, skills, ax, az) = await StageAsync("ObtainNear", (x, z) => $"setblock {x + 3} 200 {z} minecraft:red_bed[facing=east,part=foot]", (x, z) => $"setblock {x + 4} 200 {z} minecraft:red_bed[facing=east,part=head]");
        await using var _c = client;
        var bedStep = skills.Milestones().First(m => m.Item == "bed");
        Assert.That(bedStep.Done(), Is.False, "someone's bed standing near him does not move where he comes back to");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var (ok, text) = await skills.AdvanceAsync(bedStep, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(bedStep.Done(), Is.True);
        await _server.CommandAsync("effect clear ObtainNear");
        await _server.CommandAsync("damage ObtainNear 100 minecraft:generic");
        await Wait.UntilAsync(() => client.Health >= 20f && client.IsSpawned && Math.Abs(client.FeetBlock.X - ax - 3) <= 6 && Math.Abs(client.FeetBlock.Z - az) <= 6, TimeSpan.FromSeconds(30), () => $"coming back at the bed; he is at {client.FeetBlock}");
    }

    [Test]
    public async Task HungryAtSea_FishAreFood()
    {
        var (client, skills, ax, az) = await StageAsync("ObtainFish",
            (x, z) => $"fill {x - 12} 197 {z + 6} {x + 12} 199 {z + 20} minecraft:water",
            (x, z) => $"summon minecraft:cod {x - 3} 198 {z + 10} {{NoAI:1b,PersistenceRequired:1b}}",
            (x, z) => $"summon minecraft:cod {x} 198 {z + 12} {{NoAI:1b,PersistenceRequired:1b}}",
            (x, z) => $"summon minecraft:salmon {x + 3} 198 {z + 10} {{NoAI:1b,PersistenceRequired:1b}}",
            (x, z) => $"summon minecraft:salmon {x + 6} 198 {z + 12} {{NoAI:1b,PersistenceRequired:1b}}");
        await using var _c = client;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var food = skills.Milestones().First(m => m.Item == "food");
        var (ok, text) = await skills.AdvanceAsync(food, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(skills.HasFood, Is.True);
    }

    [Test]
    public async Task LookingForAnimals_HeGoesToTheLand_NotOutOverTheWater()
    {
        // He stands on a small island with water all round, and the only other land lies 52 blocks east. No sheep anywhere: this test is about where he looks. (A server shows animals out to about 96 blocks, so one on that land would be seen from here and he would never have to search.)
        var (client, skills, ax, az) = await StageAsync("ObtainLand",
            (x, z) => $"forceload add {x - 40} {z - 40} {x + 80} {z + 40}",
            (x, z) => $"fill {x - 28} 197 {z - 28} {x - 3} 199 {z + 28} minecraft:water",
            (x, z) => $"fill {x + 3} 197 {z - 28} {x + 28} 199 {z + 28} minecraft:water",
            (x, z) => $"fill {x - 2} 197 {z - 28} {x + 2} 199 {z - 3} minecraft:water",
            (x, z) => $"fill {x - 2} 197 {z + 3} {x + 2} 199 {z + 28} minecraft:water",
            (x, z) => $"fill {x + 29} 197 {z - 28} {x + 50} 199 {z + 28} minecraft:water",
            (x, z) => $"fill {x + 51} 197 {z - 6} {x + 60} 199 {z + 6} minecraft:stone",
            (x, z) => $"fill {x + 12} 200 {z - 2} {x + 16} 206 {z + 2} minecraft:air",
            (x, z) => $"fill {x - 14} 200 {z + 3} {x - 14} 205 {z + 3} minecraft:air");
        await using var _c = client;
        var looked = new List<string>();
        skills.Trace += m => { lock (looked) looked.Add(m); TestContext.Out.WriteLine(m); };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var (ok, text) = await skills.ObtainAsync("mutton", 1, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.False, "no sheep in this world");
        string first;
        lock (looked) first = looked.First(l => l.StartsWith("looking on the land"));
        var x0 = int.Parse(first.Split(' ')[5]);
        Assert.That(x0, Is.GreaterThanOrEqualTo(ax + 45), $"the first place he went to look was the land to the east, not a compass leg over the water: {first}");
    }

    [Test]
    public async Task ABed_FromWhateverColourTheSheepAre_AndItBecomesWhereHeComesBack()
    {
        // Sheep of two colours, as a real flock is: three gray, one white. Only the gray make a bed.
        var (client, skills, ax, az) = await StageAsync("ObtainBed",
            (x, z) => $"summon minecraft:sheep {x + 5} 200 {z + 5} {{Color:7b,NoAI:1b}}",
            (x, z) => $"summon minecraft:sheep {x + 5} 200 {z - 5} {{Color:0b,NoAI:1b}}",
            (x, z) => $"summon minecraft:sheep {x - 5} 200 {z + 5} {{Color:7b,NoAI:1b}}",
            (x, z) => $"summon minecraft:sheep {x - 5} 200 {z - 5} {{Color:7b,NoAI:1b}}");
        await using var _c = client;
        skills.Trace += m => TestContext.Out.WriteLine(m);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var bedStep = skills.Milestones().First(m => m.Item == "bed");
        var (ok, text) = await skills.AdvanceAsync(bedStep, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(client.FindBeds(8, 1), Is.Not.Empty, "it stands beside him");
        Assert.That(skills.HomeBed, Is.Not.Null);
        Assert.That(bedStep.Done(), Is.True);
        Assert.That(skills.Home, Is.EqualTo((skills.HomeBed!.Value.X, skills.HomeBed.Value.Z)), "his bed is where he goes back to");
        // The proof that matters: he comes back beside it, not at the world's spawn.
        var (bx, by, bz) = skills.HomeBed!.Value;
        await _server.CommandAsync("effect clear ObtainBed");
        await _server.CommandAsync("damage ObtainBed 100 minecraft:generic");
        await Wait.UntilAsync(() => client.Health >= 20f && client.IsSpawned && Math.Abs(client.FeetBlock.X - bx) <= 6 && Math.Abs(client.FeetBlock.Z - bz) <= 6, TimeSpan.FromSeconds(30), () => $"coming back at the bed {bx} {by} {bz}; he is at {client.FeetBlock}");
    }

    [Test]
    public async Task WoodOnlyInCrownsOverHisHead_HeStacksBlocksAndClimbsToIt()
    {
        // What stands round his respawn spot on mc.spawndev.com: the low trunks gone, the rest six blocks up, and sand underfoot. Stage 0's two trees are removed so these are all there is.
        var (client, skills, ax, az) = await StageAsync("ObtainHigh",
            (x, z) => $"fill {x + 12} 200 {z - 2} {x + 16} 206 {z + 2} minecraft:air",
            (x, z) => $"fill {x - 14} 200 {z + 3} {x - 14} 205 {z + 3} minecraft:air",
            (x, z) => $"fill {x + 2} 200 {z + 4} {x + 6} 200 {z + 8} minecraft:sand",
            (x, z) => $"fill {x + 8} 206 {z} {x + 8} 212 {z} minecraft:oak_log");
        await using var _c = client;
        skills.Trace += m => TestContext.Out.WriteLine(m);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var (ok, text) = await skills.ObtainAsync("wooden_pickaxe", 1, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("wooden_pickaxe"), Is.EqualTo(1));
        // The only wood there was is in that column: what he has came from up there.
        Assert.That(Enumerable.Range(206, 7).Count(y => client.World.GetBlock(ax + 8, y, az)?.Name == "minecraft:oak_log"), Is.LessThan(7), "he cut logs from the crown");
    }

    [Test]
    public async Task OutOfAPitHeDugHimself_ByDiggingAStair()
    {
        var (client, skills, ax, az) = await StageAsync("ObtainPit");
        await using var _c = client;
        await _server.CommandAsync($"give ObtainPit minecraft:stone_pickaxe");
        await client.WaitTicksAsync(10);
        // Four down, in solid stone, with nothing to build with.
        await _server.CommandAsync($"fill {ax} 196 {az} {ax} 199 {az} minecraft:air");
        await _server.CommandAsync($"tp ObtainPit {ax + 0.5} 196 {az + 0.5}");
        await Wait.UntilAsync(() => client.FeetBlock.Y == 196 && client.OnGround, Timeout, () => $"in the pit, at {client.FeetBlock}");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ok = await client.GoToAsync(SpawnDev.Minecraft.Pathing.Goal.Near(ax + 8.5, 200, az + 0.5, 1.5), new SpawnDev.Minecraft.Pathing.PathOptions { CanDig = true, MaxNodes = 8_000 }, cts.Token);
        Assert.That(ok, Is.True, $"at {client.FeetBlock}");
        Assert.That(client.FeetBlock.Y, Is.EqualTo(200));
    }

    [Test]
    public async Task AStonePickaxe_FromEmptyPockets()
    {
        var (client, skills, _, _) = await StageAsync("ObtainStone");
        await using var _c = client;
        var trace = new List<string>();
        skills.Trace += m => { lock (trace) trace.Add(m); TestContext.Out.WriteLine(m); };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var (ok, text) = await skills.ObtainAsync("stone_pickaxe", 1, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("stone_pickaxe"), Is.GreaterThanOrEqualTo(1));
        Assert.That(client.Inventory.Count("wooden_pickaxe"), Is.GreaterThanOrEqualTo(1), "he worked out he needed one first");
    }

    [Test]
    public async Task AnIronPickaxe_FromEmptyPockets_DiggingAndSmeltingItsWay()
    {
        var (client, skills, _, _) = await StageAsync("ObtainIron",
            (x, z) => $"fill {x - 6} 197 {z + 6} {x - 4} 197 {z + 6} minecraft:iron_ore",
            (x, z) => $"fill {x + 6} 197 {z + 6} {x + 7} 197 {z + 6} minecraft:coal_ore");
        await using var _c = client;
        skills.Trace += m => TestContext.Out.WriteLine(m);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        var (ok, text) = await skills.ObtainAsync("iron_pickaxe", 1, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("iron_pickaxe"), Is.EqualTo(1));
    }

    /// <summary>
    /// Three logs wanted from a trunk of six. Getting at the next one he breaks the log between without aiming at it, and it was not counted: he took a fourth for three. With ore it cost more: two
    /// of three aimed at, all three in his pockets, and he went after a "third" 150 blocks below the stage, six tries of it (4 of that test's 6 minutes, 2026-11-16).
    /// Which log goes that way is up to where the first one's drop lands (he steps into the trunk for it, and the log above is where his head would be): it happened in most runs here and not in all,
    /// so the test holds him to the outcome, three logs for three wanted, and says in its output whether it happened. With the count switched off the run gave four.
    /// </summary>
    [Test]
    public async Task ALogHeBreaksOnTheWayToTheNext_Counts_ThreeWantedIsThreeTaken()
    {
        var (client, skills, ax, az) = await StageAsync("ObtainCount");
        await using var _c = client;
        var trace = new List<string>();
        skills.Trace += m => { lock (trace) trace.Add(m); TestContext.Out.WriteLine(m); };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var (ok, text) = await skills.CollectAsync("wood", 3, cts.Token);
        TestContext.Out.WriteLine($"{ok}: {text}");
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("oak_log"), Is.EqualTo(3), "three, not the fourth he used to take: " + text);
        lock (trace) TestContext.Out.WriteLine(trace.Any(t => t.Contains("I broke it on my way")) ? "one went without his aiming at it, and was counted" : "this time he aimed at all three");
    }

    /// <summary>
    /// Live 2026-11-16, 17:54 to 19:12 and on after a restart: in a flooded pocket about 25 blocks under the sea, with one cell of air to breathe in, "getting out of the water" began with a dive every
    /// time and the need for air ended it: "coming up for air" 208 times in 78 minutes. Here he is in such a pocket (water two deep, one layer of air, rock over it) with a wooden pickaxe, under land
    /// or under a sea eight deep: he cuts his way up and ends in the open, out of the pocket.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task InAFloodedPocketUnderTheGround_HeCutsHisWayUpToTheOpen(bool underTheSea)
    {
        var name = underTheSea ? "ObtainSea" : "ObtainPocket";
        var extras = new List<Func<int, int, string>>
        {
            (x, z) => $"fill {x - 28} 180 {z - 28} {x + 28} 189 {z + 28} minecraft:stone",
            (x, z) => $"fill {x - 1} 186 {z - 1} {x + 1} 188 {z + 1} minecraft:air",
            (x, z) => $"fill {x - 1} 186 {z - 1} {x + 1} 187 {z + 1} minecraft:water",
        };
        if (underTheSea)
        {
            // A sea on the stage: a block of stone 17 across and 8 high, its inside (open at the top) filled with water.
            extras.Add((x, z) => $"fill {x - 8} 200 {z - 8} {x + 8} 207 {z + 8} minecraft:stone");
            extras.Add((x, z) => $"fill {x - 7} 200 {z - 7} {x + 7} 207 {z + 7} minecraft:water");
        }
        var (client, skills, ax, az) = await StageAsync(name, extras.ToArray());
        await using var _c = client;
        await _server.CommandAsync($"give {name} minecraft:wooden_pickaxe 1");
        await _server.CommandAsync($"tp {name} {ax + 0.5} 186 {az + 0.5}");
        await Wait.UntilAsync(() => client.FeetBlock.Y is >= 186 and <= 188 && client.FeetBlock.X == ax && client.Inventory.Count("wooden_pickaxe") == 1 && client.World.GetBlock(ax, 186, az)?.Name == "minecraft:water", Timeout, () => $"in the pocket; at {client.FeetBlock}");
        Assert.That(skills.UnderGround(), Is.True, "the setup: rock over his head");
        var trace = new List<string>();
        skills.Trace += m => { lock (trace) trace.Add(m); TestContext.Out.WriteLine(m); };
        var deaths = 0;
        client.Died += _ => Interlocked.Increment(ref deaths);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var started = DateTime.UtcNow;
        var (ok, text) = await skills.LeaveWaterAsync(cts.Token);
        TestContext.Out.WriteLine($"{ok}: {text} ({(DateTime.UtcNow - started).TotalSeconds:0} s); at {client.FeetBlock}, health {client.Health}, under the ground {skills.UnderGround()}");
        Assert.That(Volatile.Read(ref deaths), Is.Zero, "alive");
        Assert.That(skills.UnderGround(), Is.False, "the open sky over him: " + text);
        // Out of the water is what was asked: under land that can be a dry stair a few blocks short of the top, with the sky over it. Under the sea it is the surface, or the land round it.
        if (underTheSea) Assert.That(client.FeetBlock.Y, Is.GreaterThanOrEqualTo(207), "up at the surface of the sea, or out of it: " + text);
        else
        {
            Assert.That(ok, Is.True, text);
            Assert.That(client.BodySnapshot().InWater, Is.False, "and dry: " + text);
            Assert.That(client.FeetBlock.Y, Is.GreaterThanOrEqualTo(190), "well up from the pocket at 186: " + text);
        }
    }

    /// <summary>
    /// Live 2026-11-16, 17:39: back to life at y 64 with empty pockets, the nearest log was a wreck's or a mine's at y 3; he dived for it and drowned (17 drownings in the 19-hour run, 15 on the way to
    /// a wooden pickaxe). Here the only log there is hangs 50 blocks under the stage: he does not go for it, looks over the land for a tree, and says he found none.
    /// </summary>
    [Test]
    public async Task WoodFarBelowHim_IsNotATree_HeLooksOverTheLandInstead()
    {
        var (client, skills, ax, az) = await StageAsync("ObtainDeep",
            (x, z) => $"fill {x + 12} 200 {z - 2} {x + 16} 206 {z + 2} minecraft:air",
            (x, z) => $"fill {x - 14} 200 {z + 3} {x - 14} 205 {z + 3} minecraft:air",
            (x, z) => $"setblock {x + 3} 150 {z} minecraft:oak_log");
        await using var _c = client;
        await Wait.UntilAsync(() => client.World.GetBlock(ax + 3, 150, az)?.Name == "minecraft:oak_log" && client.World.GetBlock(ax + 14, 202, az)?.Name == "minecraft:air", Timeout, () => $"the stage; at {client.FeetBlock}");
        var trace = new List<string>();
        skills.Trace += m => { lock (trace) trace.Add(m); TestContext.Out.WriteLine(m); };
        var deaths = 0;
        client.Died += _ => Interlocked.Increment(ref deaths);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var (ok, text) = await skills.CollectAsync("wood", 2, cts.Token);
        TestContext.Out.WriteLine($"{ok}: {text}; at {client.FeetBlock}");
        Assert.That(ok, Is.False, text);
        lock (trace) Assert.That(trace.Where(t => t.Contains("going for the one at")), Is.Empty, "he did not set off for the log under the stage");
        Assert.That(text, Does.Contain("haven't found any"), "he looked, and says so");
        lock (trace) Assert.That(trace.Any(t => t.Contains("looking on the land")), Is.True, "over the land: " + string.Join(" | ", trace));
        Assert.That(client.FeetBlock.Y, Is.GreaterThanOrEqualTo(199), "still on the stage");
        Assert.That(Volatile.Read(ref deaths), Is.Zero);
    }

    /// <summary>
    /// Live 2026-11-16: an iron pickaxe made at 16:39 was worn out by 16:44, about 250 blocks of stone later, while he looked for animals, sheep and spiders from 20 blocks under the ground: "looking over
    /// the land" from down there is a tunnel under it. Here he is in a cell 20 blocks under the stage (the stage made 30 thick for it) with an iron pickaxe and no sheep anywhere: he comes up, looks from the
    /// surface, and says he found none.
    /// </summary>
    [Test]
    public async Task LookingForSheepFromUnderTheGround_HeComesUpFirst_AndDoesNotTunnelUnderTheLand()
    {
        var (client, skills, ax, az) = await StageAsync("ObtainUp",
            (x, z) => $"fill {x - 28} 170 {z - 28} {x + 28} 179 {z + 28} minecraft:stone",
            (x, z) => $"fill {x - 28} 180 {z - 28} {x + 28} 189 {z + 28} minecraft:stone",
            (x, z) => $"fill {x} 180 {z} {x} 181 {z} minecraft:air");
        await using var _c = client;
        await _server.CommandAsync("give ObtainUp minecraft:iron_pickaxe 1");
        await _server.CommandAsync($"tp ObtainUp {ax + 0.5} 180 {az + 0.5}");
        await Wait.UntilAsync(() => client.OnGround && client.FeetBlock == (ax, 180, az) && client.Inventory.Count("iron_pickaxe") == 1, Timeout, () => $"at {client.FeetBlock}; {client.Inventory}");
        Assert.That(skills.UnderGround(), Is.True, "the setup: 18 blocks of stone over his head");
        var trace = new List<string>();
        skills.Trace += m => { lock (trace) trace.Add(m); TestContext.Out.WriteLine(m); };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var (ok, text) = await skills.ObtainAsync("bed", 1, cts.Token);
        var stone = client.Inventory.Count("cobblestone");
        TestContext.Out.WriteLine($"{ok}: {text}; at {client.FeetBlock}, {stone} cobblestone");
        Assert.That(ok, Is.False, "there are no sheep: " + text);
        Assert.That(text, Does.Contain("found none"));
        lock (trace) Assert.That(trace.Any(t => t.Contains("going up to the surface")), Is.True, string.Join(" | ", trace));
        Assert.That(client.FeetBlock.Y, Is.GreaterThanOrEqualTo(200), "he looked from the surface");
        Assert.That(stone, Is.LessThanOrEqualTo(90), "a stair up 20 blocks, not three tunnels under the land");
    }

    [Test]
    public async Task WhatCannotBeFound_FailsSayingWhat_NotInTwoSecondsAndNotForever()
    {
        // Diamonds need an iron pickaxe, which needs iron, and there is no iron here: he goes as far as the wood and stone, then says what is missing.
        var (client, skills, _, _) = await StageAsync("ObtainNothing");
        await using var _c = client;
        var trace = new List<string>();
        skills.Trace += m => { lock (trace) trace.Add(m); };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var started = DateTime.UtcNow;
        var (ok, text) = await skills.ObtainAsync("diamond_sword", 1, cts.Token);
        TestContext.Out.WriteLine($"{(DateTime.UtcNow - started).TotalSeconds:0} s: {text}");
        Assert.That(ok, Is.False);
        Assert.That(text, Does.Contain("iron").IgnoreCase, "the thing that stopped him, by name");
        // That he went digging for iron says he had the stone pickaxe it takes. Whether he still holds it at the end is up to the ground under this stage: the iron he can see is in the real world 150 blocks
        // below a stage that floats, and six tries at it through the stage's floor wore the pickaxe out on one stage (132 cobblestone, 2026-11-16) and not on the next (9).
        lock (trace) Assert.That(trace.Any(t => t.StartsWith("digging for") && t.Contains("raw iron")), Is.True, "he got as far as there was any getting: " + string.Join(" | ", trace.TakeLast(6)));
        Assert.That(client.Inventory.Count("stone_pickaxe") >= 1 || client.Inventory.Count("cobblestone") >= 100, Is.True, "with a stone pickaxe, or the stone it broke on: " + text);
    }

    [Test]
    public async Task HeWorksHisWayUpTheSteps_OneAtATime_AndAFailedStepWaits()
    {
        var (client, skills, _, _) = await StageAsync("ObtainSteps");
        await using var _c = client;
        var reached = new List<string>();
        skills.MilestoneReached += m => reached.Add(m.Label);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        // No sheep or animals on this stage: the bed and the food cannot be had, and he must not stand still for them.
        // A failed step comes round again after its wait, so this is not one try each: what it must not do is keep him from the rest.
        for (var i = 0; i < 12 && !reached.Contains("a stone sword") && skills.NextMilestone() is { } step && !cts.IsCancellationRequested; i++)
        {
            var (_, text) = await skills.AdvanceAsync(step, cts.Token);
            TestContext.Out.WriteLine($"{step.Label}: {text}");
        }
        Assert.That(reached, Does.Contain("a wooden pickaxe").And.Contain("a wooden sword").And.Contain("a stone pickaxe").And.Contain("a stone sword"), "past the two he could not get");
        Assert.That(reached, Does.Not.Contain("a bed of my own").And.Not.Contain("some food"));
    }
}
