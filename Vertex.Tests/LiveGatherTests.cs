using SpawnDev.Minecraft;
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

    private async Task<(MinecraftClient Client, int X, int Z)> JoinAsync(string name, bool trees)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        const int r = 20;
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax + r} 215 {az + r} minecraft:air");
        await _server.CommandAsync($"fill {ax - r} 199 {az - r} {ax + r} 199 {az + r} minecraft:stone");
        if (trees)
            // Five trunks of oak logs, four high, off to one side: 20 logs, 80 planks.
            for (var i = 0; i < 5; i++)
                await _server.CommandAsync($"fill {ax + 12} 200 {az - 6 + i * 3} {ax + 12} 203 {az - 6 + i * 3} minecraft:oak_log");
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
        var (client, ax, az) = await JoinAsync(name, trees: false);
        await using var _ = client;
        // The trees an earlier test planted nearby (the server is shared) are still standing: take them down.
        await _server.CommandAsync($"fill {ax - 40} 200 {az - 40} {ax + 40} 206 {az + 40} minecraft:air replace minecraft:oak_log");
        await Wait.UntilAsync(() => client.World.GetBlock(ax + 12, 202, az - 6)?.Name != "minecraft:oak_log", Timeout, () => "no trees");
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
        await Wait.UntilAsync(() => aubs.Inventory.Slots.Where(s => !s.IsEmpty && s.Name.EndsWith("_log")).Sum(s => s.Count) >= 6, Timeout, () => aubs.Inventory.ToString());
    }
}
