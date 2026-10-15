using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Making a new field on a REAL Paper server: Vertex finds soil, pours water in the middle, tills each block with a hoe and sows it; everything is read back from the world the server sends
/// (and the server's own answer for the water), then grown crops are harvested and replanted by the existing farm skill: the whole cycle.
/// </summary>
[NonParallelizable]
public class LiveFarmingTests
{
    private const int Port = 25956;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("farming", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "easy" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    /// <summary>Joins and stands the player in the middle of a grass arena (radius 14) at y 200; the grass is the ground at y 199.</summary>
    private async Task<(MinecraftClient Client, int X, int Z)> JoinOnGrassAsync(string name)
    {
        const int r = 14;
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax + r} 215 {az + r} minecraft:air");
        await _server.CommandAsync($"fill {ax - r} 198 {az - r} {ax + r} 198 {az + r} minecraft:stone");
        await _server.CommandAsync($"fill {ax - r} 199 {az - r} {ax + r} 199 {az + r} minecraft:grass_block");
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

    /// <summary>The field: every x,z where farmland or water is at y 199 (the soil level), as the client sees the world.</summary>
    private static List<(int X, int Z, string Block)> Soil(MinecraftClient client, int ax, int az)
    {
        var found = new List<(int, int, string)>();
        for (var x = ax - 14; x <= ax + 14; x++)
        for (var z = az - 14; z <= az + 14; z++)
        {
            var b = BlockAt(client, x, 199, z);
            if (b is "minecraft:farmland" or "minecraft:water") found.Add((x, z, b));
        }
        return found;
    }

    [Test]
    public async Task AFieldWithWaterInTheMiddle_IsTilledAndSown()
    {
        const string name = "VertexField";
        var (client, ax, az) = await JoinOnGrassAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:wooden_hoe", 1);
        await GiveAsync(client, name, "minecraft:wheat_seeds", 30);
        await GiveAsync(client, name, "minecraft:water_bucket", 1);
        var skills = new Skills(client);

        var (ok, text) = await skills.PlantFieldAsync("wheat", 5, null, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("5 by 5 field of wheat").And.Contain("water in the middle").And.Contain("24 planted"));

        var soil = Soil(client, ax, az);
        var water = soil.Where(s => s.Block == "minecraft:water").ToList();
        var farmland = soil.Where(s => s.Block == "minecraft:farmland").ToList();
        Assert.That(water, Has.Count.EqualTo(1), "one block of water");
        Assert.That(farmland, Has.Count.EqualTo(24), "every other block of the 5 x 5 is farmland");
        var (wx, wz, _) = water[0];
        Assert.That(farmland.All(f => Math.Abs(f.X - wx) <= 2 && Math.Abs(f.Z - wz) <= 2), Is.True, "all within two blocks of the water: it is the middle of the field");
        Assert.That(farmland.Count(f => BlockAt(client, f.X, 200, f.Z) == "minecraft:wheat"), Is.EqualTo(24), "wheat on every block of farmland");
        await Wait.UntilAsync(() => client.Inventory.Count("wheat_seeds") == 6, Timeout, () => client.Inventory.ToString());
        Assert.That(client.Inventory.Count("wheat_seeds"), Is.EqualTo(6), "one seed each");
        Assert.That(client.Inventory.Count("water_bucket"), Is.EqualTo(0), "the bucket was emptied into the hole");

        // The server's own answer for the water and for a corner crop.
        var line = _server.WaitForLineAsync(l => l.Contains("Test passed") || l.Contains("Test failed"), Timeout);
        await _server.CommandAsync($"execute if block {wx} 199 {wz} minecraft:water");
        Assert.That(await line, Does.Contain("Test passed"));
        var corner = farmland.OrderBy(f => f.X).ThenBy(f => f.Z).First();
        line = _server.WaitForLineAsync(l => l.Contains("Test passed") || l.Contains("Test failed"), Timeout);
        await _server.CommandAsync($"execute if block {corner.X} 200 {corner.Z} minecraft:wheat[age=0]");
        Assert.That(await line, Does.Contain("Test passed"), "a freshly sown wheat on the server too");
    }

    [Test]
    public async Task WithoutAHoeItMakesOne_AndBreaksTheTallGrassInTheWay()
    {
        const string name = "VertexNoHoe";
        var (client, ax, az) = await JoinOnGrassAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:oak_planks", 8);       // a hoe needs a crafting table (4 planks), then 2 planks and 2 sticks
        await GiveAsync(client, name, "minecraft:stick", 2);
        await GiveAsync(client, name, "minecraft:carrot", 8);
        // Grass on top of some of the ground everywhere a field could go.
        for (var x = ax - 12; x <= ax + 12; x += 2)
            await _server.CommandAsync($"fill {x} 200 {az - 12} {x} 200 {az + 12} minecraft:short_grass replace minecraft:air");
        await client.WaitTicksAsync(10);
        var skills = new Skills(client);

        Assert.That(client.Inventory.Count("wooden_hoe"), Is.EqualTo(0));
        var (ok, text) = await skills.PlantFieldAsync("carrots", 3, null, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("wooden_hoe"), Is.EqualTo(1), "it made the hoe it needed");
        Assert.That(text, Does.Contain("3 by 3 field of carrots").And.Contain("no water bucket").And.Contain("8 planted"));
        var farmland = Soil(client, ax, az).Where(s => s.Block == "minecraft:farmland").ToList();
        Assert.That(farmland, Has.Count.EqualTo(8), "it tills only what it has a seed for: 8 carrots, 8 blocks of farmland");
        Assert.That(farmland.Count(f => BlockAt(client, f.X, 200, f.Z) == "minecraft:carrots"), Is.EqualTo(8), "8 carrots for the 8 it had; the grass was cleared first");
    }

    [Test]
    public async Task WhenItCannot_ItSaysWhy()
    {
        const string name = "VertexCannot";
        var (client, _, _) = await JoinOnGrassAsync(name);
        await using var _ = client;
        var skills = new Skills(client);

        var (noSeeds, a) = await skills.PlantFieldAsync("wheat", 5, null, CancellationToken.None);
        Assert.That(noSeeds, Is.False);
        Assert.That(a, Does.Contain("don't have any wheat seeds"));

        var (unknown, b) = await skills.PlantFieldAsync("dragonfruit", 5, null, CancellationToken.None);
        Assert.That(unknown, Is.False);
        Assert.That(b, Does.Contain("don't know how to grow dragonfruit").And.Contain("wheat, carrots, potatoes or beetroot"));

        await GiveAsync(client, name, "minecraft:potato", 4);
        var (noHoe, c) = await skills.PlantFieldAsync("potatoes", 3, null, CancellationToken.None);
        Assert.That(noHoe, Is.False);
        Assert.That(c, Does.Contain("need a hoe").And.Contain("couldn't make one"));
    }

    [Test]
    public async Task TheWholeCycle_PlantGrowHarvestReplant()
    {
        const string name = "VertexCycle";
        var (client, ax, az) = await JoinOnGrassAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:stone_hoe", 1);
        await GiveAsync(client, name, "minecraft:wheat_seeds", 12);
        await GiveAsync(client, name, "minecraft:water_bucket", 1);
        var skills = new Skills(client);

        var (ok, text) = await skills.PlantFieldAsync("wheat", 3, null, CancellationToken.None);
        TestContext.Out.WriteLine(text + " | inventory: " + skills.InventorySummary());
        Assert.That(ok, Is.True, text);
        var farmland = Soil(client, ax, az).Where(s => s.Block == "minecraft:farmland").ToList();
        Assert.That(farmland, Has.Count.EqualTo(8));
        // The server confirms a crop block before it sends the inventory change for the seed that made it: wait for the inventory.
        await Wait.UntilAsync(() => client.Inventory.Count("wheat_seeds") == 12 - 8, Timeout, () => client.Inventory.ToString());
        var seedsAfterPlanting = client.Inventory.Count("wheat_seeds");
        Assert.That(text, Does.Contain("8 planted"));

        // Time passes: the wheat is ripe (the server's own change, as a few days would be).
        var minX = farmland.Min(f => f.X); var maxX = farmland.Max(f => f.X); var minZ = farmland.Min(f => f.Z); var maxZ = farmland.Max(f => f.Z);
        await _server.CommandAsync($"fill {minX} 200 {minZ} {maxX} 200 {maxZ} minecraft:wheat[age=7] replace minecraft:wheat");
        await Wait.UntilAsync(() => farmland.All(f => client.World.Data.GetProperty(client.World.GetState(f.X, 200, f.Z), "age") == "7"), Timeout, () => "the wheat ripening");

        var (harvested, harvestText) = await skills.FarmAsync(CancellationToken.None);
        TestContext.Out.WriteLine(harvestText);
        Assert.That(harvested, Is.True, harvestText);
        Assert.That(harvestText, Does.Contain("harvested 8 crops").And.Contain("replanted"));
        Assert.That(client.Inventory.Count("wheat"), Is.GreaterThanOrEqualTo(8), "a wheat from each plant");
        Assert.That(farmland.Count(f => BlockAt(client, f.X, 200, f.Z) == "minecraft:wheat"), Is.GreaterThanOrEqualTo(6), "and the field is sown again");
        Assert.That(seedsAfterPlanting, Is.EqualTo(12 - 8));
    }
}
