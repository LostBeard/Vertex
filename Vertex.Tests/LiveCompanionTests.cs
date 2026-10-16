using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>Companion skills against a REAL Paper server: lighting up an area with torches so nothing spawns in the dark.</summary>
[NonParallelizable]
public class LiveCompanionTests
{
    private const int Port = 25962;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("companion", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    private async Task<(MinecraftClient Client, int X, int Z)> JoinOnArenaAsync(string name, int r = 20)
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

    private static string BlockAt(MinecraftClient client, int x, int y, int z) => client.World.Data.BlockOf(client.World.GetState(x, y, z)).Name;

    private static List<(int X, int Z)> Torches(MinecraftClient client, int ax, int az, int r = 20) =>
        (from x in Enumerable.Range(ax - r, 2 * r + 1) from z in Enumerable.Range(az - r, 2 * r + 1) where BlockAt(client, x, 200, z) == "minecraft:torch" select (x, z)).ToList();

    [Test]
    public async Task LightUp_PutsTorchesOnAGrid_SkipsWhatIsAlreadyLit_AndLeavesNoDarkSpot()
    {
        const string name = "VertexLight";
        var (client, ax, az) = await JoinOnArenaAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:torch", 30);
        await _server.CommandAsync($"setblock {ax} 200 {az} minecraft:torch");       // one is up already, where we stand
        await Wait.UntilAsync(() => BlockAt(client, ax, 200, az) == "minecraft:torch", Timeout, () => "the first torch");
        var skills = new Skills(client);

        var (ok, text) = await skills.LightUpAsync(12, null, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("torches").And.Contain("6 blocks apart"));
        var torches = Torches(client, ax, az);
        Assert.That(torches, Has.Count.InRange(8, 24), "a dozen or more across a circle of radius 12, with the one already standing counted");
        Assert.That(torches, Does.Contain((ax, az)), "the torch that was up already is still there");
        var used = 30 - client.Inventory.Count("torch");
        Assert.That(used, Is.EqualTo(torches.Count - 1), "one torch per new spot: the lit spot was skipped");

        // No dark spot: every floor cell within 12 blocks of where we stood is within 7 blocks of a torch (a torch lights 7+ blocks at light 8).
        var dark = new List<(int, int)>();
        for (var x = ax - 12; x <= ax + 12; x++)
        for (var z = az - 12; z <= az + 12; z++)
            if ((x - ax) * (x - ax) + (z - az) * (z - az) <= 144 && torches.Min(t => Math.Sqrt((t.X - x) * (t.X - x) + (t.Z - z) * (t.Z - z))) > 7) dark.Add((x - ax, z - az));
        Assert.That(dark, Is.Empty, "dark spots: " + string.Join(" ", dark.Take(10)));
        // The server's own answer for one torch.
        var line = _server.WaitForLineAsync(l => l.Contains("Test passed") || l.Contains("Test failed"), Timeout);
        var far = torches.OrderByDescending(t => Math.Abs(t.X - ax) + Math.Abs(t.Z - az)).First();
        await _server.CommandAsync($"execute if block {far.X} 200 {far.Z} minecraft:torch");
        Assert.That(await line, Does.Contain("Test passed"));

        // Again, from where it started (from anywhere else the circle would reach new ground): it is lit now.
        await client.GoToAsync(SpawnDev.Minecraft.Pathing.Goal.Near(ax + 0.5, 200, az + 0.5, 1.5));
        var (again, againText) = await skills.LightUpAsync(8, null, CancellationToken.None);       // a smaller circle inside the lit one, so a step either way at its edge cannot matter
        Assert.That(again, Is.True);
        Assert.That(againText, Does.Contain("already well lit"));
        Assert.That(client.Inventory.Count("torch"), Is.EqualTo(30 - used), "nothing more was spent");
        Assert.That(Torches(client, ax, az), Has.Count.EqualTo(torches.Count), "and none was added");
    }

    [Test]
    public async Task LightUp_MakesTorchesFromCoalAndSticks_AndSaysWhenItRunsOutOrCannot()
    {
        const string name = "VertexTorchmaker";
        var (client, ax, az) = await JoinOnArenaAsync(name);
        await using var _ = client;
        var skills = new Skills(client);

        var (none, noneText) = await skills.LightUpAsync(10, null, CancellationToken.None);
        Assert.That(none, Is.False);
        Assert.That(noneText, Does.Contain("no torches").And.Contain("couldn't make any"));

        await GiveAsync(client, name, "minecraft:coal", 2);
        await GiveAsync(client, name, "minecraft:stick", 2);
        var (ok, text) = await skills.LightUpAsync(14, null, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("ran out of torches after putting up 8"), "2 coal and 2 sticks make 8 torches, and the area needs more");
        Assert.That(Torches(client, ax, az), Has.Count.EqualTo(8));
        Assert.That(client.Inventory.Count("torch"), Is.EqualTo(0));
    }
}
