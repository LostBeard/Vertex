using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Crafting;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>Crafting with real container clicks, judged by a REAL Paper server (it computes every result itself).</summary>
[NonParallelizable]
public class LiveCraftingTests
{
    private const int Port = 25938;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("craft", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    private async Task<MinecraftClient> JoinOnPlatformAsync(string name)
    {
        var client = new MinecraftClient(GameProfile.Offline(name));
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - 4} 200 {az - 4} {ax + 4} 204 {az + 4} minecraft:air");
        await _server.CommandAsync($"fill {ax - 4} 199 {az - 4} {ax + 4} 199 {az + 4} minecraft:stone");
        await _server.CommandAsync($"clear {name}");
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 200) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        await Wait.UntilAsync(() => client.Inventory.Slots.All(s => s.IsEmpty), Timeout, () => "cleared");
        return client;
    }

    [Test]
    public void RecipeBook_KnowsTheBasics()
    {
        var book = RecipeBook.Vanilla;
        var pick = book.RecipesFor("wooden_pickaxe").Single();
        Assert.That((pick.Width, pick.Height), Is.EqualTo((3, 3)));
        Assert.That(pick.Cells[0]!.Matches("minecraft:oak_planks") && pick.Cells[0]!.Matches("minecraft:birch_planks"));
        Assert.That(pick.Cells[3], Is.Null, "the pickaxe shape has empty corners");
        Assert.That(pick.Cells[4]!.Matches("minecraft:stick"));
        var planks = book.RecipesFor("oak_planks").Single();
        Assert.That((planks.ResultCount, planks.FitsInInventory, planks.Shapeless), Is.EqualTo((4, true, true)));
        Assert.That(book.CookingRecipesFor("iron_ingot").Any(r => r.Input.Matches("minecraft:raw_iron")));
    }

    [Test]
    public async Task WoodenPickaxe_FromThreeLogs()
    {
        await using var client = await JoinOnPlatformAsync("VertexCraft");
        await _server.CommandAsync("give VertexCraft minecraft:oak_log 3");
        await Wait.UntilAsync(() => client.Inventory.Count("oak_log") == 3, Timeout, () => client.Inventory.ToString());

        // planks (2x2) -> sticks (2x2) -> crafting table (2x2) -> placed -> pickaxe (3x3 in the table).
        var (ok, message) = await client.CraftItemAsync("wooden_pickaxe", 1);
        TestContext.Out.WriteLine(message + " | inventory: " + client.Inventory);
        Assert.That(ok, message);
        Assert.That(client.Inventory.Count("wooden_pickaxe"), Is.EqualTo(1));
        var (x, y, z) = client.Position;
        Assert.That(client.World.FindBlocks(s => s > 0 && client.World.Data.BlockOf(s).Name == "minecraft:crafting_table", (x, y, z), 4, 1), Is.Not.Empty, "the table we made was placed");
        Assert.That(client.OpenWindow, Is.Null, "closed the table afterwards");
    }

    [Test]
    public async Task Torches_InTheInventoryGrid()
    {
        await using var client = await JoinOnPlatformAsync("VertexTorch");
        await _server.CommandAsync("give VertexTorch minecraft:coal 2");
        await _server.CommandAsync("give VertexTorch minecraft:stick 2");
        await Wait.UntilAsync(() => client.Inventory.Count("coal") == 2 && client.Inventory.Count("stick") == 2, Timeout, () => client.Inventory.ToString());
        var (ok, message) = await client.CraftItemAsync("torch", 8);
        Assert.That(ok, message);
        Assert.That(client.Inventory.Count("torch"), Is.EqualTo(8));
        Assert.That(client.Inventory.Count("coal") + client.Inventory.Count("stick"), Is.Zero, "used exactly two batches");
        // Asking for more than we can make says what is missing.
        var (more, why) = await client.CraftItemAsync("torch", 4);
        Assert.That(more, Is.False);
        Assert.That(why, Does.Contain("need"));
    }
}
