using SpawnDev.Minecraft;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// M8: AubsCraft's modded Fabric server ("spooky": Fabric API + Halloween content mods), run locally. A plain client
/// is refused there ("This server requires Fabric Loader and Fabric API installed on your client!"); Vertex must
/// answer Fabric's registry sync and then cope with modded blocks, items and mobs it has no data for.
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

        // Modded blocks we have no data for: a wall of one right in front of us must read as the solid unknown
        // state, and walking into it must stop us (not crash physics).
        var data = client.World.Data;
        var modded = client.FabricRegistries["minecraft:block"].Keys.First(n => data.Block(n) == null && !n.Contains("door") && !n.Contains("sign"));
        var feet = client.FeetBlock;
        await _server!.CommandAsync($"fill {feet.X + 2} {feet.Y} {feet.Z - 1} {feet.X + 2} {feet.Y + 1} {feet.Z + 1} {modded}");
        await Wait.UntilAsync(() => client.World.GetState(feet.X + 2, feet.Y, feet.Z) == data.UnknownState, TimeSpan.FromSeconds(10),
            () => $"{modded} shows as {client.World.GetBlock(feet.X + 2, feet.Y, feet.Z)}");
        client.Look(-90, 0); // east, +X
        client.Controls.Forward = true;
        await client.WaitTicksAsync(30);
        client.Controls.Clear();
        Assert.That(client.Position.X, Is.LessThan(feet.X + 2 - 0.29), $"stopped by the {modded} wall");

        // A modded mob next to us: its spawn and entity data must not break the connection.
        var mob = client.FabricRegistries["minecraft:entity_type"].Keys.FirstOrDefault(n => n == "mutantmonsters:mutant_zombie")
            ?? client.FabricRegistries["minecraft:entity_type"].Keys.First(n => data.Entity(n) == null);
        channels.Clear();
        await _server.CommandAsync($"summon {mob} {feet.X - 3} {feet.Y} {feet.Z} {{NoAI:1b}}");
        await client.WaitTicksAsync(20);
        TestContext.Out.WriteLine("plugin channels after the summon: " + string.Join(", ", channels.Keys));
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == mob), TimeSpan.FromSeconds(15),
            () => string.Join(", ", client.Entities.Values.Select(e => e.Name).Distinct()));
        await client.WaitTicksAsync(40);
        Assert.That(disconnect, Is.Null, disconnect);
        TestContext.Out.WriteLine($"modded block {modded}, modded mob {mob}; skipped packets: {client.PacketErrors}");
    }
}
