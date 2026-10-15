using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Building from a script against a REAL Paper server: Vertex checks the plan and its materials, finds a flat spot, places the blocks with real clicks, and the result is compared with the
/// plan block for block (what the server sent us about the world, plus the server's own answer for a few cells).
/// </summary>
[NonParallelizable]
public class LiveBuildTests
{
    private const int Port = 25954;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("building", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "easy" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    // A cabin 5 wide, 7 deep: walls two high, a doorway and two windows, a pitched roof, a door and a torch.
    private const string Cabin =
        "walls 0 0 0 4 1 6 cobblestone\n" +
        "clear 2 0 0 2 1 0\n" +
        "block 0 1 3 glass\nblock 4 1 3 glass\n" +
        "roof gable 0 0 4 6 2 planks\n" +
        "door 2 0 0 south\n" +
        "torch 1 0 1\n";

    /// <summary>Joins and stands the player at the middle of a big flat stone arena (radius 16) at y 200.</summary>
    private async Task<(MinecraftClient Client, int X, int Z)> JoinOnBigArenaAsync(string name, int r = 16)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax + r} 215 {az + r} minecraft:air");
        await _server.CommandAsync($"fill {ax - r} 199 {az - r} {ax + r} 199 {az + r} minecraft:stone");
        await _server.CommandAsync($"clear {name}");
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        await Task.Delay(1500);
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 200) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax - r, az - r) && client.World.IsLoaded(ax + r, az + r)
            && client.World.IsLoaded(ax - r, az + r) && client.World.IsLoaded(ax + r, az - r), Timeout, () => "arena chunks");
        await client.WaitTicksAsync(10);
        return (client, ax, az);
    }

    private async Task GiveAsync(MinecraftClient client, string name, string item, int count)
    {
        var before = client.Inventory.Count(item.Replace("minecraft:", ""));
        await _server.CommandAsync($"give {name} {item} {count}");
        await Wait.UntilAsync(() => client.Inventory.Count(item.Replace("minecraft:", "")) == before + count, Timeout, () => client.Inventory.ToString());
    }

    private async Task GiveCabinMaterialsAsync(MinecraftClient client, string name)
    {
        await GiveAsync(client, name, "minecraft:cobblestone", 64);
        await GiveAsync(client, name, "minecraft:oak_planks", 64);
        await GiveAsync(client, name, "minecraft:glass", 4);
        await GiveAsync(client, name, "minecraft:oak_door", 1);
        await GiveAsync(client, name, "minecraft:torch", 2);
    }

    private static string BlockAt(MinecraftClient client, int x, int y, int z) => client.World.Data.BlockOf(client.World.GetState(x, y, z)).Name;

    /// <summary>The corner (lowest x and z, ground level) of the nearest cobblestone ring: where the build ended up.</summary>
    private static (int X, int Y, int Z) FindCorner(MinecraftClient client, int ax, int az)
    {
        for (var x = ax - 16; x <= ax + 16; x++)
        for (var z = az - 16; z <= az + 16; z++)
            if (BlockAt(client, x, 200, z) == "minecraft:cobblestone" && BlockAt(client, x + 4, 200, z) == "minecraft:cobblestone" && BlockAt(client, x, 200, z + 6) == "minecraft:cobblestone")
                return (x, 200, z);
        throw new AssertionException("no cobblestone corner found: nothing was built");
    }

    [Test]
    public async Task ACabinFromAScript_IsBuiltBlockForBlock()
    {
        const string name = "VertexCabin";
        var (client, ax, az) = await JoinOnBigArenaAsync(name);
        await using var _ = client;
        await GiveCabinMaterialsAsync(client, name);
        var skills = new Skills(client);

        var (ok, text) = await skills.BuildScriptAsync(Cabin, null, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);

        var script = BuildScript.Parse(Cabin, client.World.Data);
        var (cx, cy, cz) = FindCorner(client, ax, az);
        var wrong = new List<string>();
        foreach (var c in script.Cells)
        {
            var actual = BlockAt(client, cx + c.X, cy + c.Y, cz + c.Z);
            var ok1 = c.Item switch
            {
                "planks" => actual.EndsWith("_planks"),
                "door" => actual.EndsWith("_door"),
                "minecraft:torch" => actual is "minecraft:torch" or "minecraft:wall_torch",       // clicked on the side of a wall, the server makes it a wall torch
                _ => actual == c.Item,
            };
            if (!ok1) wrong.Add($"({c.X},{c.Y},{c.Z}) wanted {c.Item}, found {actual}");
        }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {script.Cells.Count} cells are not as planned: " + string.Join("; ", wrong.Take(8)));
        Assert.That(BlockAt(client, cx + 2, cy, cz), Does.EndWith("_door"), "the doorway has its door");
        Assert.That(BlockAt(client, cx + 2, cy + 1, cz), Does.EndWith("_door"), "both halves of it");
        Assert.That(BlockAt(client, cx + 1, cy, cz + 1), Does.EndWith("torch"));

        // The server's own answer for a few cells.
        foreach (var (rx, ry, rz, expect) in new[] { (0, 0, 0, "cobblestone"), (4, 1, 3, "glass"), (2, 4, 3, "planks") })
        {
            var line = _server.WaitForLineAsync(l => l.Contains("Test passed") || l.Contains("Test failed"), Timeout);
            await _server.CommandAsync($"execute if block {cx + rx} {cy + ry} {cz + rz} minecraft:{(expect == "planks" ? "oak_planks" : expect)}");
            Assert.That(await line, Does.Contain("Test passed"), $"the server's block at ({rx},{ry},{rz}) should be {expect}");
        }
    }

    [Test]
    public async Task AnInterruptedBuildIsFinishedByKeepBuilding_WithoutRedoingWhatStands()
    {
        const string name = "VertexResume";
        var (client, ax, az) = await JoinOnBigArenaAsync(name);
        await using var _ = client;
        await GiveCabinMaterialsAsync(client, name);
        var skills = new Skills(client);
        var (ok, text) = await skills.BuildScriptAsync(Cabin, null, CancellationToken.None);
        Assert.That(ok, Is.True, text);
        var (cx, cy, cz) = FindCorner(client, ax, az);

        // Someone knocks out a stretch of wall and the ridge; Vertex still carries spare blocks.
        await _server.CommandAsync($"fill {cx} {cy} {cz + 4} {cx} {cy + 1} {cz + 6} minecraft:air");
        await _server.CommandAsync($"fill {cx + 2} {cy + 4} {cz} {cx + 2} {cy + 4} {cz + 6} minecraft:air");
        await Wait.UntilAsync(() => BlockAt(client, cx, cy, cz + 5) == "minecraft:air" && BlockAt(client, cx + 2, cy + 4, cz + 3) == "minecraft:air", Timeout, () => "the knocked-out blocks");
        await GiveAsync(client, name, "minecraft:cobblestone", 8);
        await GiveAsync(client, name, "minecraft:oak_planks", 8);

        var (resumed, resumeText) = await skills.ResumeBuildAsync(CancellationToken.None);
        TestContext.Out.WriteLine(resumeText);
        Assert.That(resumed, Is.True, resumeText);
        Assert.That(resumeText, Does.Contain("6 blocks placed").Or.Contain("13 blocks placed"), "only the missing 6 wall + 7 ridge blocks: " + resumeText);
        Assert.That(BlockAt(client, cx, cy, cz + 5), Is.EqualTo("minecraft:cobblestone"));
        Assert.That(BlockAt(client, cx + 2, cy + 4, cz + 3), Does.EndWith("_planks"));
    }

    [Test]
    public async Task NotEnoughMaterials_SaysExactlyWhatIsMissing_AndBuildsNothing()
    {
        const string name = "VertexShort";
        var (client, ax, az) = await JoinOnBigArenaAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:cobblestone", 20);
        var skills = new Skills(client);

        var (ok, text) = await skills.BuildScriptAsync(Cabin, null, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.False);
        Assert.That(text, Does.Contain("need 36 cobblestone and have 20").And.Contain("planks").And.Contain("glass").And.Contain("door"));
        Assert.That(client.Inventory.Count("cobblestone"), Is.EqualTo(20), "nothing was spent");
        Assert.That(() => FindCorner(client, ax, az), Throws.TypeOf<AssertionException>(), "nothing was built");
    }

    [Test]
    public async Task ABadScriptIsRefusedWithAnExplanation()
    {
        const string name = "VertexBadPlan";
        var (client, _, _) = await JoinOnBigArenaAsync(name);
        await using var _ = client;
        var skills = new Skills(client);
        var (ok, text) = await skills.BuildScriptAsync("fill 0 0 0 4 2 4 unobtainium", null, CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(text, Does.Contain("problem").And.Contain("unobtainium"));
        var (resumed, resumeText) = await skills.ResumeBuildAsync(CancellationToken.None);
        Assert.That(resumed, Is.False);
        Assert.That(resumeText, Does.Contain("haven't started a build"));
    }

    // ---- named shapes

    private static BlockFace SideFacing(double fromX, double fromZ, double toX, double toZ)
    {
        double fx = toX - fromX, fz = toZ - fromZ;
        return Math.Abs(fx) > Math.Abs(fz) ? (fx > 0 ? BlockFace.East : BlockFace.West) : (fz > 0 ? BlockFace.South : BlockFace.North);
    }

    [Test]
    public async Task AHouse_IsBuiltWithTheDoorFacingThePlayer_AndOnlyWhatItCarries()
    {
        const string name = "VertexHouse";
        var (client, ax, az) = await JoinOnBigArenaAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:cobblestone", 100);
        await GiveAsync(client, name, "minecraft:glass", 3);
        await GiveAsync(client, name, "minecraft:oak_door", 1);
        await GiveAsync(client, name, "minecraft:torch", 1);
        var skills = new Skills(client);

        var (ok, text) = await skills.BuildShapeAsync("cabin", 0, 0, 0, "cobblestone", null, null, null, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("cobblestone house").And.Contain("side"));

        // The 5 x 5 ring: the corner where cobblestone stands 4 away along both axes.
        (int X, int Z)? corner = null;
        for (var x = ax - 16; x <= ax + 16 && corner == null; x++)
        for (var z = az - 16; z <= az + 16 && corner == null; z++)
            if (BlockAt(client, x, 200, z) == "minecraft:cobblestone" && BlockAt(client, x + 4, 200, z) == "minecraft:cobblestone" && BlockAt(client, x, 200, z + 4) == "minecraft:cobblestone")
                corner = (x, z);
        Assert.That(corner, Is.Not.Null, "a 5 x 5 house stands somewhere on the arena");
        var (cx, cz) = corner!.Value;

        // The door is in the middle of the wall that faces the player.
        var side = SideFacing(cx + 2.5, cz + 2.5, ax + 0.5, az + 0.5);
        var (doorX, doorZ) = side switch { BlockFace.North => (cx + 2, cz), BlockFace.South => (cx + 2, cz + 4), BlockFace.West => (cx, cz + 2), _ => (cx + 4, cz + 2) };
        Assert.That(BlockAt(client, doorX, 200, doorZ), Does.EndWith("_door"), $"the door should face the player ({side})");
        // A window (glass) in the middle of each of the other three walls, at the second layer.
        var windows = new[] { (cx + 2, cz), (cx + 2, cz + 4), (cx, cz + 2), (cx + 4, cz + 2) }.Where(p => p != (doorX, doorZ)).ToList();
        Assert.That(windows.Select(p => BlockAt(client, p.Item1, 201, p.Item2)), Is.All.EqualTo("minecraft:glass"));
        // Walls two... three high, a flat roof (walls are 3 high: a gable would reach too high), a torch inside.
        Assert.That(BlockAt(client, cx, 202, cz), Is.EqualTo("minecraft:cobblestone"));
        Assert.That(BlockAt(client, cx + 2, 203, cz + 2), Is.EqualTo("minecraft:cobblestone"), "the roof is closed over the middle");
        Assert.That(new[] { BlockAt(client, cx + 1, 200, cz + 1) }, Has.Some.EndsWith("torch"));
        Assert.That(client.Inventory.Count("cobblestone"), Is.EqualTo(100 - 68), "the walls (43) and the flat roof (25) were all that was used");
    }

    [Test]
    public async Task ACastle_IsBuiltBlockForBlock_AndTheGateFacesThePlayer()
    {
        const string name = "VertexCastle";
        var (client, ax, az) = await JoinOnBigArenaAsync(name, r: 30);
        await using var _ = client;
        // What the shape needs, worked out from the same script the builder will use.
        var probe = BuildScript.Parse(BuildShapes.Script("castle", 13, 13, 3, "minecraft:cobblestone", "minecraft:cobblestone", BlockFace.South, false, false, false), client.World.Data);
        await GiveAsync(client, name, "minecraft:cobblestone", probe.Needs["minecraft:cobblestone"] + 5);
        var skills = new Skills(client);

        var (ok, text) = await skills.BuildShapeAsync("castle", 0, 0, 0, "cobblestone", null, null, null, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);

        // Find it: the corner tower's 3 x 3 ring, 12 blocks from the opposite corner's.
        (int X, int Z)? corner = null;
        for (var x = ax - 30; x <= ax + 30 && corner == null; x++)
        for (var z = az - 30; z <= az + 30 && corner == null; z++)
            if (BlockAt(client, x, 200, z) == "minecraft:cobblestone" && BlockAt(client, x + 12, 200, z) == "minecraft:cobblestone" && BlockAt(client, x, 200, z + 12) == "minecraft:cobblestone"
                && BlockAt(client, x + 12, 200, z + 12) == "minecraft:cobblestone")
                corner = (x, z);
        Assert.That(corner, Is.Not.Null, "a castle stands somewhere on the arena");
        var (cx, cz) = corner!.Value;
        var side = SideFacing(cx + 6.5, cz + 6.5, ax + 0.5, az + 0.5);
        var script = BuildScript.Parse(BuildShapes.Script("castle", 13, 13, 3, "minecraft:cobblestone", "minecraft:cobblestone", side, false, false, false), client.World.Data);
        var wrong = script.Cells.Where(c => BlockAt(client, cx + c.X, 200 + c.Y, cz + c.Z) != c.Item).Select(c => $"({c.X},{c.Y},{c.Z})").ToList();
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {script.Cells.Count} blocks are not as planned: " + string.Join(" ", wrong.Take(10)));
        // The gate is open: air at the middle of the wall that faces the player, two high.
        var (gx, gz) = side switch { BlockFace.North => (cx + 6, cz), BlockFace.South => (cx + 6, cz + 12), BlockFace.West => (cx, cz + 6), _ => (cx + 12, cz + 6) };
        Assert.That((BlockAt(client, gx, 200, gz), BlockAt(client, gx, 201, gz)), Is.EqualTo(("minecraft:air", "minecraft:air")));
        Assert.That(client.Inventory.Count("cobblestone"), Is.EqualTo(5), "exactly what the plan needs was used");
    }

    [Test]
    public async Task APlatformAndAPyramid_AreSolid_AndAnUnknownShapeIsRefusedKindly()
    {
        const string name = "VertexShapes";
        var (client, ax, az) = await JoinOnBigArenaAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:cobblestone", 120);
        var skills = new Skills(client);

        var (ok, text) = await skills.BuildShapeAsync("pyramid", 7, 0, 0, "cobblestone", null, null, null, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("cobblestone"), Is.EqualTo(120 - (49 + 25 + 9 + 1)));
        var top = 0;
        for (var x = ax - 16; x <= ax + 16; x++)
        for (var z = az - 16; z <= az + 16; z++)
            if (BlockAt(client, x, 203, z) == "minecraft:cobblestone") top++;
        Assert.That(top, Is.EqualTo(1), "one block at the top of a 7-wide pyramid, 4 layers up");

        var (refused, why) = await skills.BuildShapeAsync("spaceship", 0, 0, 0, null, null, null, null, CancellationToken.None);
        Assert.That(refused, Is.False);
        Assert.That(why, Does.Contain("don't know how to build a spaceship").And.Contain("castle"));
    }
}
