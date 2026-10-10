using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Pathing;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Pathfinding + following against a REAL Paper server: obstacle courses built with /fill, driven only by
/// controls; the server must never correct us and must agree on where we end up.
/// </summary>
[NonParallelizable]
public class LiveNavigationTests
{
    private const int Port = 25930;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("nav", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    /// <summary>A 41x41 course in the sky over spawn, floor at y=199, player standing at its west edge.</summary>
    private async Task<(MinecraftClient Client, int X, int Z)> JoinCourseAsync(string name, Func<int, int, string[]> build)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - 2} 200 {az - 20} {ax + 40} 215 {az + 20} minecraft:air");
        await _server.CommandAsync($"fill {ax - 2} 199 {az - 20} {ax + 40} 199 {az + 20} minecraft:stone");
        await _server.CommandAsync($"clear {name}");
        foreach (var cmd in build(ax, az)) await _server.CommandAsync(cmd);
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 200) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax + 40, az + 20) && client.World.IsLoaded(ax - 2, az - 20)
            && client.World.IsLoaded(ax + 40, az - 20) && client.World.IsLoaded(ax - 2, az + 20), Timeout, () => "course chunks");
        // Let the course's own block updates arrive before planning.
        await client.WaitTicksAsync(10);
        return (client, ax, az);
    }

    [Test]
    public async Task Walks_AroundWalls_UpSteps_AndDownDrops()
    {
        const string name = "VertexNav";
        var (client, x, z) = await JoinCourseAsync(name, (ax, az) =>
        [
            // A wall across the course with one gap at az+8.
            $"fill {ax + 6} 200 {az - 20} {ax + 6} 202 {az + 20} minecraft:stone_bricks",
            $"fill {ax + 6} 200 {az + 8} {ax + 6} 201 {az + 8} minecraft:air",
            // Stairs up: slab, block, block+slab, then a 2-high platform.
            $"fill {ax + 10} 200 {az - 3} {ax + 10} 200 {az + 3} minecraft:stone_slab[type=bottom]",
            $"fill {ax + 11} 200 {az - 3} {ax + 20} 200 {az + 3} minecraft:stone",
            $"fill {ax + 12} 201 {az - 3} {ax + 20} 201 {az + 3} minecraft:stone",
            // The platform ends in a 2-block drop on its east side.
        ]);
        await using var _ = client;
        var teleports = client.TeleportCount;

        // Onto the top of the platform (feet y = 202).
        Assert.That(await client.GoToAsync(Goal.Block(x + 18, 202, z)), $"reached the platform; at {client.Position}");
        Assert.That(client.FeetBlock, Is.EqualTo((x + 18, 202, z)));

        // Off the far end, down the 2-block drop, to the course's east side.
        Assert.That(await client.GoToAsync(Goal.Block(x + 30, 200, z + 5)), $"came down; at {client.Position}");
        Assert.That(client.FeetBlock, Is.EqualTo((x + 30, 200, z + 5)));

        // And all the way back through the gap in the wall.
        Assert.That(await client.GoToAsync(Goal.Near(x + 1.5, 200, z + 0.5, 1)), $"back west; at {client.Position}");
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");
    }

    /// <summary>
    /// Stranded on top of a pillar (like spawning on a tree): every way down is a drop of more than 3, so a
    /// fussy plan finds nothing. GoToAsync must escalate (a fall that hurts but is survivable, or digging down).
    /// </summary>
    [Test]
    public async Task GetsDownFromAPillar()
    {
        const string name = "VertexPillar";
        var (client, x, z) = await JoinCourseAsync(name, (ax, az) => [$"fill {ax + 5} 200 {az} {ax + 5} 206 {az} minecraft:dirt"]);
        await using var _ = client;
        await _server.CommandAsync($"gamemode survival {name}");
        await _server.CommandAsync($"tp {name} {x + 5.5} 207 {z + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 207) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        var teleports = client.TeleportCount;

        Assert.That(await client.GoToAsync(Goal.Block(x + 10, 200, z)), $"got down; at {client.Position}");
        Assert.That(client.FeetBlock, Is.EqualTo((x + 10, 200, z)));
        Assert.That(client.Health, Is.GreaterThanOrEqualTo(4f), "survived");
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");
    }

    [Test]
    public async Task DigsThrough_AndPillarsUp_WhenAllowed()
    {
        const string name = "VertexBuilder";
        var (client, x, z) = await JoinCourseAsync(name, (ax, az) =>
        [
            // Fully enclosed: a dirt box around the start, so the only way out is digging.
            $"fill {ax - 1} 200 {az - 1} {ax + 1} 201 {az + 1} minecraft:dirt hollow",
            $"setblock {ax} 200 {az} minecraft:air", $"setblock {ax} 201 {az} minecraft:air",
            // A ledge 3 blocks up, only reachable by pillaring.
            $"fill {ax + 8} 200 {az - 2} {ax + 12} 202 {az + 2} minecraft:stone",
        ]);
        await using var _ = client;
        await _server.CommandAsync($"give {name} minecraft:dirt 16");
        await _server.CommandAsync($"give {name} minecraft:stone_shovel 1");
        await Wait.UntilAsync(() => client.Inventory.Count("dirt") == 16 && client.Inventory.Count("stone_shovel") == 1, Timeout, () => client.Inventory.ToString());
        var teleports = client.TeleportCount;

        var options = new PathOptions { CanDig = true, CanPlace = true };
        Assert.That(await client.GoToAsync(Goal.Block(x + 5, 200, z), options), $"dug out; at {client.Position}");
        Assert.That(await client.GoToAsync(Goal.Block(x + 10, 203, z), options), $"on the ledge; at {client.Position}");
        Assert.That(client.FeetBlock, Is.EqualTo((x + 10, 203, z)));
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");
    }

    [Test]
    public async Task WalksThroughDoorsAndGates_ClosingThemBehind()
    {
        const string name = "VertexDoors";
        var (client, x, z) = await JoinCourseAsync(name, (ax, az) =>
        [
            // Two brick walls across the course: a closed oak door in the first, a closed fence gate in the second.
            $"fill {ax + 6} 200 {az - 20} {ax + 6} 202 {az + 20} minecraft:stone_bricks",
            $"setblock {ax + 6} 200 {az} minecraft:oak_door[facing=east,half=lower]",
            $"setblock {ax + 6} 201 {az} minecraft:oak_door[facing=east,half=upper]",
            $"fill {ax + 12} 200 {az - 20} {ax + 12} 202 {az + 20} minecraft:stone_bricks",
            $"setblock {ax + 12} 200 {az + 4} minecraft:oak_fence_gate[facing=east]",
            $"setblock {ax + 12} 201 {az + 4} minecraft:air",
        ]);
        await using var _ = client;
        var teleports = client.TeleportCount;
        var data = client.World.Data;
        string? Open(int bx, int by, int bz) => data.GetProperty(client.World.GetState(bx, by, bz), "open");
        Assert.That((Open(x + 6, 200, z), Open(x + 12, 200, z + 4)), Is.EqualTo(("false", "false")));

        // Digging allowed, and still it must not break the bricks or the doors: it opens them.
        var options = new PathOptions { CanDig = true };
        Assert.That(await client.GoToAsync(Goal.Block(x + 18, 200, z + 2), options), $"through both; at {client.Position}");
        Assert.That(client.FeetBlock, Is.EqualTo((x + 18, 200, z + 2)));
        Assert.That(data.BlockOf(client.World.GetState(x + 6, 200, z)).Name, Is.EqualTo("minecraft:oak_door"), "door still there");
        Assert.That(data.BlockOf(client.World.GetState(x + 12, 200, z + 4)).Name, Is.EqualTo("minecraft:oak_fence_gate"), "gate still there");
        await client.WaitTicksAsync(5);
        Assert.That((Open(x + 6, 200, z), Open(x + 12, 200, z + 4)), Is.EqualTo(("false", "false")), "closed both behind us");
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");

        // And back the other way.
        Assert.That(await client.GoToAsync(Goal.Block(x + 1, 200, z), options), $"back; at {client.Position}");
        await client.WaitTicksAsync(5);
        Assert.That((Open(x + 6, 200, z), Open(x + 12, 200, z + 4)), Is.EqualTo(("false", "false")), "closed both behind us");
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");
    }

    [Test]
    public async Task NeverDigsThroughABuild()
    {
        const string name = "VertexPolite";
        var (client, x, z) = await JoinCourseAsync(name, (ax, az) =>
        [
            // Shut in a plank hut (no door): the only way out would be breaking someone's build.
            $"fill {ax - 1} 200 {az - 1} {ax + 1} 202 {az + 1} minecraft:oak_planks hollow",
            $"setblock {ax} 200 {az} minecraft:air", $"setblock {ax} 201 {az} minecraft:air",
        ]);
        await using var _ = client;
        await _server.CommandAsync($"give {name} minecraft:iron_axe 1");
        await Wait.UntilAsync(() => client.Inventory.Count("iron_axe") == 1, Timeout, () => client.Inventory.ToString());

        var options = new PathOptions { CanDig = true, MaxNodes = 5_000 };
        Assert.That(await client.GoToAsync(Goal.Block(x + 5, 200, z), options), Is.False, $"stayed in; at {client.Position}");
        var planks = client.World.FindBlocks(st => st > 0 && client.World.Data.BlockOf(st).Name == "minecraft:oak_planks", client.Position, 3, 64);
        // A 3x3x3 hollow shell is 26 blocks; the floor block under our feet was cleared: 25 planks.
        Assert.That(planks, Has.Count.EqualTo(25), "every plank of the hut is still there");
    }
}
