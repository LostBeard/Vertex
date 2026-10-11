using System.IO.Compression;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Data;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// M8: AubsCraft's modded Fabric server ("spooky": Fabric API + Halloween content mods), run locally. A plain client
/// is refused there ("This server requires Fabric Loader and Fabric API installed on your client!"); Vertex must
/// answer Fabric's registry sync, know the modded blocks (tools/ModDump), and cope with modded items and mobs.
/// </summary>
[NonParallelizable]
public class LiveFabricTests
{
    private const int Port = 25940;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private PaperServer? _server;

    [OneTimeSetUp]
    public async Task Start()
    {
        if (!SpookyServer.Available) Assert.Ignore("the AubsCraft tests have not installed the spooky server here: " + SpookyServer.Template);
        _server = await SpookyServer.StartAsync("spooky", Port, new Dictionary<string, string> { ["difficulty"] = "normal" });
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    /// <summary>
    /// Learning a server's blocks the way a player gets them: from its modpack. A copy of the real AubsCraft pack
    /// index (the jars come from Modrinth's CDN, never from production) is dumped by the runtime path. Vertex then
    /// joins spooky knowing ONLY what it learned (the built-in catalog off): every block known. Without it, the
    /// check at join names every block it lacks.
    /// </summary>
    [Test]
    public async Task LearnsTheBlocks_FromTheServersModpack()
    {
        var pack = Path.Combine(Path.GetTempPath(), $"vertex-pack-{Guid.NewGuid():N}.mrpack");
        var world = Path.Combine(Path.GetTempPath(), $"vertex-world-{Guid.NewGuid():N}.blocks.json");
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(pack, System.IO.Compression.ZipArchiveMode.Create))
                zip.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, "Live", "Fixtures", "aubscraft-1.21.5.modrinth.index.json"), "modrinth.index.json");
            var log = new List<string>();
            var learner = new Vertex.Mods.ModpackBlocks(PaperServer.CacheRoot, m => { lock (log) log.Add(m); TestContext.Out.WriteLine(m); });
            var learned = await learner.DumpAsync(pack).WaitAsync(TimeSpan.FromMinutes(20));

            // Same jars as the server, so the same facts as the built-in catalog (dumped from the server's own mods).
            foreach (var name in GameData.ModdedCatalog.Keys)
            {
                Assert.That(learned.ContainsKey(name), "the pack did not teach " + name);
                var (a, b) = (learned[name], GameData.ModdedCatalog[name]);
                Assert.That((a.StateCount, a.DefaultIndex, a.Hardness, a.Flags, a.Friction), Is.EqualTo((b.StateCount, b.DefaultIndex, b.Hardness, b.Flags, b.Friction)), name);
                Assert.That(a.Shapes.Zip(b.Shapes).All(z => z.First.SequenceEqual(z.Second)), name + " shapes");
            }
            Vertex.Mods.ModpackBlocks.SaveWorld(world, learned.Values, "test");
            var saved = Vertex.Mods.ModpackBlocks.LoadWorld(world)!;
            Assert.That(saved.Keys, Is.EquivalentTo(learned.Keys), "saved for the world and read back");

            // Joining with nothing: the check at join lists every modded block as unknown.
            await using (var blind = new MinecraftClient(GameProfile.Offline("VertexBlind"), new ClientOptions { UseEmbeddedModdedBlocks = false }))
            {
                using var cts = new CancellationTokenSource(Timeout);
                await blind.ConnectAsync("127.0.0.1", Port, cts.Token);
                var modded = blind.FabricRegistries["minecraft:block"].Keys.Where(n => GameData.Vanilla.Block(n) == null).ToList();
                Assert.That(blind.UnknownModdedBlocks, Is.EquivalentTo(modded));
                Assert.That(blind.World.Data.ModdedBlockCount, Is.Zero);
            }
            // Joining with what it learned: all known.
            await using var client = new MinecraftClient(GameProfile.Offline("VertexLearned"), new ClientOptions { UseEmbeddedModdedBlocks = false, ModdedBlocks = saved });
            using (var cts = new CancellationTokenSource(Timeout)) await client.ConnectAsync("127.0.0.1", Port, cts.Token);
            Assert.That(client.UnknownModdedBlocks, Is.Empty);
            Assert.That(client.World.Data.ModdedBlockCount, Is.EqualTo(client.FabricRegistries["minecraft:block"].Count - (GameData.Vanilla.Blocks.Length - 1)));
        }
        finally
        {
            File.Delete(pack);
            File.Delete(world);
        }
    }

    /// <summary>
    /// Learning blocks by touch, with nothing to go on (built-in catalog off, no modpack): Vertex steps toward each
    /// unknown block from every open side and over it, the server accepting or correcting each step. The shapes it
    /// learns must match the real ones the mods define (our catalog is the answer key): a doormat 1/16 high, a
    /// plate along one wall, a full cube, a small pumpkin's bounding box, and a cobweb that holds you. Then the path
    /// over the doormat, under a low ceiling, which a solid unknown cube blocks, is open.
    /// </summary>
    [Test]
    public async Task LearnsBlockShapes_ByTouch()
    {
        await using var client = new MinecraftClient(GameProfile.Offline("VertexToucher"), new ClientOptions
        {
            UseEmbeddedModdedBlocks = false,
            Log = m => TestContext.Out.WriteLine(m),
        });
        using (var cts = new CancellationTokenSource(Timeout)) await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var feet = client.FeetBlock;
        int x0 = feet.X, y = feet.Y, z0 = feet.Z;
        // A stone floor with open air above; the test blocks in a row along +x, 3 apart, Vertex free to walk round each.
        await _server!.CommandAsync($"fill {x0 - 3} {y} {z0 - 4} {x0 + 18} {y + 4} {z0 + 4} minecraft:air");
        await _server.CommandAsync($"fill {x0 - 3} {y - 1} {z0 - 4} {x0 + 18} {y - 1} {z0 + 4} minecraft:stone");
        string[] blocks = ["mcwholidays:bat_doormat", "mcwholidays:bat_wall_deco_1", "mcwholidays:blue_candy_cane_block", "mcwholidays:small_pumpkin", "mcwholidays:hanging_cobweb"];
        for (var i = 0; i < blocks.Length; i++) await _server.CommandAsync($"setblock {x0 + 3 + 3 * i} {y} {z0} {blocks[i]}");
        await Wait.UntilAsync(() => client.World.GetRawState(x0 + 3 + 3 * (blocks.Length - 1), y, z0) >= client.World.Data.KnownStateCount, TimeSpan.FromSeconds(10), () => "blocks placed");
        var teleportsBefore = client.TeleportCount;
        var unlearned = client.World.Data;

        for (var i = 0; i < blocks.Length; i++)
        {
            var bx = x0 + 3 + 3 * i;
            Assert.That(client.World.Data.IsUnknownBlock(client.World.GetState(bx, y, z0)), blocks[i] + " starts unknown");
            var probe = await client.MeasureBlockAsync(bx, y, z0).WaitAsync(TimeSpan.FromMinutes(2));
            Assert.That(probe, Is.Not.Null, blocks[i]);
            client.Learn(probe!);
            var truth = GameData.ModdedCatalog[blocks[i]];
            var real = truth.Shapes[truth.DefaultIndex];
            TestContext.Out.WriteLine($"{blocks[i]}: learned {string.Join(" ", probe!.Shape)} sticky={probe.Sticky} ({probe.FacesMeasured} faces); real {string.Join(" ", real)}");
            if (real.Length == 0)
            {
                Assert.That(probe.Shape, Is.Empty, blocks[i]);
                Assert.That(probe.Sticky, Is.EqualTo(blocks[i].Contains("cobweb")), blocks[i] + " holds you");
                continue;
            }
            // The bounding box of the real shape, to within the probe's resolution.
            var bounds = new Box(real.Min(b => b.MinX), real.Min(b => b.MinY), real.Min(b => b.MinZ), real.Max(b => b.MaxX), real.Max(b => b.MaxY), real.Max(b => b.MaxZ));
            var got = probe.Shape.Single();
            Assert.That(new[] { got.MinX, got.MinY, got.MinZ, got.MaxX, got.MaxY, got.MaxZ },
                Is.EqualTo(new[] { bounds.MinX, bounds.MinY, bounds.MinZ, bounds.MaxX, bounds.MaxY, bounds.MaxZ }).Within(1.0 / 32), blocks[i]);
            Assert.That(probe.Sticky, Is.False, blocks[i]);
        }
        PhysicsCheck(client);

        // A corridor with a ceiling 2 above the floor and the doormat in it: as an unknown cube it is a 1-block step
        // with no headroom (no path); learned, it is a floor you walk over. Entered from its open west end.
        var mat = client.World.GetRawState(x0 + 3, y, z0);
        await _server.CommandAsync($"fill {x0 + 1} {y + 2} {z0 - 1} {x0 + 5} {y + 2} {z0 + 1} minecraft:stone");
        await _server.CommandAsync($"fill {x0 + 1} {y} {z0 - 1} {x0 + 5} {y + 1} {z0 - 1} minecraft:stone");
        await _server.CommandAsync($"fill {x0 + 1} {y} {z0 + 1} {x0 + 5} {y + 1} {z0 + 1} minecraft:stone");
        await Wait.UntilAsync(() => client.World.GetState(x0 + 5, y + 2, z0) > 0, TimeSpan.FromSeconds(10), () => "corridor built");
        Assert.That(await client.GoToAsync(SpawnDev.Minecraft.Pathing.Goal.Block(x0, y, z0), new SpawnDev.Minecraft.Pathing.PathOptions()), $"to the corridor; at {client.Position}");
        var goal = SpawnDev.Minecraft.Pathing.Goal.Block(x0 + 4, y, z0);
        Assert.That(new SpawnDev.Minecraft.Pathing.Pathfinder(client.World, unlearned).FindPath(client.FeetBlock, goal, new SpawnDev.Minecraft.Pathing.PathOptions()).Complete,
            Is.False, "before learning, the doormat is a solid cube under a low ceiling");
        Assert.That(new SpawnDev.Minecraft.Pathing.Pathfinder(client.World).FindPath(client.FeetBlock, goal, new SpawnDev.Minecraft.Pathing.PathOptions()).Complete, "over the learned doormat");
        Assert.That(await client.GoToAsync(goal, new SpawnDev.Minecraft.Pathing.PathOptions()), $"walked it; at {client.Position}");
        Assert.That(client.World.GetRawState(x0 + 3, y, z0), Is.EqualTo(mat), "and did not break it");
        TestContext.Out.WriteLine($"server corrections while probing and walking: {client.TeleportCount - teleportsBefore}");
    }

    /// <summary>
    /// The skill: Vertex studies every kind of unknown block around (one of each), saves what it learned with the
    /// server's block-list fingerprint, and on the next visit starts out knowing them.
    /// </summary>
    [Test]
    public async Task StudiesTheUnknownBlocksAround_AndRemembersThemNextVisit()
    {
        var file = Path.Combine(Path.GetTempPath(), $"vertex-touched-{Guid.NewGuid():N}.json");
        try
        {
            int x0, y, z0;
            HashSet<int> kinds;
            await using (var client = new MinecraftClient(GameProfile.Offline("VertexStudent"), new ClientOptions { UseEmbeddedModdedBlocks = false }))
            {
                using (var cts = new CancellationTokenSource(Timeout)) await client.ConnectAsync("127.0.0.1", Port, cts.Token);
                (x0, y, z0) = client.FeetBlock;
                await _server!.CommandAsync($"fill {x0 - 6} {y} {z0 - 6} {x0 + 6} {y + 4} {z0 + 6} minecraft:air");
                await _server!.CommandAsync($"fill {x0 - 6} {y - 1} {z0 - 6} {x0 + 6} {y - 1} {z0 + 6} minecraft:stone");
                // Two of the same kind (studied once) and two other kinds, spread out.
                await _server!.CommandAsync($"setblock {x0 + 3} {y} {z0} mcwholidays:bat_doormat");
                await _server!.CommandAsync($"setblock {x0 - 3} {y} {z0} mcwholidays:bat_doormat");
                await _server!.CommandAsync($"setblock {x0} {y} {z0 + 3} mcwholidays:blue_candy_cane_block");
                await _server!.CommandAsync($"setblock {x0} {y} {z0 - 3} mcwholidays:hanging_cobweb");
                await Wait.UntilAsync(() => client.World.GetRawState(x0, y, z0 - 3) >= client.World.Data.KnownStateCount, TimeSpan.FromSeconds(10), () => "placed");
                var skills = new Vertex.Brain.Skills(client);
                kinds = skills.UnstudiedBlocks(16).Select(b => b.Raw).ToHashSet();
                Assert.That(kinds, Has.Count.EqualTo(3), "three kinds to study");

                var (ok, message) = await skills.StudyBlocksAsync(16, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(3));
                TestContext.Out.WriteLine(message);
                Assert.That(ok, message);
                Assert.That(message, Does.Contain("studied 3 kinds").And.Contain("1 of them would trap me"));
                Assert.That(client.LearnedShapes.Keys, Is.EquivalentTo(kinds));
                Assert.That(skills.UnstudiedBlocks(16), Is.Empty, "nothing left to study");
                Vertex.Mods.TouchedBlocks.Save(file, new Vertex.Mods.TouchedBlocks(client.BlockRegistryFingerprint!, client.LearnedShapes.ToDictionary(), client.StickyStates.ToList()));
            }

            // Next visit: it starts out knowing them.
            var touched = Vertex.Mods.TouchedBlocks.Load(file)!;
            await using var again = new MinecraftClient(GameProfile.Offline("VertexStudent"), new ClientOptions
            {
                UseEmbeddedModdedBlocks = false, LearnedShapes = touched.Shapes, LearnedSticky = touched.Sticky, LearnedFingerprint = touched.Fingerprint,
            });
            using (var cts = new CancellationTokenSource(Timeout)) await again.ConnectAsync("127.0.0.1", Port, cts.Token);
            await Wait.UntilAsync(() => again.World.GetRawState(x0, y, z0 - 3) >= again.World.Data.KnownStateCount, TimeSpan.FromSeconds(10), () => "chunks arrived");
            Assert.That(again.LearnedShapes.Keys, Is.EquivalentTo(kinds));
            Assert.That(again.World.Data.LearnedStateCount, Is.EqualTo(3));
            var mat = again.World.GetState(x0 + 3, y, z0);
            Assert.That(again.World.Data.CollisionBoxes(mat).Single().MaxY, Is.EqualTo(0.0625).Within(1.0 / 64));
            var web = again.World.GetState(x0, y, z0 - 3);
            Assert.That(again.World.Data.StateFlags[web] & GameData.FlagSticky, Is.Not.Zero, "still knows the cobweb traps");
            Assert.That(new Vertex.Brain.Skills(again).UnstudiedBlocks(16), Is.Empty);
        }
        finally { File.Delete(file); }
    }

    private static void PhysicsCheck(MinecraftClient client) =>
        Assert.That(client.PhysicsEnabled, "physics back on after probing");

    [Test]
    public async Task JoinsTheModdedServer_AndStaysOnline()
    {
        var log = new List<string>();
        await using var client = new MinecraftClient(GameProfile.Offline("VertexSpooky"), new ClientOptions { Log = m => { lock (log) log.Add(m); TestContext.Out.WriteLine(m); } });
        string? disconnect = null;
        client.Disconnected += r => disconnect = r;
        var channels = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        client.PluginMessage += (ch, data) =>
        {
            channels.AddOrUpdate($"{ch} ({data.Length} bytes)", 1, (_, n) => n + 1);
        };
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        }
        catch (Exception e)
        {
            Assert.Fail($"join failed: {e.Message}\n{string.Join('\n', _server!.OutputTail(15))}");
        }
        await Wait.UntilAsync(() => client.World.LoadedChunks >= 60 || disconnect != null, Timeout, () => $"{client.World.LoadedChunks} chunks");
        await client.WaitTicksAsync(200); // modded mobs walk around, chunks keep streaming
        Assert.That(disconnect, Is.Null, disconnect);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Play));
        TestContext.Out.WriteLine($"{client.World.LoadedChunks} chunks, {client.Entities.Count} entities: " +
            string.Join(", ", client.Entities.Values.GroupBy(e => e.Name).Select(g => $"{g.Count()} {g.Key}")));
        Assert.That(client.IsModded);
        var fabricEntities = client.FabricRegistries["minecraft:entity_type"];
        var mismatched = fabricEntities.Where(kv => client.World.Data.Entity(kv.Key) is { } v && v.Id != kv.Value).Select(kv => $"{kv.Key} vanilla {client.World.Data.Entity(kv.Key)!.Id} server {kv.Value}").Take(8).ToList();
        // Fabric appends modded entries: every vanilla entity type keeps its vanilla id (what our tables assume).
        Assert.That(mismatched, Is.Empty, "vanilla entity ids moved on this server");

        // Modded blocks come with their real facts (tools/ModDump): every one this server synced is known, by name,
        // with its own properties and shape, numbered exactly as the server numbers them.
        var data = client.World.Data;
        var moddedNames = client.FabricRegistries["minecraft:block"].Keys.Where(n => GameData.Vanilla.Block(n) == null).ToList();
        Assert.That(data.ModdedBlockCount, Is.EqualTo(moddedNames.Count), "a block the catalog does not have");
        Assert.That(data.KnownStateCount - data.VanillaStateCount, Is.EqualTo(moddedNames.Sum(n => GameData.ModdedCatalog[n].StateCount)));
        var feet = client.FeetBlock;
        await _server!.CommandAsync($"fill {feet.X - 6} {feet.Y} {feet.Z - 6} {feet.X + 6} {feet.Y + 3} {feet.Z + 6} minecraft:air");
        await _server!.CommandAsync($"fill {feet.X - 6} {feet.Y - 1} {feet.Z - 6} {feet.X + 6} {feet.Y - 1} {feet.Z + 6} minecraft:stone");
        await _server!.CommandAsync($"setblock {feet.X + 2} {feet.Y} {feet.Z + 3} mcwholidays:small_pumpkin[facing=west]");
        await Wait.UntilAsync(() => client.World.GetBlock(feet.X + 2, feet.Y, feet.Z + 3)?.Name == "mcwholidays:small_pumpkin", TimeSpan.FromSeconds(10),
            () => $"shows as {client.World.GetBlock(feet.X + 2, feet.Y, feet.Z + 3)}");
        var pumpkin = client.World.GetState(feet.X + 2, feet.Y, feet.Z + 3);
        Assert.That(data.GetProperty(pumpkin, "facing"), Is.EqualTo("west"));
        Assert.That(data.CollisionBoxes(pumpkin), Is.Not.EqualTo(new[] { new Box(0, 0, 0, 1, 1, 1) }), "a small pumpkin, not a full cube");

        // A stone wall across the arena with a closed spooky door in it: Vertex walks through (opens it, shuts it
        // behind), which needs the door's real shape and the server's tags (the mod puts its doors in wooden_doors).
        Assert.That(data.BlockTag("minecraft:wooden_doors"), Does.Contain("spookydoors:spooky_oak_door"));
        await _server!.CommandAsync($"fill {feet.X + 3} {feet.Y} {feet.Z - 6} {feet.X + 3} {feet.Y + 2} {feet.Z + 6} minecraft:stone");
        await _server!.CommandAsync($"setblock {feet.X + 3} {feet.Y} {feet.Z} spookydoors:spooky_oak_door[facing=east,half=lower,open=false]");
        await _server!.CommandAsync($"setblock {feet.X + 3} {feet.Y + 1} {feet.Z} spookydoors:spooky_oak_door[facing=east,half=upper,open=false]");
        await Wait.UntilAsync(() => client.World.GetBlock(feet.X + 3, feet.Y + 1, feet.Z)?.Name == "spookydoors:spooky_oak_door", TimeSpan.FromSeconds(10),
            () => $"shows as {client.World.GetBlock(feet.X + 3, feet.Y, feet.Z)}");
        var door = client.World.GetState(feet.X + 3, feet.Y, feet.Z);
        Assert.That((data.GetProperty(door, "facing"), data.GetProperty(door, "half"), data.GetProperty(door, "open")), Is.EqualTo(("east", "lower", "false")));
        Assert.That(data.CollisionBoxes(door).Single().MaxX - data.CollisionBoxes(door).Single().MinX, Is.EqualTo(3.0 / 16).Within(1e-9), "a door is a thin plate");
        var teleports = client.TeleportCount;
        Assert.That(await client.GoToAsync(SpawnDev.Minecraft.Pathing.Goal.Block(feet.X + 5, feet.Y, feet.Z), new SpawnDev.Minecraft.Pathing.PathOptions { CanDig = true }),
            $"through the door; at {client.Position}");
        await client.WaitTicksAsync(5);
        Assert.That(data.GetProperty(client.World.GetState(feet.X + 3, feet.Y, feet.Z), "open"), Is.EqualTo("false"), "shut it behind");
        Assert.That(client.World.GetBlock(feet.X + 3, feet.Y, feet.Z)?.Name, Is.EqualTo("spookydoors:spooky_oak_door"), "went through it, did not dig it");
        Assert.That(client.TeleportCount, Is.EqualTo(teleports), "the server corrected our movement");

        // A modded mob next to us: its spawn and entity data must not break the connection.
        var mob = client.FabricRegistries["minecraft:entity_type"].Keys.FirstOrDefault(n => n == "mutantmonsters:mutant_zombie")
            ?? client.FabricRegistries["minecraft:entity_type"].Keys.First(n => data.Entity(n) == null);
        channels.Clear();
        await _server!.CommandAsync($"summon {mob} {feet.X - 3} {feet.Y} {feet.Z} {{NoAI:1b}}");
        await client.WaitTicksAsync(20);
        TestContext.Out.WriteLine("plugin channels after the summon: " + string.Join(", ", channels.Keys));
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == mob), TimeSpan.FromSeconds(15),
            () => string.Join(", ", client.Entities.Values.Select(e => e.Name).Distinct()));
        await client.WaitTicksAsync(40);
        Assert.That(disconnect, Is.Null, disconnect);
        TestContext.Out.WriteLine($"modded mob {mob}; skipped packets: {client.PacketErrors}");
    }
}
