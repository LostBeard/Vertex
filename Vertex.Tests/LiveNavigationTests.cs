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

    /// <summary>A 41x41 course in the sky over spawn, floor at y=199 (open air under it), player standing at its west edge.</summary>
    private async Task<(MinecraftClient Client, int X, int Z)> JoinCourseAsync(string name, Func<int, int, string[]> build)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - 2} 200 {az - 20} {ax + 40} 215 {az + 20} minecraft:air");
        // Under the floor too: courses dig into it (ditches, holes) and leave blocks there for the next test.
        await _server.CommandAsync($"fill {ax - 2} 190 {az - 20} {ax + 40} 198 {az + 20} minecraft:air");
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

    /// <summary>
    /// A wall 6 high across the whole course with a ladder up its west face: the only way onto it is the ladder,
    /// and the only way back down without a 6-block fall is the ladder again (drops over 3 are not allowed).
    /// </summary>
    [Test]
    public async Task Climbs_UpALadder_AndBackDown()
    {
        const string name = "VertexLadder";
        var (client, x, z) = await JoinCourseAsync(name, (ax, az) =>
        [
            $"fill {ax + 6} 200 {az - 20} {ax + 8} 205 {az + 20} minecraft:stone",
            $"fill {ax + 5} 200 {az} {ax + 5} 205 {az} minecraft:ladder[facing=west]",
        ]);
        await using var _ = client;
        var teleports = client.TeleportCount;

        Assert.That(await client.GoToAsync(Goal.Block(x + 7, 206, z), new PathOptions()), $"up; at {client.Position}");
        Assert.That(client.FeetBlock, Is.EqualTo((x + 7, 206, z)), "on top of the wall");

        Assert.That(await client.GoToAsync(Goal.Block(x + 1, 200, z), new PathOptions()), $"down; at {client.Position}");
        Assert.That(client.FeetBlock, Is.EqualTo((x + 1, 200, z)));
        Assert.That(client.Health, Is.EqualTo(20f), "climbed down, did not fall");
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");

        // And the plan really goes down the ladder: at least until a drop of 3 or less is left (a player would
        // let go there too; more than 3 hurts).
        var finder = new Pathfinder(client.World);
        var (steps, complete) = finder.FindPath((x + 7, 206, z), Goal.Block(x + 1, 200, z), new PathOptions());
        var plan = string.Join(" ", steps.Select(s => $"{s.Kind}({s.X},{s.Y},{s.Z})"));
        Assert.That(complete, plan);
        Assert.That(steps.Count(s => s.Kind == MoveKind.Climb && s.X == x + 5), Is.GreaterThanOrEqualTo(3), plan);
        var y = 206;
        foreach (var s in steps)
        {
            if (s.Kind == MoveKind.Drop) Assert.That(y - s.Y, Is.LessThanOrEqualTo(3), plan);
            y = s.Y;
        }
    }

    /// <summary>
    /// Every placement empties the held stack (five kinds of planks, one each), so every next click comes right
    /// after a stack ran out: the client must know at once that its hand is empty (vanilla predicts it), not a
    /// tick later when the server's slot update arrives, or it clicks with nothing and the block never appears.
    /// </summary>
    [Test]
    public async Task Builds_FromStacksThatRunOut_OneBlockEach()
    {
        const string name = "VertexStacks";
        var (client, x, z) = await JoinCourseAsync(name, (_, _) => []);
        await using var _ = client;
        var kinds = new[] { "oak_planks", "birch_planks", "spruce_planks", "jungle_planks", "acacia_planks" };
        await GiveAsync(client, name, kinds.Select(k => (k, 1)).ToArray());
        var plan = Enumerable.Range(0, kinds.Length)
            .Select(i => new Placement(x + 2, 200 + i, z + 2, s => s.Name.EndsWith("_planks"), "planks")).ToList();

        // The server sends the placed block at once but the held slot's new count only at the end of its tick
        // (broadcastChanges). At 2 ticks a second that gap is up to half a second, so a client that waits for the
        // server to say the stack is gone clicks with it every time; at 20 it only sometimes did.
        (int, int, string?) result;
        await _server.CommandAsync("tick rate 2");
        try { result = await client.BuildAsync(plan).WaitAsync(TimeSpan.FromMinutes(1)); }
        finally { await _server.CommandAsync("tick rate 20"); }
        Assert.That(result, Is.EqualTo((5, 0, (string?)null)));
        Assert.That(Enumerable.Range(0, 5).Select(i => client.World.Data.BlockOf(client.World.GetState(x + 2, 200 + i, z + 2)).Name),
            Has.All.EndsWith("_planks"));
        Assert.That(client.Inventory.Find(s => s.Name.EndsWith("_planks")), Is.Empty);
    }

    private async Task GiveAsync(MinecraftClient client, string name, params (string Item, int Count)[] items)
    {
        foreach (var (item, count) in items)
            for (var left = count; left > 0; left -= 64)
                await _server.CommandAsync($"give {name} minecraft:{item} {Math.Min(64, left)}");
        await Wait.UntilAsync(() => items.GroupBy(i => i.Item).All(g => client.Inventory.Count(g.Key) == g.Sum(i => i.Count)),
            Timeout, () => client.Inventory.ToString());
    }

    private static List<(int X, int Y, int Z)> FindAround(MinecraftClient client, string block, int x, int y, int z, int radius = 20) =>
        client.World.FindBlocks(st => st > 0 && client.World.Data.BlockOf(st).Name == block, (x, y, z), radius, 2000);

    [Test]
    public async Task BuildsAStraightWall_UpAStep_FillingADip_SkippingAHole()
    {
        const string name = "VertexWall";
        var (client, x, z) = await JoinCourseAsync(name, (ax, az) =>
        [
            $"setblock {ax + 4} 200 {az} minecraft:stone",              // a step up in the line
            $"fill {ax + 5} 198 {az} {ax + 5} 199 {az} minecraft:air",  // a dip two deep (stone at 197)
            $"setblock {ax + 5} 197 {az} minecraft:stone",
            $"fill {ax + 6} 192 {az} {ax + 6} 199 {az} minecraft:air",  // a hole deeper than the ground search
        ]);
        await using var _ = client;
        await GiveAsync(client, name, ("cobblestone", 64));
        var teleports = client.TeleportCount;

        var skills = new Vertex.Brain.Skills(client);
        var (ok, message) = await skills.BuildWallAsync(null, "east", 8, 3, around: false, null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        TestContext.Out.WriteLine(message);
        Assert.That(ok, message);
        Assert.That(message, Does.Contain("cobblestone wall 8 blocks long and 3 high going east").And.Contain("left out 1 spot"));

        var expected = new HashSet<(int, int, int)>();
        for (var i = 1; i <= 8; i++)
        {
            var (from, to) = i switch { 4 => (201, 203), 5 => (198, 202), 6 => (0, -1), _ => (200, 202) };
            for (var y = from; y <= to; y++) expected.Add((x + i, y, z));
        }
        var cobble = FindAround(client, "minecraft:cobblestone", x + 4, 200, z).ToHashSet();
        Assert.That(cobble, Is.EquivalentTo(expected), "up the step, the dip filled to the top, the hole left out: "
            + string.Join(" ", cobble.OrderBy(c => c.X).ThenBy(c => c.Y)));
        Assert.That(client.Inventory.Count("cobblestone"), Is.EqualTo(64 - expected.Count));
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");
    }

    [Test]
    public async Task BuildsAPen_AroundItself_WithADoor()
    {
        const string name = "VertexPen";
        var (client, x, z) = await JoinCourseAsync(name, (_, _) => []);
        await using var _ = client;
        await GiveAsync(client, name, ("oak_planks", 64), ("oak_door", 1));
        await _server.CommandAsync($"tp {name} {x + 20.5} 200 {z + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.X - (x + 20.5)) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        var teleports = client.TeleportCount;

        var skills = new Vertex.Brain.Skills(client);
        var (ok, message) = await skills.BuildWallAsync(null, null, 3, 2, around: true, "wood", CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        TestContext.Out.WriteLine(message);
        Assert.That(ok, message);
        Assert.That(message, Does.Contain("wooden wall around here, 7 by 7, with a door on the"));

        var planks = FindAround(client, "minecraft:oak_planks", x + 20, 200, z).ToHashSet();
        var door = FindAround(client, "minecraft:oak_door", x + 20, 200, z);
        Assert.That(door, Has.Count.EqualTo(2), "both halves of the door");
        var gate = (door[0].X, door[0].Z);
        var ring = new HashSet<(int, int, int)>();
        for (var dx = -3; dx <= 3; dx++)
        for (var dz = -3; dz <= 3; dz++)
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) == 3 && (x + 20 + dx, z + dz) != gate)
                for (var y = 200; y < 202; y++) ring.Add((x + 20 + dx, y, z + dz));
        Assert.That(planks, Is.EquivalentTo(ring), "a 7x7 ring two high, the gateway holding the door");
        var side = System.Text.RegularExpressions.Regex.Match(message, @"on the (\w+) side").Groups[1].Value;
        var (gdx, gdz) = Vertex.Brain.Blueprints.Dir(Enum.Parse<BlockFace>(side, ignoreCase: true));
        Assert.That(gate, Is.EqualTo((x + 20 + 3 * gdx, z + 3 * gdz)), "the door is on the side it said");
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");
    }

    [Test]
    public async Task BuildsATower_MakesLadders_AndClimbsDown()
    {
        const string name = "VertexTower";
        var (client, x, z) = await JoinCourseAsync(name, (_, _) => []);
        await using var _ = client;
        // 8 high: 8 rings of 8 + a core of 7 = 71 blocks. Four ladders short: it must make them (sticks, a table).
        await GiveAsync(client, name, ("cobblestone", 128), ("ladder", 4), ("stick", 14), ("crafting_table", 1));
        await _server.CommandAsync($"tp {name} {x + 20.5} 200 {z + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.X - (x + 20.5)) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        var teleports = client.TeleportCount;

        var skills = new Vertex.Brain.Skills(client);
        var (ok, message) = await skills.BuildTowerAsync(null, 8, "cobblestone", CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(3));
        TestContext.Out.WriteLine(message);
        Assert.That(ok, message);
        Assert.That(message, Does.Contain("cobblestone tower 8 blocks high with a ladder").And.Not.Contain("still up on top"));

        var cobble = FindAround(client, "minecraft:cobblestone", x + 20, 204, z);
        Assert.That(cobble, Has.Count.EqualTo(71));
        int minX = cobble.Min(c => c.X), maxX = cobble.Max(c => c.X), minZ = cobble.Min(c => c.Z), maxZ = cobble.Max(c => c.Z);
        Assert.That((maxX - minX, maxZ - minZ, cobble.Min(c => c.Y), cobble.Max(c => c.Y)), Is.EqualTo((2, 2, 200, 207)), "3x3, 8 high");
        var ladders = FindAround(client, "minecraft:ladder", x + 20, 204, z);
        Assert.That(ladders.Select(l => l.Y).OrderBy(v => v), Is.EqualTo(Enumerable.Range(200, 8)), "a ladder all the way up");
        Assert.That(ladders.Select(l => (l.X, l.Z)).Distinct().Count(), Is.EqualTo(1), "in one column");
        // Hanging on the tower: the block behind each ladder is the tower.
        var data = client.World.Data;
        var facing = data.GetProperty(client.World.GetState(ladders[0].X, ladders[0].Y, ladders[0].Z), "facing")!;
        var (fdx, fdz) = Vertex.Brain.Blueprints.Dir(Enum.Parse<BlockFace>(facing, ignoreCase: true));
        Assert.That(cobble, Does.Contain((ladders[0].X - fdx, ladders[0].Y, ladders[0].Z - fdz)), "ladder hangs on the tower wall");
        // Back down on the ground, by the ladder.
        Assert.That(client.FeetBlock.Y, Is.EqualTo(200), $"came down: at {client.Position}");
        Assert.That(client.Health, Is.EqualTo(20f), "climbed down, did not jump");
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");
    }

    [Test]
    public async Task BuildsBridges_AcrossWater_AndADitch()
    {
        const string name = "VertexBridge";
        var (client, x, z) = await JoinCourseAsync(name, (ax, az) =>
        [
            // A river 7 wide (one deep) and further on a ditch 4 wide and 4 deep, across the whole course.
            $"fill {ax + 10} 199 {az - 20} {ax + 16} 199 {az + 20} minecraft:water",
            $"fill {ax + 22} 196 {az - 20} {ax + 25} 199 {az + 20} minecraft:air",
            $"fill {ax + 22} 195 {az - 20} {ax + 25} 195 {az + 20} minecraft:stone",
        ]);
        await using var _ = client;
        await GiveAsync(client, name, ("cobblestone", 20));
        await _server.CommandAsync($"tp {name} {x + 5.5} 200 {z + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.X - (x + 5.5)) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        var teleports = client.TeleportCount;
        var skills = new Vertex.Brain.Skills(client);

        var (ok, message) = await skills.BuildBridgeAsync(null, "east", null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));
        TestContext.Out.WriteLine(message);
        Assert.That(ok, message);
        Assert.That(message, Does.Contain("cobblestone bridge 7 blocks long going east"));
        Assert.That(client.FeetBlock, Is.EqualTo((x + 17, 200, z)), "on the far bank");

        (ok, message) = await skills.BuildBridgeAsync(null, "east", null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));
        TestContext.Out.WriteLine(message);
        Assert.That(ok, message);
        Assert.That(message, Does.Contain("cobblestone bridge 4 blocks long going east"));
        Assert.That(client.FeetBlock, Is.EqualTo((x + 26, 200, z)), "across the ditch");

        var bridge = FindAround(client, "minecraft:cobblestone", x + 17, 199, z).ToHashSet();
        Assert.That(bridge, Is.EquivalentTo(Enumerable.Range(10, 7).Concat(Enumerable.Range(22, 4)).Select(i => (x + i, 199, z))),
            "bridge blocks (x relative): " + string.Join(" ", bridge.OrderBy(b => b.X).Select(b => $"{b.X - x},{b.Y},{b.Z - z}")));
        Assert.That(client.Inventory.Count("cobblestone"), Is.EqualTo(20 - 11));
        Assert.That(client.Health, Is.EqualTo(20f), "never fell");
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");

        // Nothing to bridge: says so instead of building.
        (ok, message) = await skills.BuildBridgeAsync(null, "east", null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));
        Assert.That(ok, Is.False, message);
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
