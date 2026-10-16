using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>Fighting against a REAL Paper server: a bow, drawn and aimed with the arrow's real flight, brings down mobs standing still and walking. Husks are used: they do not burn in daylight.</summary>
[NonParallelizable]
public class LiveFightingTests
{
    private const int Port = 25964;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("fighting", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "easy" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    private async Task<(MinecraftClient Client, int X, int Z)> JoinOnArenaAsync(string name, int r = 30)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax + r} 215 {az + r} minecraft:air");
        await _server.CommandAsync($"fill {ax - r} 199 {az - r} {ax + r} 199 {az + r} minecraft:stone");
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

    private async Task<bool> ServerSaysAsync(string execute)
    {
        var line = _server.WaitForLineAsync(l => l.Contains("Test passed") || l.Contains("Test failed"), Timeout);
        await _server.CommandAsync(execute);
        return (await line).Contains("Test passed");
    }

    [Test]
    public async Task ABowBringsDownAMobStandingFarAway_WithoutGoingNearIt()
    {
        const string name = "VertexArcher";
        var (client, ax, az) = await JoinOnArenaAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:bow", 1);
        await GiveAsync(client, name, "minecraft:arrow", 24);
        await _server.CommandAsync($"summon minecraft:husk {ax + 14.5} 200 {az + 0.5} {{NoAI:1b,Silent:1b}}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:husk"), Timeout, () => "the husk");
        var start = client.Position;
        var skills = new Skills(client);

