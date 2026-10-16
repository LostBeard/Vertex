using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Animal husbandry against a REAL Paper server: two cows fed wheat breed and a baby appears, sheep are sheared, a cow is milked, pigs breed on carrots. The animals live in a small pen with a
/// player standing in it; the server's own selectors say what happened to them (a tagged parent, a sheep still with wool).
/// </summary>
[NonParallelizable]
public class LiveAnimalTests
{
    private const int Port = 25958;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("animals", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    /// <summary>Joins and stands the player in a fenced pen (a 11 x 11 square, walls two high) on a grass arena, at y 200.</summary>
    private async Task<(MinecraftClient Client, int X, int Z)> JoinInPenAsync(string name)
    {
        const int r = 14;
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax + r} 215 {az + r} minecraft:air");
        await _server.CommandAsync($"fill {ax - r} 198 {az - r} {ax + r} 198 {az + r} minecraft:stone");
        await _server.CommandAsync($"fill {ax - r} 199 {az - r} {ax + r} 199 {az + r} minecraft:grass_block");
        // The pen: four walls two blocks high around x-5..x+5, z-5..z+5.
        await _server.CommandAsync($"fill {ax - 5} 200 {az - 5} {ax + 5} 201 {az - 5} minecraft:cobblestone");
        await _server.CommandAsync($"fill {ax - 5} 200 {az + 5} {ax + 5} 201 {az + 5} minecraft:cobblestone");
        await _server.CommandAsync($"fill {ax - 5} 200 {az - 5} {ax - 5} 201 {az + 5} minecraft:cobblestone");
        await _server.CommandAsync($"fill {ax + 5} 200 {az - 5} {ax + 5} 201 {az + 5} minecraft:cobblestone");
        await _server.CommandAsync($"clear {name}");
        await Task.Delay(1500);
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 200) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax - r, az - r) && client.World.IsLoaded(ax + r, az + r)
            && client.World.IsLoaded(ax - r, az + r) && client.World.IsLoaded(ax + r, az - r), Timeout, () => "arena chunks");
        await client.WaitTicksAsync(10);
        return (client, ax, az);
    }

    private async Task Summon(string type, int x, int z, string nbt = "") =>
        await _server.CommandAsync($"summon minecraft:{type} {x + 0.5} 200 {z + 0.5} {{Tags:[\"parent\"]{(nbt.Length > 0 ? "," + nbt : "")}}}");

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
    public async Task TwoCowsFedWheat_BreedAndABabyAppears()
    {
        const string name = "VertexBreed";
        var (client, ax, az) = await JoinInPenAsync(name);
        await using var _ = client;
        await Summon("cow", ax - 3, az - 3);
        await Summon("cow", ax + 3, az + 3);
        await GiveAsync(client, name, "minecraft:wheat", 4);
        await Wait.UntilAsync(() => client.Entities.Values.Count(e => e.Name == "minecraft:cow") == 2, Timeout, () => "the cows");
        var skills = new Skills(client);

        var (ok, text) = await skills.BreedAnimalsAsync("cows", 1, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("1 baby born"));
        await Wait.UntilAsync(() => client.Inventory.Count("wheat") == 2, Timeout, () => client.Inventory.ToString());
        Assert.That(await ServerSaysAsync("execute if entity @e[type=minecraft:cow,tag=!parent]"), Is.True, "the server has a cow that is not one of the two we started with");
    }

    [Test]
    public async Task PigsBreedOnCarrots_AndItSaysWhatItNeedsWhenItLacksFoodOrPartners()
    {
        const string name = "VertexPigs";
        var (client, ax, az) = await JoinInPenAsync(name);
        await using var _ = client;
        var skills = new Skills(client);

        var (none, noneText) = await skills.BreedAnimalsAsync("pigs", 1, CancellationToken.None);
        Assert.That(none, Is.False);
        Assert.That(noneText, Does.Contain("two adult pigs").And.Contain("I only see 0"));

        await Summon("pig", ax - 2, az + 2);
        await Summon("pig", ax + 2, az - 2);
        await Wait.UntilAsync(() => client.Entities.Values.Count(e => e.Name == "minecraft:pig") == 2, Timeout, () => "the pigs");
        var (noFood, noFoodText) = await skills.BreedAnimalsAsync("pig", 1, CancellationToken.None);
        Assert.That(noFood, Is.False);
        Assert.That(noFoodText, Does.Contain("carrot or potato or beetroot").And.Contain("only have 0"));

        await GiveAsync(client, name, "minecraft:carrot", 3);
        var (ok, text) = await skills.BreedAnimalsAsync("pig", 1, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(await ServerSaysAsync("execute if entity @e[type=minecraft:pig,tag=!parent]"), Is.True);

        var (unknown, unknownText) = await skills.BreedAnimalsAsync("dragons", 1, CancellationToken.None);
        Assert.That(unknown, Is.False);
        Assert.That(unknownText, Does.Contain("don't know how to breed dragons"));
    }

    [Test]
    public async Task SheepAreShornAndTheWoolPickedUp()
    {
        const string name = "VertexShear";
        var (client, ax, az) = await JoinInPenAsync(name);
        await using var _ = client;
        await Summon("sheep", ax - 3, az + 1);
        await Summon("sheep", ax + 3, az - 2);
        await Summon("sheep", ax + 1, az + 3, "Color:3b");
        await Wait.UntilAsync(() => client.Entities.Values.Count(e => e.Name == "minecraft:sheep") == 3, Timeout, () => "the sheep");
        var skills = new Skills(client);

        var (noShears, noShearsText) = await skills.ShearSheepAsync(0, CancellationToken.None);
        Assert.That(noShears, Is.False);
        Assert.That(noShearsText, Does.Contain("need shears"));

        await GiveAsync(client, name, "minecraft:shears", 1);
        var (ok, text) = await skills.ShearSheepAsync(0, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("sheared 3 sheep"));
        Assert.That(client.Inventory.Slots.Where(s => s.Name.EndsWith("_wool")).Sum(s => s.Count), Is.GreaterThanOrEqualTo(3), "at least a wool from each sheep");
        Assert.That(await ServerSaysAsync("execute unless entity @e[type=minecraft:sheep,nbt={Sheared:0b}]"), Is.True, "the server has no sheep still carrying wool");

        var (again, againText) = await skills.ShearSheepAsync(0, CancellationToken.None);
        Assert.That(again, Is.False, "nothing left to shear");
        Assert.That(againText, Does.Contain("don't see any sheep with wool"));
    }

    [Test]
    public async Task ACowIsMilked()
    {
        const string name = "VertexMilk";
        var (client, ax, az) = await JoinInPenAsync(name);
        await using var _ = client;
        await Summon("cow", ax - 3, az + 2);
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:cow"), Timeout, () => "the cow");
        var skills = new Skills(client);

        var (noBucket, noBucketText) = await skills.MilkCowAsync(CancellationToken.None);
        Assert.That(noBucket, Is.False);
        Assert.That(noBucketText, Does.Contain("empty bucket"));

        await GiveAsync(client, name, "minecraft:bucket", 1);
        var (ok, text) = await skills.MilkCowAsync(CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("milk_bucket"), Is.EqualTo(1));
        Assert.That(client.Inventory.Count("bucket"), Is.EqualTo(0));
    }
}
