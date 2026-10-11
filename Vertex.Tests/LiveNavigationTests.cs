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
    public async Task BuildsAHouse_AndWalksOutThroughTheDoor()
    {
        const string name = "VertexHouse";
        var (client, x, z) = await JoinCourseAsync(name, (_, _) => []);
        await using var _ = client;
        foreach (var (item, count) in new[] { ("oak_planks", 64), ("oak_planks", 64), ("glass", 3), ("torch", 1), ("oak_door", 1) })
            await _server.CommandAsync($"give {name} minecraft:{item} {count}");
        await Wait.UntilAsync(() => client.Inventory.Count("oak_planks") == 128 && client.Inventory.Count("oak_door") == 1 && client.Inventory.Count("glass") == 3,
            Timeout, () => client.Inventory.ToString());
        // Stand out on the course: the nearest site is then west of us, so the door must face east, away from the
        // north-west corner the builder last looked at (placing the torch). Facing comes from our rotation.
        await _server.CommandAsync($"tp {name} {x + 30.5} 200 {z + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.X - (x + 30.5)) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        var teleports = client.TeleportCount;

        var skills = new Vertex.Brain.Skills(client);
        var (ok, message) = await skills.BuildHouseAsync(null, null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        TestContext.Out.WriteLine(message);
        Assert.That(ok, message);
        Assert.That(message, Does.Contain("7 by 7 wooden house").And.Contain("east side"));

        var data = client.World.Data;
        List<(int X, int Y, int Z)> Find(string block) =>
            client.World.FindBlocks(st => st > 0 && data.BlockOf(st).Name == block, (x + 25, 201, z), 15, 500);
        Assert.That(Find("minecraft:oak_planks"), Has.Count.EqualTo(Vertex.Brain.Blueprints.HouseBlocks(7, glass: true)));
        Assert.That(Find("minecraft:glass"), Has.Count.EqualTo(3));
        Assert.That(Find("minecraft:torch"), Has.Count.EqualTo(1));
        var door = Find("minecraft:oak_door");
        Assert.That(door, Has.Count.EqualTo(2), "both halves of the door");
        Assert.That(client.Inventory.Count("oak_planks"), Is.EqualTo(128 - Vertex.Brain.Blueprints.HouseBlocks(7, glass: true)));

        // Outside, and the door shut behind us.
        await client.WaitTicksAsync(5);
        var lower = door.OrderBy(d => d.Y).First();
        Assert.That(data.GetProperty(client.World.GetState(lower.X, lower.Y, lower.Z), "open"), Is.EqualTo("false"), "closed the door behind it");
        var side = System.Text.RegularExpressions.Regex.Match(message, @"on the (\w+) side").Groups[1].Value;
        Assert.That(data.GetProperty(client.World.GetState(lower.X, lower.Y, lower.Z), "facing"), Is.EqualTo(side), "the door faces out of its wall");
        var planks = Find("minecraft:oak_planks");
        int minX = planks.Min(p => p.X), maxX = planks.Max(p => p.X), minZ = planks.Min(p => p.Z), maxZ = planks.Max(p => p.Z);
        Assert.That((maxX - minX, maxZ - minZ), Is.EqualTo((6, 6)), "7 by 7");
        var (px, _, pz) = client.Position;
        Assert.That(px < minX || px > maxX + 1 || pz < minZ || pz > maxZ + 1, $"outside the house at {client.Position}");
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");
    }

    [Test]
    public async Task Explores_ReportsWhatItFound_AndComesBack()
    {
        const string name = "VertexExplorer";
        // A strip running east from the course: a bell (a village) 70 blocks out, beyond the 48-block look at the
        // start, and a sniffer (never natural) at 100, beyond Paper's 96-block animal tracking range. Both are
        // only news on the way. The strip is past the server's view distance: force-load it so fill works.
        var (client, x, z) = await JoinCourseAsync(name, (ax, az) =>
        [
            $"forceload add {ax + 40} {az - 1} {ax + 106} {az + 1}",
            $"fill {ax + 40} 200 {az - 1} {ax + 106} 203 {az + 1} minecraft:air",
            $"fill {ax + 40} 199 {az - 1} {ax + 106} 199 {az + 1} minecraft:stone",
            $"setblock {ax + 70} 200 {az + 1} minecraft:bell",
            $"summon minecraft:sniffer {ax + 100.5} 200 {az + 0.5} {{NoAI:1b,Tags:[\"vexplore\"]}}",
        ]);
        await using var _ = client;
        var folder = Path.Combine(Path.GetTempPath(), "vertex-explore-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memory = Vertex.Brain.WorldMemory.Load(folder, "test");
            var skills = new Vertex.Brain.Skills(client);
            var (ok, message) = await skills.ExploreAsync("east", 100, memory, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(3));
            TestContext.Out.WriteLine(message);
            Assert.That(ok, message);
            Assert.That(message, Does.Contain($"a village around {x + 70} {z + 1}").And.Contain("1 sniffer"));
            Assert.That(message, Does.Match(@"explored (9\d|10\d) blocks east"), "went the whole way");
            Assert.That(memory.Places.Any(p => (p.X, p.Y, p.Z) == (x + 70, 200, z + 1) && p.Name.StartsWith("village")), "remembered the village");
            var (px, _, pz) = client.Position;
            Assert.That(Math.Sqrt((px - x - 0.5) * (px - x - 0.5) + (pz - z - 0.5) * (pz - z - 0.5)), Is.LessThan(3), $"came back; at {client.Position}");
        }
        finally
        {
            await _server.CommandAsync("kill @e[tag=vexplore]");
            await _server.CommandAsync($"forceload remove {x + 40} {z - 1} {x + 106} {z + 1}");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
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
