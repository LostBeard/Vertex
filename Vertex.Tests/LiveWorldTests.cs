using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Items;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// M2 against a REAL Paper 1.21.5 server: the client's world, entities and inventory are checked against the
/// server itself (console `execute if block`, /fill, /summon, /give). Any parse error ends the connection, so
/// "still connected" after heavy traffic is part of every assertion.
/// </summary>
[NonParallelizable]
public class LiveWorldTests
{
    private const int Port = 25924;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;
    private static GameData D => GameData.Vanilla;

    [OneTimeSetUp]
    public async Task StartServer() => _server = await PaperServer.StartAsync("world", Port, TimeSpan.FromMinutes(4));

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    private async Task<MinecraftClient> JoinAsync(string name)
    {
        var client = new MinecraftClient(GameProfile.Offline(name));
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        // view-distance=4: wait for the 9x9 chunks around us.
        await Wait.UntilAsync(() => client.World.LoadedChunks >= 81, Timeout,
            () => $"{client.World.LoadedChunks} chunks loaded, state {client.State}, {client.DisconnectReason}");
        return client;
    }

    private static (int X, int Y, int Z) Feet(MinecraftClient c)
    {
        var (x, y, z) = c.Position;
        return ((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
    }

    private async Task<bool> ServerSaysBlockIsAsync(int x, int y, int z, string block)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        // `execute if block` prints "Test passed" / "Test failed"; a marker command after it pairs the answer.
        var answer = _server.WaitForLineAsync(l => l.Contains("Test passed") || l.Contains("Test failed"), Timeout);
        await _server.CommandAsync($"execute if block {x} {y} {z} {block}");
        return (await answer).Contains("Test passed");
    }

    [Test]
    public async Task Chunks_MatchTheServerBlockForBlock()
    {
        await using var client = await JoinAsync("VertexChunks");
        Assert.That(client.World.MinY, Is.EqualTo(-64));
        Assert.That(client.World.Height, Is.EqualTo(384));

        // We stand on something solid.
        var (fx, fy, fz) = Feet(client);
        Assert.That(D.HasCollision(client.World.GetState(fx, fy - 1, fz)), $"standing on {client.World.GetBlock(fx, fy - 1, fz)}");

        // 40 random loaded positions, natural terrain (many palettes), confirmed one by one by the server.
        var random = new Random(770);
        var checkedSolid = 0;
        for (var i = 0; i < 40; i++)
        {
            int x = fx + random.Next(-60, 61), z = fz + random.Next(-60, 61), y = fy + random.Next(-40, 10);
            var state = client.World.GetState(x, y, z);
            Assert.That(state, Is.Not.EqualTo(SpawnDev.Minecraft.World.GameWorld.Unloaded), $"{x} {y} {z} not loaded");
            var name = D.BlockOf(state).Name;
            if (!D.IsAir(state)) checkedSolid++;
            Assert.That(await ServerSaysBlockIsAsync(x, y, z, name), $"client thinks {x} {y} {z} is {name}");
        }
        Assert.That(checkedSolid, Is.GreaterThan(10), "too few non-air samples to mean anything");
        Assert.That(client.State, Is.EqualTo(ConnectionState.Play));
    }

    [Test]
    public async Task BlockUpdates_AndPropertiesArrive()
    {
        await using var client = await JoinAsync("VertexBlocks");
        var (x, y, z) = Feet(client);

        // One block with properties: block_update.
        await _server.CommandAsync($"setblock {x + 3} {y} {z} minecraft:oak_stairs[facing=east,half=top]");
        await Wait.UntilAsync(() => client.World.GetBlock(x + 3, y, z)?.Name == "minecraft:oak_stairs", Timeout, () => $"{client.World.GetBlock(x + 3, y, z)}");
        var stairs = client.World.GetState(x + 3, y, z);
        Assert.That(D.GetProperty(stairs, "facing"), Is.EqualTo("east"));
        Assert.That(D.GetProperty(stairs, "half"), Is.EqualTo("top"));

        // Many blocks in one go: section_blocks_update.
        await _server.CommandAsync($"fill {x + 5} {y} {z - 2} {x + 7} {y + 2} {z + 2} minecraft:gold_block");
        await Wait.UntilAsync(() => client.World.GetBlock(x + 7, y + 2, z + 2)?.Name == "minecraft:gold_block", Timeout, () => "fill");
        for (var bx = x + 5; bx <= x + 7; bx++)
        for (var by = y; by <= y + 2; by++)
        for (var bz = z - 2; bz <= z + 2; bz++)
            Assert.That(client.World.GetBlock(bx, by, bz)?.Name, Is.EqualTo("minecraft:gold_block"), $"{bx} {by} {bz}");

        // A second player joining now gets the gold inside the chunk data itself (no updates).
        await using var late = await JoinAsync("VertexLate");
        Assert.That(late.World.GetBlock(x + 6, y + 1, z)?.Name, Is.EqualTo("minecraft:gold_block"));
        Assert.That(D.GetProperty(late.World.GetState(x + 3, y, z), "facing"), Is.EqualTo("east"));
        await _server.CommandAsync($"fill {x + 3} {y} {z - 2} {x + 7} {y + 2} {z + 2} minecraft:air");

        // Tags from the server: stone is mineable with a pickaxe.
        Assert.That(client.Tag("minecraft:block", "minecraft:mineable/pickaxe"), Does.Contain(D.Block("stone")!.Id));
    }

    [Test]
    public async Task Entities_SpawnMoveCarryItemsAndDie()
    {
        await using var client = await JoinAsync("VertexMobs");
        var (x, y, z) = Feet(client);

        await _server.CommandAsync($"summon minecraft:pig {x + 2} {y} {z + 2} {{NoAI:1b,Tags:[\"vtest\"]}}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:pig"), Timeout, () => string.Join(", ", client.Entities.Values));
        var pig = client.Entities.Values.First(e => e.Name == "minecraft:pig");
        Assert.That(pig.X, Is.EqualTo(x + 2.5).Within(0.01));
        Assert.That(pig.Z, Is.EqualTo(z + 2.5).Within(0.01));
        await Wait.UntilAsync(() => pig.Health != null, Timeout, () => "pig health metadata");
        Assert.That(pig.Health, Is.EqualTo(10f));

        await _server.CommandAsync($"tp @e[tag=vtest] {x + 2} {y} {z + 5}");
        await Wait.UntilAsync(() => Math.Abs(pig.Z - (z + 5.5)) < 0.01, Timeout, () => $"pig at {pig.Z}");

        await _server.CommandAsync($"summon minecraft:item {x - 2} {y} {z} {{Item:{{id:\"minecraft:diamond\",count:3}},PickupDelay:32767,Tags:[\"vtest\"]}}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.IsItem && e.Item?.Name == "minecraft:diamond"), Timeout,
            () => string.Join(", ", client.Entities.Values.Where(e => e.IsItem).Select(e => e.Item?.ToString())));
        var diamond = client.Entities.Values.First(e => e.IsItem && e.Item?.Name == "minecraft:diamond");
        Assert.That(diamond.Item!.Count, Is.EqualTo(3));

        // The other player is an entity with its name from the player list.
        await using var other = await JoinAsync("VertexFriend");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.PlayerName == "VertexFriend"), Timeout, () => string.Join(", ", client.Entities.Values));

        // Killing the pig drops a porkchop (a new item entity); the two we made must be gone.
        await _server.CommandAsync("kill @e[tag=vtest]");
        await Wait.UntilAsync(() => !client.Entities.ContainsKey(pig.Id) && !client.Entities.ContainsKey(diamond.Id), Timeout, () => string.Join(", ", client.Entities.Values));
    }

    [Test]
    public async Task Inventory_DecodesItemsWithManyComponents()
    {
        await using var client = await JoinAsync("VertexInv");
        // One of each component family Vertex is likely to meet; every give must succeed on the server and
        // decode on the client (a wrong component definition would break the stream and disconnect us).
        string[] gives =
        [
            "diamond_pickaxe[enchantments={\"minecraft:efficiency\":5,\"minecraft:unbreaking\":3},damage=10,custom_name={text:\"Pick\",color:\"gold\"}]",
            "written_book[written_book_content={title:\"Hi\",author:\"Vertex\",pages:[\"page one\",{text:\"two\"}]}]",
            "potion[potion_contents={potion:\"minecraft:strong_healing\"}]",
            "firework_rocket[fireworks={flight_duration:2,explosions:[{shape:\"star\",colors:[I;16711680],has_trail:true}]}] 3",
            "white_banner[banner_patterns=[{pattern:\"minecraft:stripe_top\",color:\"red\"}]]",
            "leather_chestplate[dyed_color=16711680]",
            "shield[base_color=\"red\"]",
            "crossbow[charged_projectiles=[{id:\"minecraft:arrow\",count:1}]]",
            "enchanted_book[stored_enchantments={\"minecraft:mending\":1}]",
            "tipped_arrow[potion_contents={potion:\"minecraft:poison\"}] 16",
            "bundle[bundle_contents=[{id:\"minecraft:diamond\",count:2}]]",
            "chest[container=[{slot:0,item:{id:\"minecraft:apple\",count:3}}]]",
            "diamond_sword[attribute_modifiers=[{type:\"minecraft:attack_damage\",amount:10,operation:\"add_value\",id:\"vertex:test\",slot:\"mainhand\"}]]",
            "goat_horn[instrument=\"minecraft:ponder_goat_horn\"]",
            "suspicious_stew[suspicious_stew_effects=[{id:\"minecraft:night_vision\",duration:100}]]",
            "trident[lore=[{text:\"line one\"}],unbreakable={}]",
            "cooked_beef 64",
            "oak_log 32",
        ];
        foreach (var give in gives)
        {
            var ok = _server.WaitForLineAsync(l => l.Contains("Gave ") || l.Contains("<--[HERE]") || l.Contains("Unknown") || l.Contains("Malformed") || l.Contains("Expected"), Timeout);
            await _server.CommandAsync($"give VertexInv minecraft:{give}");
            var line = await ok;
            Assert.That(line, Does.Contain("Gave "), $"server rejected: give {give}\n{string.Join('\n', _server.OutputTail(4))}");
        }
        await Wait.UntilAsync(() => client.Inventory.Count("oak_log") == 32, Timeout, () => client.Inventory.ToString());
        Assert.That(client.State, Is.EqualTo(ConnectionState.Play), client.DisconnectReason);

        var pick = client.Inventory.Slots.First(s => s.Name == "minecraft:diamond_pickaxe");
        Assert.That(pick.Damage, Is.EqualTo(10));
        Assert.That(pick.CustomName, Is.EqualTo("Pick"));
        Assert.That(pick.Enchantments.Count(), Is.EqualTo(2));
        Assert.That(pick.DurabilityLeft, Is.EqualTo(1551));
        Assert.That(client.Inventory.Count("cooked_beef"), Is.EqualTo(64));
        Assert.That(client.Inventory.Count("firework_rocket"), Is.EqualTo(3));
        Assert.That(client.Inventory.Count("tipped_arrow"), Is.EqualTo(16));
        Assert.That(client.Inventory.Slots.Count(s => !s.IsEmpty), Is.EqualTo(gives.Length));

        // Experience and time.
        await _server.CommandAsync("xp set VertexInv 7 levels");
        await Wait.UntilAsync(() => client.ExperienceLevel == 7, Timeout, () => $"level {client.ExperienceLevel}");
        await _server.CommandAsync("time set 1000");
        await Wait.UntilAsync(() => client.World.DayTime % 24000 is >= 1000 and < 1100, Timeout, () => $"time {client.World.DayTime}");
        Assert.That(client.Attribute("movement_speed"), Is.EqualTo(0.1).Within(1e-6));
    }
}
