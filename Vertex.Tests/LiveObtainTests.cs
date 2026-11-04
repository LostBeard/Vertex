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
            (x, z) => $"summon minecraft:cod {x - 3} 198 {z + 10} {{NoAI:1b}}",
            (x, z) => $"summon minecraft:cod {x} 198 {z + 12} {{NoAI:1b}}",
            (x, z) => $"summon minecraft:salmon {x + 3} 198 {z + 10} {{NoAI:1b}}",
            (x, z) => $"summon minecraft:salmon {x + 6} 198 {z + 12} {{NoAI:1b}}");
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

    [Test]
    public async Task WhatCannotBeFound_FailsSayingWhat_NotInTwoSecondsAndNotForever()
    {
        // Diamonds need an iron pickaxe, which needs iron, and there is no iron here: he goes as far as the wood and stone, then says what is missing.
        var (client, skills, _, _) = await StageAsync("ObtainNothing");
        await using var _c = client;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var started = DateTime.UtcNow;
        var (ok, text) = await skills.ObtainAsync("diamond_sword", 1, cts.Token);
        TestContext.Out.WriteLine($"{(DateTime.UtcNow - started).TotalSeconds:0} s: {text}");
        Assert.That(ok, Is.False);
        Assert.That(text, Does.Contain("iron").IgnoreCase, "the thing that stopped him, by name");
        Assert.That(client.Inventory.Count("stone_pickaxe"), Is.GreaterThanOrEqualTo(1), "he got as far as there was any getting");
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
