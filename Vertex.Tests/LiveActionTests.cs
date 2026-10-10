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
    public async Task Storage_WalksToAChest_StoresAndTakes()
    {
        const string name = "VertexStore";
        var (client, x, z) = await JoinOnArenaAsync(name, (ax, az) => [$"setblock {ax + 5} 200 {az + 3} minecraft:chest[facing=west]"]);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:diamond", 7, () => client.Inventory.Count("diamond") == 7);
        await GiveAsync(client, name, "minecraft:dirt", 20, () => client.Inventory.Count("dirt") == 20);

        Assert.That(client.FindStorage(), Does.Contain((x + 5, 200, z + 3))); // other tests' chests may be around too
        Assert.That(client.DistanceToBlock(x + 5, 200, z + 3), Is.GreaterThan(client.BlockReach), "has to walk there");
        Assert.That(await client.OpenContainerAsync(x + 5, 200, z + 3), $"open: {client.OpenWindow}");
        Assert.That(await client.StoreAsync(s => s.Name == "minecraft:dirt"), Is.EqualTo(20));
        Assert.That(client.Inventory.Count("dirt"), Is.Zero);
        Assert.That(client.Inventory.Count("diamond"), Is.EqualTo(7), "kept what it was not told to store");
        await client.CloseWindowAsync();

        var line = _server.WaitForLineAsync(l => l.Contains("has the following block data"), Timeout);
        await _server.CommandAsync($"data get block {x + 5} 200 {z + 3} Items");
        Assert.That(await line, Does.Contain("minecraft:dirt").And.Contain("count: 20"));

        Assert.That(await client.OpenContainerAsync(x + 5, 200, z + 3));
        Assert.That(await client.TakeAsync(s => s.Name == "minecraft:dirt", 5), Is.EqualTo(20), "whole stacks come back");
        Assert.That(client.Inventory.Count("dirt"), Is.EqualTo(20));
        await client.CloseWindowAsync();
    }

    [Test]
    public async Task Skills_StoreEverything_TakeBack_AndSleepTillMorning()
    {
        const string name = "VertexHome";
        var (client, x, z) = await JoinOnArenaAsync(name, (ax, az) =>
        [
            $"setblock {ax - 4} 200 {az - 3} minecraft:barrel[facing=up]",
            $"setblock {ax + 3} 200 {az + 2} minecraft:white_bed[facing=east,part=foot]", $"setblock {ax + 4} 200 {az + 2} minecraft:white_bed[facing=east,part=head]",
        ]);
        await using var _ = client;
        var skills = new Vertex.Brain.Skills(client);
        await GiveAsync(client, name, "minecraft:stone_pickaxe", 1, () => client.Inventory.Count("stone_pickaxe") == 1);
        await GiveAsync(client, name, "minecraft:bread", 3, () => client.Inventory.Count("bread") == 3);
        await GiveAsync(client, name, "minecraft:dirt", 20, () => client.Inventory.Count("dirt") == 20);
        await GiveAsync(client, name, "minecraft:cobblestone", 10, () => client.Inventory.Count("cobblestone") == 10);

        var (stored, message) = await skills.StoreAsync("everything", CancellationToken.None);
        TestContext.Out.WriteLine(message);
        Assert.That(stored, message);
        Assert.That(message, Does.Contain("stored 30 items"));
        Assert.That((client.Inventory.Count("stone_pickaxe"), client.Inventory.Count("bread")), Is.EqualTo((1, 3)), "kept the tool and the food");

        var (took, message2) = await skills.TakeAsync("cobblestones", 5, CancellationToken.None); // spoken plural
        TestContext.Out.WriteLine(message2);
        Assert.That(took, message2);
        Assert.That(client.Inventory.Count("cobblestone"), Is.EqualTo(10));
        Assert.That(client.Inventory.Count("dirt"), Is.Zero, "took only what was asked for");

        try
        {
            await _server.CommandAsync("time set 18000");
            await Wait.UntilAsync(() => client.World.DayTime % 24000 >= 13000, Timeout, () => $"time {client.World.DayTime}");
            await client.WaitTicksAsync(5); // beds read Level.skyDarken, which the server updates on its next tick
            var (slept, message3) = await skills.SleepAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(slept, message3);
            Assert.That(message3, Does.Contain("morning"));
        }
        finally
        {
            await _server.CommandAsync("time set day");
        }
    }

    [Test]
    public async Task Bed_RefusedByDay_SleepsAtNight_AndWakesInTheMorning()
    {
        const string name = "VertexSleep";
        var (client, x, z) = await JoinOnArenaAsync(name, (ax, az) =>
            [$"setblock {ax + 3} 200 {az + 2} minecraft:red_bed[facing=east,part=foot]", $"setblock {ax + 4} 200 {az + 2} minecraft:red_bed[facing=east,part=head]"]);
        await using var _ = client;
        try
        {
            var bed = client.FindBeds()[0];
            Assert.That(bed.Y, Is.EqualTo(200));
            await _server.CommandAsync("time set day");
            var refusal = await client.LieDownAsync(bed.X, bed.Y, bed.Z);
            Assert.That(refusal, Does.Contain("night"), "the server's own reason");
            Assert.That(client.Sleeping, Is.False);

            await _server.CommandAsync("time set 18000"); // midnight
            await Wait.UntilAsync(() => client.World.DayTime % 24000 >= 13000, Timeout, () => $"time {client.World.DayTime}");
            await client.WaitTicksAsync(5); // beds read Level.skyDarken, which the server updates on its next tick
            Assert.That(await client.LieDownAsync(bed.X, bed.Y, bed.Z), Is.Null);
            Assert.That(client.Pose, Is.EqualTo(SpawnDev.Minecraft.Entities.EntityPose.Sleeping));
            var teleports = client.TeleportCount;
            // We keep ticking and reporting our (still) position like the vanilla client; Paper ignores small moves
            // from a sleeper and must never have to correct us.
            await client.WaitTicksAsync(60);
            Assert.That(client.TeleportCount, Is.EqualTo(teleports), "lying still: the server never had to put us back");

            await client.LeaveBedAsync();
            Assert.That(client.Sleeping, Is.False, "got up");
            await client.WaitTicksAsync(10);
            Assert.That(client.OnGround);

            // Back to bed; alone on the server, the night passes after 100 ticks asleep and the server wakes us.
            Assert.That(await client.LieDownAsync(bed.X, bed.Y, bed.Z), Is.Null);
            await Wait.UntilAsync(() => !client.Sleeping, TimeSpan.FromSeconds(20), () => $"pose {client.Pose}, time {client.World.DayTime % 24000}");
            // The server sends the clock once a second (MinecraftServer.synchronizeTime every 20 ticks).
            await Wait.UntilAsync(() => client.World.DayTime % 24000 < 1000, TimeSpan.FromSeconds(3), () => $"still night: {client.World.DayTime % 24000}");
            await client.WaitTicksAsync(20);
            Assert.That(client.OnGround, $"standing beside the bed at {client.Position}");
        }
        finally
        {
            await _server.CommandAsync("time set day");
        }
    }

    [Test]
    public async Task Reflex_RunsFromALitCreeper_ThenGoesBackToItsJob()
    {
        const string name = "VertexDodge";
        // A 41x41 floor to run on; explosions must not eat it if the reflex fails.
        await _server.CommandAsync("gamerule mobGriefing false");
        var (client, x, z) = await JoinOnArenaAsync(name, (ax, az) =>
            [$"fill {ax - 20} 200 {az - 20} {ax + 20} 203 {az + 20} minecraft:air", $"fill {ax - 20} 199 {az - 20} {ax + 20} 199 {az + 20} minecraft:stone"]);
        await using var _ = client;
        await Wait.UntilAsync(() => client.World.IsLoaded(x - 20, z - 20) && client.World.IsLoaded(x + 20, z + 20)
            && client.World.IsLoaded(x - 20, z + 20) && client.World.IsLoaded(x + 20, z - 20), Timeout, () => "floor chunks");
        var skills = new Vertex.Brain.Skills(client);
        var tasks = new Vertex.Brain.TaskRunner();
        var ended = new List<Vertex.Brain.TaskOutcome>();
        tasks.Finished += o => { lock (ended) ended.Add(o); };
        using var reflexes = new Vertex.Brain.Reflexes(client, skills, tasks, () => new Vertex.Brain.PlayStyle());
        tasks.Start("standing around", async ct => { await Task.Delay(System.Threading.Timeout.Infinite, ct); return (true, ""); });

        await _server.CommandAsync($"summon minecraft:creeper {x + 4.5} 200 {z + 0.5} {{Tags:[\"vdodge\"],active_effects:[{{id:\"minecraft:fire_resistance\",duration:1000000}}]}}");
        try
        {
            await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:creeper"), Timeout, () => "creeper");
            var creeper = client.Entities.Values.First(e => e.Name == "minecraft:creeper");
            // It walks up, its fuse starts; Vertex must be gone before the 30-tick fuse ends.
            await Wait.UntilAsync(() => ended.Count > 0 || !client.Entities.ContainsKey(creeper.Id), TimeSpan.FromSeconds(30),
                () => $"creeper at {creeper.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z):0.0}, fuse {creeper.Metadata.GetValueOrDefault(16)}, task {tasks.Current}");
            Assert.That(client.Entities.ContainsKey(creeper.Id), "the creeper did not explode");
            Assert.That(client.Health, Is.EqualTo(20f), "not hurt");
            lock (ended) Assert.That(ended[0].Success, ended[0].Summary);
            var (px, _, pz) = client.Position;
            Assert.That(Math.Sqrt((px - creeper.X) * (px - creeper.X) + (pz - creeper.Z) * (pz - creeper.Z)), Is.GreaterThanOrEqualTo(7.5), "out of its reach");
            await Wait.UntilAsync(() => tasks.Current == "standing around", TimeSpan.FromSeconds(2), () => $"task {tasks.Current}");
        }
        finally
        {
            reflexes.Enabled = false;
            tasks.Stop();
            await _server.CommandAsync("kill @e[tag=vdodge]");
        }
    }

    [Test]
    public async Task Armor_WearsTheBestItCarries()
    {
        const string name = "VertexArmor";
        var (client, x, z) = await JoinOnArenaAsync(name, (_, _) => []);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:leather_helmet", 1, () => client.Inventory.Count("leather_helmet") == 1);
        await GiveAsync(client, name, "minecraft:iron_chestplate", 1, () => client.Inventory.Count("iron_chestplate") == 1);
        Assert.That(client.HasBetterArmor);
        Assert.That(await client.EquipBestArmorAsync(), Is.EqualTo(2));
        Assert.That((client.Inventory.Head.Name, client.Inventory.Chest.Name), Is.EqualTo(("minecraft:leather_helmet", "minecraft:iron_chestplate")));
        Assert.That(client.HasBetterArmor, Is.False);

        // A better helmet replaces the leather one, which goes back into the inventory.
        await GiveAsync(client, name, "minecraft:diamond_helmet", 1, () => client.Inventory.Count("diamond_helmet") == 1);
        Assert.That(await client.EquipBestArmorAsync(), Is.EqualTo(1));
        Assert.That(client.Inventory.Head.Name, Is.EqualTo("minecraft:diamond_helmet"));
        Assert.That(client.Inventory.Find(s => s.Name == "minecraft:leather_helmet").Any(), "the old helmet is kept");
        // 3 (diamond helmet) + 6 (iron chestplate), and the server agrees about the head.
        // (The server syncs the attribute on its next tick, after the slot update.)
        await Wait.UntilAsync(() => client.Attribute("armor") == 9, Timeout, () => $"armor {client.Attribute("armor")}");
        var line = _server.WaitForLineAsync(l => l.Contains("has the following entity data"), Timeout);
        await _server.CommandAsync($"data get entity {name} equipment.head.id");
        Assert.That(await line, Does.Contain("minecraft:diamond_helmet"));
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