        var (ok, text) = await skills.ShootAsync("husk", CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("brought down 1 husk"));
        Assert.That(await ServerSaysAsync("execute unless entity @e[type=minecraft:husk]"), Is.True, "the server has no husk left");
        var moved = Math.Sqrt(Math.Pow(client.Position.X - start.X, 2) + Math.Pow(client.Position.Z - start.Z, 2));
        Assert.That(moved, Is.LessThan(3), "it shot from where it stood, 14 blocks away");
        await Wait.UntilAsync(() => client.Inventory.Count("arrow") < 24, Timeout, () => client.Inventory.ToString());
        var used = 24 - client.Inventory.Count("arrow");
        TestContext.Out.WriteLine($"arrows used: {used}");
        Assert.That(used, Is.InRange(2, 14), "a husk has 20 health and a full-draw arrow does 6 to 9: two to four hits, a few misses allowed");
    }

    [Test]
    public async Task ABowBringsDownAMobWalkingTowardIt()
    {
        const string name = "VertexArcher2";
        var (client, ax, az) = await JoinOnArenaAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:bow", 1);
        await GiveAsync(client, name, "minecraft:arrow", 32);
        await GiveAsync(client, name, "minecraft:iron_sword", 1);
        await _server.CommandAsync($"summon minecraft:husk {ax - 24.5} 200 {az + 6.5}");      // with its wits about it: it comes for us
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:husk"), Timeout, () => "the husk");
        var skills = new Skills(client);

        var (ok, text) = await skills.ShootAsync("husk", CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("brought down 1 husk"));
        Assert.That(await ServerSaysAsync("execute unless entity @e[type=minecraft:husk]"), Is.True);
        await Wait.UntilAsync(() => client.Inventory.Count("arrow") < 32, Timeout, () => client.Inventory.ToString());
        Assert.That(32 - client.Inventory.Count("arrow"), Is.GreaterThanOrEqualTo(1), "at least one arrow flew before it was close");
        Assert.That(client.Health, Is.GreaterThan(8), "and it was not badly hurt doing it");
    }

    [Test]
    public async Task WithoutABowOrArrowsOrATarget_ItSaysSo()
    {
        const string name = "VertexNoBow";
        var (client, ax, az) = await JoinOnArenaAsync(name);
        await using var _ = client;
        var skills = new Skills(client);

        var (noBow, a) = await skills.ShootAsync("zombie", CancellationToken.None);
        Assert.That(noBow, Is.False);
        Assert.That(a, Does.Contain("need a bow"));

        await GiveAsync(client, name, "minecraft:bow", 1);
        await _server.CommandAsync($"summon minecraft:husk {ax + 14.5} 200 {az + 0.5} {{NoAI:1b,Silent:1b}}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:husk"), Timeout, () => "the husk");
        var (noArrows, b) = await skills.ShootAsync("husk", CancellationToken.None);
        Assert.That(noArrows, Is.False);
        Assert.That(b, Does.Contain("ran out of arrows"));

        await GiveAsync(client, name, "minecraft:arrow", 2);
        var (none, c) = await skills.ShootAsync("creeper", CancellationToken.None);
        Assert.That(none, Is.False);
        Assert.That(c, Does.Contain("don't see any creeper"));
    }

    [Test]
    public async Task GuardShootsACreeperComingForItsFriend_BeforeItIsClose() => await GuardAgainstACreeperAsync("VertexGuardArch", bow: true);

    [Test]
    public async Task GuardWithoutABow_Baseline() => await GuardAgainstACreeperAsync("VertexGuardHand", bow: false);

    private async Task GuardAgainstACreeperAsync(string name, bool bow)
    {
        var (client, ax, az) = await JoinOnArenaAsync(name);
        await using var _ = client;
        await Task.Delay(1500);
        var friend = new MinecraftClient(GameProfile.Offline("AubriWard"));
        using var cts0 = new CancellationTokenSource(Timeout);
        await friend.ConnectAsync("127.0.0.1", Port, cts0.Token);
        await using var _f = friend;
        await _server.CommandAsync($"tp AubriWard {ax + 2.5} 200 {az + 0.5}");
        if (bow)
        {
            await GiveAsync(client, name, "minecraft:bow", 1);
            await GiveAsync(client, name, "minecraft:arrow", 32);
        }
        await GiveAsync(client, name, "minecraft:iron_sword", 1);
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.PlayerName == "AubriWard"), Timeout, () => "Vertex sees its friend");
        var skills = new Skills(client);
        var health = client.Health;

        using var guardCts = new CancellationTokenSource();
        var guard = skills.GuardAsync("AubriWard", guardCts.Token);
        await _server.CommandAsync($"summon minecraft:creeper {ax - 13.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:creeper"), Timeout, () => "the creeper");
        // It walks in (13 blocks off: a creeper only notices a player within 16); Vertex should bring it down.
        var watchStart = DateTime.UtcNow;
        while (DateTime.UtcNow - watchStart < TimeSpan.FromSeconds(40) && client.Entities.Values.Any(e => e.Name == "minecraft:creeper" && e.Health is not <= 0))
        {
            var cr = client.Entities.Values.FirstOrDefault(e => e.Name == "minecraft:creeper");
            if (cr != null) TestContext.Out.WriteLine($"t+{(DateTime.UtcNow - watchStart).TotalSeconds:0.0}s creeper {cr.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z):0.0} away, swell {(cr.Metadata.TryGetValue(16, out var sw) ? sw : "?")}, hp {cr.Health}; me {client.Health} at {client.Position.X:0.0},{client.Position.Z:0.0}");
            await Task.Delay(1000);
        }
        var lowest = client.Health;
        guardCts.Cancel();
        try { await guard; } catch (OperationCanceledException) { }
        TestContext.Out.WriteLine($"health {health} -> {lowest}; arrows left {client.Inventory.Count("arrow")}");
        Assert.That(await ServerSaysAsync("execute unless entity @e[type=minecraft:creeper]"), Is.True, "the server has no creeper left");
        if (bow) Assert.That(client.Inventory.Count("arrow"), Is.LessThan(32), "it shot");
        Assert.That(client.Health, Is.GreaterThanOrEqualTo(health - 2), "and was not blown up: it never let the creeper close");
    }
}
