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
        for (var i = 0; i < 5 && skills.NextMilestone() is { } step && !cts.IsCancellationRequested; i++)
        {
            var (_, text) = await skills.AdvanceAsync(step, cts.Token);
            TestContext.Out.WriteLine($"{step.Label}: {text}");
            if (step.Label == "some food") break;      // no animals on this stage
        }
        Assert.That(reached, Does.Contain("a wooden pickaxe").And.Contain("a stone pickaxe").And.Contain("a stone sword"));
        Assert.That(skills.NextMilestone()?.Label, Is.Not.EqualTo("some food"), "no animal here: it waits while he goes on to what is next");
    }
}
