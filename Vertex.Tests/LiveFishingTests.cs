using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>Fishing against a REAL Paper server: a pond in a stone basin, a rod (with Lure so the test does not wait minutes), and the bobber's real bite.</summary>
[NonParallelizable]
public class LiveFishingTests
{
    private const int Port = 25970;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("fishing", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
        await _server.CommandAsync("weather clear");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    private async Task<(MinecraftClient Client, int X, int Z)> JoinBesideAPondAsync(string name)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        const int r = 14;
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        await _server.CommandAsync($"fill {ax - r} 197 {az - r} {ax + r} 215 {az + r} minecraft:air");
        await _server.CommandAsync($"fill {ax - r} 199 {az - r} {ax + r} 199 {az + r} minecraft:stone");
        // A basin: stone walls and floor around a pond 7 long, 9 wide, 2 deep, its top level with the floor.
        await _server.CommandAsync($"fill {ax + 2} 197 {az - 5} {ax + 10} 199 {az + 5} minecraft:stone");
        await _server.CommandAsync($"fill {ax + 3} 198 {az - 4} {ax + 9} 199 {az + 4} minecraft:water");
        await _server.CommandAsync($"clear {name}");
        await Task.Delay(1500);
        await _server.CommandAsync($"tp {name} {ax - 4.5} 200 {az + 0.5} -90 0");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 200) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax - r, az - r) && client.World.IsLoaded(ax + r, az + r)
            && client.World.IsLoaded(ax - r, az + r) && client.World.IsLoaded(ax + r, az - r), Timeout, () => "arena chunks");
        await Wait.UntilAsync(() => client.World.GetBlock(ax + 5, 199, az)?.Name == "minecraft:water", Timeout, () => "the pond");
        await client.WaitTicksAsync(10);
        return (client, ax, az);
    }

    [Test]
    public async Task WithoutARod_ItSaysSo_AndWithoutWater_ItSaysThat()
    {
        const string name = "VertexNoRod";
        var (client, ax, az) = await JoinBesideAPondAsync(name);
        await using var _ = client;
        var skills = new Skills(client);
        var (ok, text) = await skills.FishAsync(1, CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(text, Does.Contain("need a fishing rod"));

        await _server.CommandAsync($"give {name} minecraft:fishing_rod 1");
        await Wait.UntilAsync(() => client.Inventory.Count("fishing_rod") == 1, Timeout, () => client.Inventory.ToString());
        await _server.CommandAsync($"fill {ax - 30} 198 {az - 30} {ax + 30} 199 {az + 30} minecraft:air replace minecraft:water");       // this pond and any pond an earlier test made nearby
        await Wait.UntilAsync(() => client.World.GetBlock(ax + 5, 199, az)?.Name == "minecraft:air", Timeout, () => "the pond gone");
        var (ok2, text2) = await skills.FishAsync(1, CancellationToken.None);
        Assert.That(ok2, Is.False);
        Assert.That(text2, Does.Contain("open water"));
    }

    [Test]
    public async Task ItWalksToThePond_CastsAndLandsAFish()
    {
        const string name = "VertexFisher";
        var (client, ax, az) = await JoinBesideAPondAsync(name);
        await using var _ = client;
        await _server.CommandAsync($"give {name} minecraft:fishing_rod[enchantments={{\"minecraft:lure\":3}}] 1");
        await Wait.UntilAsync(() => client.Inventory.Count("fishing_rod") == 1, Timeout, () => client.Inventory.ToString());
        var skills = new Skills(client);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var (ok, text) = await skills.FishAsync(1, cts.Token);
        TestContext.Out.WriteLine(text + $" | at {client.Position}");
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("I caught"));
        Assert.That(client.Position.X, Is.InRange(ax + 1.0, ax + 2.99), "it stood at the edge of the pond");
    }
}
