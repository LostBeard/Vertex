using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Entities;
using SpawnDev.Minecraft.Items;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Trading with villagers against a REAL Paper server: a villager is summoned with known trades (deterministic), Vertex opens its screen, reads the offers the server sends, and trades;
/// every result is judged by the server (how often it says the trade was used, what the inventory holds).
/// </summary>
[NonParallelizable]
public class LiveTradingTests
{
    private const int Port = 25952;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("trading", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "easy" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    // Four trades with known numbers: sell wheat, buy bread, one that is already sold out (a second ingredient), and one whose price has risen with demand (15 coal + floor(15 * 5 * 0.05) = 18).
    private const string Offers =
        "Offers:{Recipes:[" +
        "{buy:{id:\"minecraft:wheat\",count:20},sell:{id:\"minecraft:emerald\",count:1},maxUses:12,uses:0,rewardExp:1b,xp:2,priceMultiplier:0.05f}," +
        "{buy:{id:\"minecraft:emerald\",count:1},sell:{id:\"minecraft:bread\",count:6},maxUses:16,uses:0,rewardExp:1b,xp:1,priceMultiplier:0.05f}," +
        "{buy:{id:\"minecraft:emerald\",count:3},buyB:{id:\"minecraft:iron_ingot\",count:2},sell:{id:\"minecraft:diamond\",count:1},maxUses:2,uses:2,rewardExp:1b,xp:5,priceMultiplier:0.05f}," +
        "{buy:{id:\"minecraft:coal\",count:15},sell:{id:\"minecraft:emerald\",count:1},maxUses:16,uses:0,rewardExp:1b,xp:2,demand:5,priceMultiplier:0.05f}" +
        "]}";

    private async Task<(MinecraftClient Client, Entity Villager)> JoinWithVillagerAsync(string name, string villagerNbt, double distance = 2.5)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - 6} 200 {az - 6} {ax + 6} 210 {az + 6} minecraft:air");
        await _server.CommandAsync($"fill {ax - 6} 199 {az - 6} {ax + 6} 199 {az + 6} minecraft:stone");
        await _server.CommandAsync($"clear {name}");
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        await Task.Delay(1500);
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
        await _server.CommandAsync($"summon minecraft:villager {ax + distance} 200 {az + 0.5} {{NoAI:1b,Silent:1b,PersistenceRequired:1b,{villagerNbt}}}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 200) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax - 6, az - 6) && client.World.IsLoaded(ax + 6, az + 6)
            && client.World.IsLoaded(ax - 6, az + 6) && client.World.IsLoaded(ax + 6, az - 6), Timeout, () => "arena chunks");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Type.Name == "minecraft:villager"), Timeout, () => "the villager never arrived");
        await client.WaitTicksAsync(10);
        return (client, client.Entities.Values.First(e => e.Type.Name == "minecraft:villager"));
    }

    private async Task<string> ServerSaysAsync(string dataGet)
    {
        var line = _server.WaitForLineAsync(l => l.Contains("entity data"), Timeout);
        await _server.CommandAsync(dataGet);
        return await line;
    }

    private async Task GiveAsync(MinecraftClient client, string name, string item, int count)
    {
        var before = client.Inventory.Count(item.Replace("minecraft:", ""));
        await _server.CommandAsync($"give {name} {item} {count}");
        await Wait.UntilAsync(() => client.Inventory.Count(item.Replace("minecraft:", "")) == before + count, Timeout, () => client.Inventory.ToString());
    }

    [Test]
    public async Task OpenTheScreen_ReadTheOffers_AndTrade()
    {
        const string name = "VertexTrade";
        var (client, villager) = await JoinWithVillagerAsync(name, "VillagerData:{profession:\"minecraft:farmer\",level:2,type:\"minecraft:plains\"},Xp:10," + Offers);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:wheat", 45);

        var window = await client.OpenTradesAsync(villager);
        Assert.That(window, Is.Not.Null, "the villager's trading screen opened with its trades");
        foreach (var t in window!.Trades) TestContext.Out.WriteLine("offer: " + t);
        Assert.That(window.Trades, Has.Count.EqualTo(4));

        var wheat = window.Trades[0];
        Assert.That((wheat.CostA.Name, wheat.CostA.Count, wheat.Result.Name, wheat.Result.Count), Is.EqualTo(("minecraft:wheat", 20, "minecraft:emerald", 1)));
        Assert.That((wheat.MaxUses, wheat.Uses, wheat.UsesLeft, wheat.OutOfStock), Is.EqualTo((12, 0, 12, false)));
        Assert.That(window.Trades[1].Gives("bread") && window.Trades[1].Wants("emerald"), Is.True, "match by short name");
        var soldOut = window.Trades[2];
        Assert.That((soldOut.OutOfStock, soldOut.UsesLeft, soldOut.CostB?.Name, soldOut.CostB?.Count), Is.EqualTo((true, 0, "minecraft:iron_ingot", 2)), "the second ingredient and the sold-out flag");
        Assert.That(window.Trades[3].PriceA, Is.EqualTo(18), "15 coal + floor(15 * demand 5 * 0.05) = 18: the price the server charges");
        Assert.That(window.MerchantLevel, Is.EqualTo(2));

        // Sell wheat twice: 45 wheat -> 2 emeralds, 5 wheat left.
        Assert.That(await client.TradeAsync(0, 2), Is.EqualTo(2));
        Assert.That(client.Inventory.Count("emerald"), Is.EqualTo(2));
        Assert.That(client.Inventory.Count("wheat"), Is.EqualTo(5));
        // Buy bread with both emeralds: 12 bread.
        Assert.That(await client.TradeAsync(1, 2), Is.EqualTo(2));
        Assert.That(client.Inventory.Count("bread"), Is.EqualTo(12));
        Assert.That(client.Inventory.Count("emerald"), Is.EqualTo(0));
        await client.CloseWindowAsync();

        // The server's own count of how often each trade was used.
        Assert.That(await ServerSaysAsync("data get entity @e[type=minecraft:villager,limit=1] Offers.Recipes[0].uses"), Does.EndWith(": 2"));
        Assert.That(await ServerSaysAsync("data get entity @e[type=minecraft:villager,limit=1] Offers.Recipes[1].uses"), Does.EndWith(": 2"));
    }

    [Test]
    public async Task ATradeThatCannotHappenTradesNothing_SoldOut_NoIngredients_OrNotEnough()
    {
        const string name = "VertexNoTrade";
        var (client, villager) = await JoinWithVillagerAsync(name, "VillagerData:{profession:\"minecraft:farmer\",level:2,type:\"minecraft:plains\"},Xp:10," + Offers);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:wheat", 19);       // one short of a trade
        await GiveAsync(client, name, "minecraft:emerald", 3);
        await GiveAsync(client, name, "minecraft:iron_ingot", 2);

        var window = await client.OpenTradesAsync(villager);
        Assert.That(window, Is.Not.Null);
        Assert.That(await client.TradeAsync(0, 1), Is.EqualTo(0), "19 wheat is not 20");
        Assert.That(await client.TradeAsync(2, 1), Is.EqualTo(0), "sold out, even with both ingredients in hand");
        Assert.That(client.Inventory.Count("wheat"), Is.EqualTo(19), "nothing was taken");
        Assert.That(client.Inventory.Count("emerald"), Is.EqualTo(3));
        Assert.That(client.Inventory.Count("diamond"), Is.EqualTo(0));
        Assert.That(await client.TradeAsync(9, 1), Is.EqualTo(0), "no such trade");
        await client.CloseWindowAsync();
    }

    [Test]
    public async Task AVillagerWithNoJobDoesNotTrade()
    {
        const string name = "VertexNitwit";
        var (client, villager) = await JoinWithVillagerAsync(name, "VillagerData:{profession:\"minecraft:nitwit\",level:1,type:\"minecraft:plains\"}");
        await using var _ = client;
        Assert.That(await client.OpenTradesAsync(villager), Is.Null, "a nitwit shakes its head: no screen");
        Assert.That(client.TradingScreenOpen, Is.False);
    }

    // ---- the skill: what the language model calls ("sell my wheat", "buy 12 bread", "what does it sell?")

    private const string Farmer = "VillagerData:{profession:\"minecraft:farmer\",level:2,type:\"minecraft:plains\"},Xp:10,";

    [Test]
    public async Task Skill_SellsAllOfSomething_WalkingToTheVillagerFirst()
    {
        const string name = "VertexSell";
        var (client, _) = await JoinWithVillagerAsync(name, Farmer + Offers, distance: 5.5);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:wheat", 45);
        var skills = new Vertex.Brain.Skills(client);

        var (ok, text) = await skills.TradeWithVillagerAsync(null, "wheat", 0, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(text, Does.Contain("40 wheat").And.Contain("2 emerald").And.Contain("2 trades"));
        Assert.That(client.Inventory.Count("emerald"), Is.EqualTo(2));
        Assert.That(client.Inventory.Count("wheat"), Is.EqualTo(5), "only whole trades: the last 5 are kept");
        Assert.That(client.OpenWindow, Is.Null, "the screen is closed afterwards");
    }

    [Test]
    public async Task Skill_BuysAQuantity_AndSaysWhenItCannotPay_OrTheVillagerIsSoldOut()
    {
        const string name = "VertexBuy";
        var (client, _) = await JoinWithVillagerAsync(name, Farmer + Offers);
        await using var _ = client;
        var skills = new Vertex.Brain.Skills(client);

        var (poor, poorText) = await skills.TradeWithVillagerAsync("bread", null, 12, CancellationToken.None);
        Assert.That(poor, Is.False);
        Assert.That(poorText, Does.Contain("can't pay").And.Contain("1 emerald").And.Contain("I have 0"));

        await GiveAsync(client, name, "minecraft:emerald", 5);
        var (ok, text) = await skills.TradeWithVillagerAsync("bread", null, 12, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("bread"), Is.EqualTo(12), "12 bread is two trades of 6");
        Assert.That(client.Inventory.Count("emerald"), Is.EqualTo(3));

        await GiveAsync(client, name, "minecraft:iron_ingot", 2);
        var (soldOut, soldText) = await skills.TradeWithVillagerAsync("diamond", null, 1, CancellationToken.None);
        Assert.That(soldOut, Is.False);
        Assert.That(soldText, Does.Contain("sold out"));
        Assert.That(client.Inventory.Count("emerald"), Is.EqualTo(3), "nothing was spent");
    }

    [Test]
    public async Task Skill_PaysTheDemandRaisedPriceTheServerCharges_AndListsOffers()
    {
        const string name = "VertexDemand";
        var (client, _) = await JoinWithVillagerAsync(name, Farmer + Offers);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:coal", 40);
        var skills = new Vertex.Brain.Skills(client);

        var (listed, list) = await skills.VillagerOffersAsync(CancellationToken.None);
        TestContext.Out.WriteLine(list);
        Assert.That(listed, Is.True);
        Assert.That(list, Does.Contain("apprentice villager").And.Contain("20 wheat for 1 emerald").And.Contain("18 coal for 1 emerald").And.Contain("(sold out)"));
        Assert.That(client.OpenWindow, Is.Null, "looking buys nothing and closes the screen");

        var (ok, text) = await skills.TradeWithVillagerAsync("emerald", "coal", 2, CancellationToken.None);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("emerald"), Is.EqualTo(2));
        Assert.That(client.Inventory.Count("coal"), Is.EqualTo(40 - 36), "two trades at the raised price of 18, not the base 15");
    }

    [Test]
    public async Task Skill_SaysSoWhenThereIsNoVillager_OrItHasNothingToDoWithIt()
    {
        const string name = "VertexNobody";
        var (client, villager) = await JoinWithVillagerAsync(name, Farmer + Offers);
        await using var _ = client;
        var skills = new Vertex.Brain.Skills(client);

        var (ok, text) = await skills.TradeWithVillagerAsync("netherite ingot", null, 1, CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(text, Does.Contain("doesn't sell netherite ingot").And.Contain("It offers:"), "it says what it does offer");

        await _server.CommandAsync("kill @e[type=minecraft:villager]");
        await Wait.UntilAsync(() => !client.Entities.Values.Any(e => e.Name == "minecraft:villager"), Timeout, () => "the villager is still there");
        var (none, noneText) = await skills.TradeWithVillagerAsync("emerald", "wheat", 1, CancellationToken.None);
        Assert.That(none, Is.False);
        Assert.That(noneText, Does.Contain("don't see a villager"));
    }
}
