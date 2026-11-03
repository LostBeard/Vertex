using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Pathing;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>"Build me a house" with empty pockets, against a REAL Paper server: Vertex cuts logs, makes planks of them, and builds, instead of answering "I don't have enough".</summary>
[NonParallelizable]
public class LiveGatherTests
{
    private const int Port = 25972;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("gather", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    private async Task<(MinecraftClient Client, int X, int Z)> JoinAsync(string name, bool trees, bool leftoverTrunk = false)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        const int r = 20;
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        if (leftoverTrunk)
        {
            // What an earlier test in this world leaves behind: a trunk 35 blocks off (outside the 20 cleared below, inside the 48 the log search covers) on floor that joins this arena's.
            await _server.CommandAsync($"fill {ax + r + 1} 199 {az - 2} {ax + 38} 199 {az + 2} minecraft:stone");
            await _server.CommandAsync($"fill {ax + 35} 200 {az} {ax + 35} 203 {az} minecraft:oak_log");
        }
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax + r} 215 {az + r} minecraft:air");
        await _server.CommandAsync($"fill {ax - r} 199 {az - r} {ax + r} 199 {az + r} minecraft:stone");
        if (trees)
            // Five trunks of oak logs, four high, off to one side: 20 logs, 80 planks.
            for (var i = 0; i < 5; i++)
                await _server.CommandAsync($"fill {ax + 12} 200 {az - 6 + i * 3} {ax + 12} 203 {az - 6 + i * 3} minecraft:oak_log");
        else
        {
            // No trees means NO trees within the 48 blocks the log search covers. Every test in this world builds trunks in the sky near where ITS player spawned, and players spawn at
            // different spots, so an earlier test's trunks can lie just outside the 20 blocks cleared above and be found (seen 2026-11-02: "I cut logs but couldn't make planks of them:
            // I need 11 more oak log", twice, in full runs). The trunks are all at y 200 to 203: clear that band 60 blocks around, in two strips (a fill is limited to 32768 blocks).
            await _server.CommandAsync($"fill {ax - 60} 200 {az - 60} {ax + 60} 203 {az} minecraft:air");
            await _server.CommandAsync($"fill {ax - 60} 200 {az + 1} {ax + 60} 203 {az + 60} minecraft:air");
        }
        await _server.CommandAsync($"clear {name}");
        await Task.Delay(1500);
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 200) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax - r, az - r) && client.World.IsLoaded(ax + r, az + r)
            && client.World.IsLoaded(ax - r, az + r) && client.World.IsLoaded(ax + r, az - r), Timeout, () => "arena chunks");
        if (trees) await Wait.UntilAsync(() => client.World.GetBlock(ax + 12, 202, az - 6)?.Name == "minecraft:oak_log", Timeout, () => "the trees");
        await client.WaitTicksAsync(10);
        return (client, ax, az);
    }

    [Test]
    public async Task AHouseWithEmptyPockets_IsBuiltFromLogsItCuts()
    {
        const string name = "VertexGather";
        var (client, ax, az) = await JoinAsync(name, trees: true);
        await using var _ = client;
        var skills = new Skills(client);
        Assert.That(client.Inventory.Count("oak_planks"), Is.EqualTo(0));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var (ok, text) = await skills.BuildShapeAsync("house", 0, 0, 0, "wood", null, null, null, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("cut").And.Contain("log"), "it says it cut logs first");

        // A house of planks stands near where it started (the trees were 12 blocks off).
        var planks = 0;
        for (var x = ax - 8; x <= ax + 10; x++)
        for (var z = az - 8; z <= az + 8; z++)
            for (var y = 200; y <= 204; y++)
                if (client.World.GetBlock(x, y, z)?.Name.EndsWith("_planks") == true) planks++;
        TestContext.Out.WriteLine($"{planks} plank blocks standing");
        Assert.That(planks, Is.GreaterThanOrEqualTo(30), "a house worth of planks is standing");
    }

    /// <summary>
    /// Live on AubsCraft 2026-11-01 (TJ, watching): on his own he chose "build a house" while carrying 5 dirt, took dirt as the wall material because it was what he carried most of, needed 71
    /// and stood still. A few of something he cannot fetch must not beat a material he can: no material named, 5 dirt in the pockets, logs nearby, he builds from wood.
    /// </summary>
    [Test]
    public async Task AHouseWithAHandfulOfDirt_NoMaterialNamed_IsBuiltFromLogsItCuts()
    {
        const string name = "VertexDirt";
        var (client, ax, az) = await JoinAsync(name, trees: true);
        await using var _ = client;
        await _server.CommandAsync($"give {name} minecraft:dirt 5");
        await Wait.UntilAsync(() => client.Inventory.Count("dirt") == 5, Timeout, () => "the dirt, have " + client.Inventory.Count("dirt"));
        var skills = new Skills(client);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var (ok, text) = await skills.BuildShapeAsync("house", 0, 0, 0, null, null, null, null, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("cut").And.Contain("log"), "it says it cut logs first");
        var planks = 0;
        for (var x = ax - 8; x <= ax + 10; x++)
        for (var z = az - 8; z <= az + 8; z++)
            for (var y = 200; y <= 204; y++)
                if (client.World.GetBlock(x, y, z)?.Name.EndsWith("_planks") == true) planks++;
        Assert.That(planks, Is.GreaterThanOrEqualTo(30), "a house worth of planks is standing");
    }

    [Test]
    public async Task APenForAnimals_IsAFenceRingWithAGate_MadeFromLogsItCuts()
    {
        const string name = "VertexPenner";
        var (client, ax, az) = await JoinAsync(name, trees: true);
        await using var _ = client;
        var skills = new Skills(client);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var (ok, text) = await skills.BuildShapeAsync("pen", 0, 0, 0, null, null, null, null, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        int fences = 0, gates = 0, gx = 0, gz = 0;
        var xs = new List<int>(); var zs = new List<int>();
        for (var x = ax - 10; x <= ax + 10; x++)
        for (var z = az - 10; z <= az + 10; z++)
        {
            var b = client.World.GetBlock(x, 200, z)?.Name ?? "";
            if (b.EndsWith("_fence_gate")) { gates++; gx = x; gz = z; xs.Add(x); zs.Add(z); }
            else if (b.EndsWith("_fence")) { fences++; xs.Add(x); zs.Add(z); }
        }
        // The gate's plate must run ALONG the side it is in (facing across it), or animals walk round it.
        var facing = client.World.Data.GetProperty(client.World.GetState(gx, 200, gz), "facing");
        var onNorthOrSouthSide = gz == zs.Min() || gz == zs.Max();
        TestContext.Out.WriteLine($"gate at {gx},{gz} facing {facing}; ring x {xs.Min()}..{xs.Max()}, z {zs.Min()}..{zs.Max()}");
        Assert.That(onNorthOrSouthSide ? facing is "north" or "south" : facing is "east" or "west", Is.True, $"the gate faces {facing}");
        TestContext.Out.WriteLine($"{fences} fences, {gates} gates standing");
        Assert.That(fences, Is.EqualTo(23), "a 7 by 7 ring is 24 blocks, one of them the gate");
        Assert.That(gates, Is.EqualTo(1));
    }

    [Test]
    public async Task WithNoTreesToCut_ItSaysSo()
    {
        const string name = "VertexNoTrees";
        // A trunk an earlier test left (the server is shared) 35 blocks away: JoinAsync takes it down with the rest of the band. (This line used to try it with one fill of 81 x 7 x 81 = 45,927
        // blocks, over the 32,768 a fill may touch: the server refused it and the trees stayed, so the test failed whenever a leftover trunk was in the 48 blocks the log search covers.)
        var (client, ax, az) = await JoinAsync(name, trees: false, leftoverTrunk: true);
        await using var _ = client;
        await Wait.UntilAsync(() => client.World.GetBlock(ax + 35, 202, az)?.Name != "minecraft:oak_log", Timeout, () => "the leftover trunk is still standing");
        await client.WaitTicksAsync(20);
        var skills = new Skills(client);

        var (ok, text) = await skills.BuildShapeAsync("house", 0, 0, 0, "wood", null, null, null, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.False);
        Assert.That(text, Does.Contain("planks").And.Contain("couldn't cut logs"));
    }

    [Test]
    public async Task BringMeWood_CutsLogsAndHandsThemOver()
    {
        const string name = "VertexBringer";
        var (client, ax, az) = await JoinAsync(name, trees: true);
        await using var _ = client;
        // The one who asked stands nearby (and picks things up the way a player does).
        var aubs = new MinecraftClient(GameProfile.Offline("AubriGets"));
        using var cts0 = new CancellationTokenSource(Timeout);
        await Task.Delay(1500);
        await aubs.ConnectAsync("127.0.0.1", Port, cts0.Token);
        await using var _a = aubs;
        await _server.CommandAsync($"tp AubriGets {ax - 3.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.PlayerName == "AubriGets"), Timeout, () => "Vertex sees the player");
        var skills = new Skills(client);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var (ok, text) = await skills.GiveAsync("AubriGets", "wood", 6, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        // A person who is handed something picks it up: walks to the dropped logs when they are not within reach (they did not always land at their feet: flaky in full runs, 2026-11-03, with a
        // friend who stood still; the drop is thrown a few blocks and Vertex only drops them, it does not check who picked them up).
        int Logs() => aubs.Inventory.Slots.Where(s => !s.IsEmpty && s.Name.EndsWith("_log")).Sum(s => s.Count);
        var deadline = DateTime.UtcNow + Timeout;
        while (Logs() < 6 && DateTime.UtcNow < deadline)
        {
            var (bx, by, bz) = aubs.Position;
            var drop = aubs.Entities.Values.Where(e => e.IsItem && e.DistanceTo(bx, by, bz) < 12).OrderBy(e => e.DistanceTo(bx, by, bz)).FirstOrDefault();
            if (drop != null && drop.DistanceTo(bx, by, bz) > 0.6)
            {
                using var step = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await aubs.GoToAsync(Goal.Near(drop.X, drop.Y, drop.Z, 0.8), new PathOptions { MaxNodes = 5_000 }, step.Token); } catch (OperationCanceledException) { }
            }
            await aubs.WaitTicksAsync(10);
        }
        Assert.That(Logs(), Is.GreaterThanOrEqualTo(6), aubs.Inventory.ToString());
    }
}
