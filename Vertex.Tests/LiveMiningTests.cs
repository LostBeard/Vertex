using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>Mining underground against a REAL Paper server: ore known only from the chunk data, a tunnel dug to it, and what makes that safe (lava, water, gravel).</summary>
[NonParallelizable]
public class LiveMiningTests
{
    private const int Port = 25966;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("mining", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    /// <summary>A block of stone 31 x 31 and 30 deep under a grass surface at y 200; the player stands on it.</summary>
    private async Task<(MinecraftClient Client, int X, int Z)> JoinOnRockAsync(string name)
    {
        const int r = 15;
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax + r} 215 {az + r} minecraft:air");
        await _server.CommandAsync($"fill {ax - r} 170 {az - r} {ax + r} 198 {az + r} minecraft:stone");
        await _server.CommandAsync($"fill {ax - r} 199 {az - r} {ax + r} 199 {az + r} minecraft:grass_block");
        await _server.CommandAsync($"clear {name}");
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

    [Test]
    public async Task Collect_DigsDownToOreItCanSee_AndPicksUpTheDropFromThePocketItLeft()
    {
        const string name = "VertexMiner";
        var (client, ax, az) = await JoinOnRockAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:iron_pickaxe", 1);
        await _server.CommandAsync($"setblock {ax + 6} 185 {az + 4} minecraft:diamond_ore");
        await Wait.UntilAsync(() => client.World.Data.BlockOf(client.World.GetState(ax + 6, 185, az + 4)).Name == "minecraft:diamond_ore", Timeout, () => "the ore");
        var skills = new Skills(client);

        var (ok, text) = await skills.CollectAsync("diamond", 1, CancellationToken.None);
        TestContext.Out.WriteLine(text + " | at " + client.Position + " | inventory: " + skills.InventorySummary());
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("diamond"), Is.EqualTo(1));
    }

    [Test]
    public async Task OreUnderLavaIsNotWorthBurningFor()
    {
        const string name = "VertexCareful";
        var (client, ax, az) = await JoinOnRockAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:iron_pickaxe", 1);
        // A diamond with lava all around it: above, and on the four sides (stone below). Opening any side lets lava out.
        await _server.CommandAsync($"setblock {ax + 6} 185 {az + 4} minecraft:diamond_ore");
        foreach (var (dx, dy, dz) in new[] { (0, 1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) })
            await _server.CommandAsync($"setblock {ax + 6 + dx} {185 + dy} {az + 4 + dz} minecraft:lava");
        await Wait.UntilAsync(() => client.World.Data.BlockOf(client.World.GetState(ax + 6, 186, az + 4)).Name == "minecraft:lava", Timeout, () => "the lava");
        var skills = new Skills(client);
        var health = client.Health;

        var (ok, text) = await skills.CollectAsync("diamond", 1, CancellationToken.None);
        TestContext.Out.WriteLine(text + " | health " + client.Health + " | at " + client.Position);
        await client.WaitTicksAsync(40);
        Assert.That(ok, Is.False, "it does not mine it");
        Assert.That(text, Does.Contain("next to lava"), "and says why");
        Assert.That(client.Inventory.Count("diamond"), Is.EqualTo(0));
        Assert.That(client.Health, Is.GreaterThanOrEqualTo(health), "it was not burned or hurt going after a diamond in lava");
        var feet = client.World.Data.BlockOf(client.World.GetState((int)Math.Floor(client.Position.X), (int)Math.Floor(client.Position.Y), (int)Math.Floor(client.Position.Z))).Name;
        Assert.That(feet, Is.Not.EqualTo("minecraft:lava"), "and it is not standing in lava");
    }
}
