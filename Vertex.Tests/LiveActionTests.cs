using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Items;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// M3 actions against a REAL Paper server: every action is judged by the server (it re-checks break timing, reach
/// and placement), and results are read back from what the server sends.
/// </summary>
[NonParallelizable]
public class LiveActionTests
{
    private const int Port = 25928;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        // Not spawn-monsters=false: CraftBukkit then discards even SUMMONED monsters the moment they appear
        // (found: the test zombie never reached any client). Stop natural spawning with the gamerule instead.
        _server = await PaperServer.StartAsync("actions", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "easy" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    /// <summary>Joins and stands the player at (ax+0.5, 200, az+0.5) on a stone floor, facing +X.</summary>
    private async Task<(MinecraftClient Client, int X, int Z)> JoinOnArenaAsync(string name, Func<int, int, string[]> build)
    {
        var client = new MinecraftClient(GameProfile.Offline(name));
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - 6} 200 {az - 6} {ax + 6} 210 {az + 6} minecraft:air");
        await _server.CommandAsync($"fill {ax - 6} 199 {az - 6} {ax + 6} 199 {az + 6} minecraft:stone");
        await _server.CommandAsync($"clear {name}");
        foreach (var cmd in build(ax, az)) await _server.CommandAsync(cmd);
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 200) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        // Every arena block must be in a chunk we have (the arena can straddle a chunk border).
        await Wait.UntilAsync(() => client.World.IsLoaded(ax - 6, az - 6) && client.World.IsLoaded(ax + 6, az + 6)
            && client.World.IsLoaded(ax - 6, az + 6) && client.World.IsLoaded(ax + 6, az - 6), Timeout, () => "arena chunks");
        return (client, ax, az);
    }

    private async Task GiveAsync(MinecraftClient client, string name, string item, int count, Func<bool> arrived)
    {
        await _server.CommandAsync($"give {name} {item} {count}");
        await Wait.UntilAsync(arrived, Timeout, () => client.Inventory.ToString());
    }

    /// <summary>
    /// Drops scatter randomly and can land just outside pickup reach: if it is not picked up where we stand, step
    /// toward the item like a player would.
    /// </summary>
    private static async Task PickUpAsync(MinecraftClient client, string item, int count)
    {
        if (await client.WaitUntilAsync(() => client.Inventory.Count(item) == count, 20)) return;
        var drop = client.Entities.Values.FirstOrDefault(e => e.IsItem && e.Item?.Name == "minecraft:" + item);
        if (drop != null)
        {
            var (px, _, pz) = client.Position;
            client.Look((float)(Math.Atan2(-(drop.X - px), drop.Z - pz) * 180 / Math.PI), 0);
            client.Controls.Forward = true;
            await client.WaitUntilAsync(() => client.Inventory.Count(item) == count, 15);
            client.Controls.Clear();
        }
        await Wait.UntilAsync(() => client.Inventory.Count(item) == count, Timeout, () => $"{item} pickup: inventory [{client.Inventory}], at {client.Position}, items: "
            + string.Join("; ", client.Entities.Values.Where(e => e.IsItem).Select(e => $"{e.Item} at {e.X:0.##} {e.Y:0.##} {e.Z:0.##}")));
    }

    [Test]
    public async Task Dig_ByHandAndWithTools_AtVanillaSpeed()
    {
        const string name = "VertexDig";
        var (client, x, z) = await JoinOnArenaAsync(name, (ax, az) =>
        [
            $"setblock {ax + 1} 200 {az} minecraft:dirt",
            $"setblock {ax + 1} 201 {az} minecraft:stone",
            $"setblock {ax + 1} 200 {az + 1} minecraft:stone",
        ]);
        await using var _ = client;
        var data = client.World.Data;

        // Dirt by hand: hardness 0.5, speed 1, no tool needed -> 1/0.5/30 per tick = 15 ticks. It drops dirt.
        Assert.That(client.TicksToBreak(client.World.GetState(x + 1, 200, z)), Is.EqualTo(15));
        var t0 = client.Ticks;
        Assert.That(await client.DigAsync(x + 1, 200, z), "dirt");
        Assert.That(client.Ticks - t0, Is.InRange(15, 25));
        await PickUpAsync(client, "dirt", 1);

        // Stone with a wooden pickaxe: speed 2 -> 2/1.5/30 -> 23 ticks, drops cobblestone.
        await GiveAsync(client, name, "minecraft:wooden_pickaxe", 1, () => client.Inventory.Count("wooden_pickaxe") == 1);
        Assert.That(await client.EquipAsync("wooden_pickaxe"));
        Assert.That(client.TicksToBreak(client.World.GetState(x + 1, 201, z)), Is.EqualTo(23));
        Assert.That(await client.DigAsync(x + 1, 201, z), "stone with pickaxe");
        await PickUpAsync(client, "cobblestone", 1);

        // Stone by hand: needs a tool -> 1/1.5/100 -> 150 ticks and NO drop. The server still accepts the break.
        var empty = Enumerable.Range(0, 9).First(i => client.Inventory[PlayerInventory.HotbarStart + i].IsEmpty);
        await client.SelectHotbarAsync(empty);
        Assert.That(client.Inventory.MainHand.IsEmpty);
        var stone = client.World.GetState(x + 1, 200, z + 1);
        Assert.That(client.TicksToBreak(stone), Is.EqualTo(150));
        Assert.That(client.CanHarvest(stone), Is.False);
        Assert.That(await client.DigAsync(x + 1, 200, z + 1), "stone by hand");
        await client.WaitTicksAsync(20);
        Assert.That(client.Inventory.Count("cobblestone"), Is.EqualTo(1), "no drop without a pickaxe");
        Assert.That(data.BlockOf(client.World.GetState(x + 1, 200, z + 1)).Name, Is.EqualTo("minecraft:air"));
    }

    [Test]
    public async Task Place_Eat_AndEquip()
    {
        const string name = "VertexPlace";
        var (client, x, z) = await JoinOnArenaAsync(name, (_, _) => []);
        await using var _ = client;

        // Place oak planks in front of us (against the floor), then one on top of it.
        await GiveAsync(client, name, "minecraft:oak_planks", 5, () => client.Inventory.Count("oak_planks") == 5);
        Assert.That(await client.EquipAsync("oak_planks"));
        Assert.That(await client.PlaceBlockAsync(x + 2, 200, z), "on the floor");
        Assert.That(await client.PlaceBlockAsync(x + 2, 201, z), "on the placed block");
        Assert.That(client.World.GetBlock(x + 2, 201, z)?.Name, Is.EqualTo("minecraft:oak_planks"));
        await Wait.UntilAsync(() => client.Inventory.Count("oak_planks") == 3, Timeout, () => client.Inventory.ToString());
        // Cannot place into our own body.
        Assert.That(await client.PlaceBlockAsync(x, 200, z), Is.False);

        // Get hungry, then eat bread.
        await _server.CommandAsync($"effect give {name} minecraft:hunger 3 255");
        await Wait.UntilAsync(() => client.Food < 15, Timeout, () => $"food {client.Food}");
        await _server.CommandAsync($"effect clear {name}");
        await GiveAsync(client, name, "minecraft:bread", 3, () => client.Inventory.Count("bread") == 3);
        Assert.That(await client.EquipAsync("bread"));
        var food = client.Food;
        Assert.That(await client.EatAsync(), "ate");
        Assert.That(client.Food, Is.EqualTo(Math.Min(20, food + 5)), "bread is 5 food");
        await Wait.UntilAsync(() => client.Inventory.Count("bread") == 2, Timeout, () => client.Inventory.ToString());
    }

    [Test]
    public async Task Attack_DamagesAMob()
    {
        const string name = "VertexFight";
        var (client, x, z) = await JoinOnArenaAsync(name, (ax, az) => [$"summon minecraft:zombie {ax + 2} 200 {az} {{NoAI:1b,Tags:[\"vfight\"],active_effects:[{{id:\"minecraft:fire_resistance\",duration:1000000}}]}}"]);
        await using var _ = client;
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:zombie" && e.Health != null), Timeout,
            () => string.Join(", ", client.Entities.Values));
        var zombie = client.Entities.Values.First(e => e.Name == "minecraft:zombie");
        Assert.That(zombie.Health, Is.EqualTo(20f));

        await GiveAsync(client, name, "minecraft:stone_sword", 1, () => client.Inventory.Count("stone_sword") == 1);
        Assert.That(await client.EquipAsync("stone_sword"));
        // Not synced by the server: computed from the sword's modifiers like the vanilla client does.
        Assert.That(client.Attribute("attack_damage"), Is.EqualTo(5.0), "1 base + 4 stone sword");
        Assert.That(client.Attribute("attack_speed"), Is.EqualTo(1.6).Within(1e-6), "4 base - 2.4 sword");
        Assert.That(client.AttackCooldownTicks, Is.EqualTo(13), "20 / 1.6, rounded up");
        Assert.That(client.AttackReady, Is.False, "just changed weapon");
        Assert.That(await client.WaitUntilAsync(() => client.AttackReady, 40), "charged");
        await client.AttackAsync(zombie);
        // 5 damage against a zombie's 2 armor: armor counts clamp(2 - 5/2, 2*0.2, 20) = 0.4 -> 5 * (1 - 0.4/25) = 4.92.
        await Wait.UntilAsync(() => zombie.Health < 20f, Timeout, () => $"zombie health {zombie.Health}");
        Assert.That(zombie.Health, Is.EqualTo(20f - 4.92f).Within(0.01f));
        await _server.CommandAsync("kill @e[tag=vfight]");
    }

    [Test]
    public async Task Guard_FightsMonstersNearThePlayer_AndOnlyThose()
    {
        const string name = "VertexGuard", ward = "VertexWard";
        string Zombie(double x, double z) =>
            $"summon minecraft:zombie {x} 200 {z} {{NoAI:1b,Tags:[\"vguard\"],active_effects:[{{id:\"minecraft:fire_resistance\",duration:1000000}}]}}";
        // The ward stands 4 blocks from Vertex; two zombies 3 blocks either side of them, and one 12.5 blocks away
        // (on its own block, outside the arena): within Vertex's attack range, but not near the ward.
        var (client, x, z) = await JoinOnArenaAsync(name, (ax, az) =>
            [$"setblock {ax - 12} 199 {az + 4} minecraft:stone", Zombie(ax + 3.5, az + 4.5), Zombie(ax - 2.5, az + 4.5), Zombie(ax - 11.5, az + 4.5)]);
        await using var _ = client;
        await using var aubs = new MinecraftClient(GameProfile.Offline(ward));
        using (var cts = new CancellationTokenSource(Timeout)) await aubs.ConnectAsync("127.0.0.1", Port, cts.Token);
        await _server.CommandAsync($"tp {ward} {x + 0.5} 200 {z + 4.5}");
        await GiveAsync(client, name, "minecraft:stone_sword", 1, () => client.Inventory.Count("stone_sword") == 1);
        await Wait.UntilAsync(() => client.Entities.Values.Count(e => e.Name == "minecraft:zombie") == 3 && client.Entities.Values.Any(e => e.PlayerName == ward),
            Timeout, () => string.Join(", ", client.Entities.Values));
        var far = client.Entities.Values.Single(e => e.Name == "minecraft:zombie" && e.X < x - 10).Id;

        var skills = new Vertex.Brain.Skills(client);
        using var stop = new CancellationTokenSource();
        var guarding = skills.GuardAsync("vertexward", stop.Token); // fuzzy name, like speech
        await Wait.UntilAsync(() => client.Entities.Values.Count(e => e.Name == "minecraft:zombie") == 1 || guarding.IsCompleted, TimeSpan.FromSeconds(60),
            () => string.Join(", ", client.Entities.Values.Where(e => e.Name == "minecraft:zombie").Select(e => $"zombie {e.Health} at {e.X:0.0},{e.Z:0.0}")));
        Assert.That(guarding.IsCompleted, Is.False, guarding.IsCompleted ? guarding.Result.Item2 : "");
        await client.WaitTicksAsync(40);
        Assert.That(client.Entities.ContainsKey(far), "left the zombie that is not near the ward alone");
        var (px, py, pz) = client.Position;
        Assert.That(client.Entities.Values.Single(e => e.PlayerName == ward).DistanceTo(px, py, pz), Is.LessThan(5), "back with the ward");
        // Guarding runs until stopped; stopping cancels it like every other task (TaskRunner expects the throw).
        stop.Cancel();
        Assert.That(async () => await guarding, Throws.InstanceOf<OperationCanceledException>());
        await _server.CommandAsync("kill @e[tag=vguard]");
    }

    [Test]
    public async Task Chest_OpenStoreAndClose()
    {
        const string name = "VertexChest";
        var (client, x, z) = await JoinOnArenaAsync(name, (ax, az) => [$"setblock {ax + 2} 200 {az} minecraft:chest[facing=west]"]);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:diamond", 7, () => client.Inventory.Count("diamond") == 7);

        ContainerWindow? opened = null;
        client.WindowOpened += w => opened = w;
        await client.UseOnBlockAsync(x + 2, 200, z, BlockFace.West);
        await Wait.UntilAsync(() => opened != null && client.OpenWindow?.Slots.Length == 63, Timeout, () => $"window {client.OpenWindow}");
        Assert.That(client.OpenWindow!.ContainerSlotCount, Is.EqualTo(27));

        // Shift-click the diamonds from our inventory part of the window into the chest.
        var window = client.OpenWindow;
        var from = Array.FindIndex(window.Slots, 27, s => s.Name == "minecraft:diamond");
        Assert.That(from, Is.GreaterThanOrEqualTo(27));
        await client.ClickAsync(window.Id, from, 0, 1);
        await Wait.UntilAsync(() => client.OpenWindow!.Slots.Take(27).Any(s => s.Name == "minecraft:diamond" && s.Count == 7), Timeout,
            () => string.Join(",", client.OpenWindow!.Slots.Take(27)));
        Assert.That(client.Inventory.Count("diamond"), Is.EqualTo(0));
        await client.CloseWindowAsync();

        // The server's own view of the chest.
        var line = _server.WaitForLineAsync(l => l.Contains("has the following block data"), Timeout);
        await _server.CommandAsync($"data get block {x + 2} 200 {z} Items");
        Assert.That(await line, Does.Contain("minecraft:diamond").And.Contain("count: 7"));
    }
}
