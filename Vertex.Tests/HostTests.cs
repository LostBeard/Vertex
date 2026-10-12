using SpawnDev.Minecraft;
using Vertex.Host;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// The dashboard's "Start playing" path end to end on the GPU: VertexHost loads the AI (language model + the fast
/// command matcher), joins a real Paper server, and plays. A second player asks it, in chat, to follow; Vertex must
/// act at once (System One) and answer through the language model. Settings, characters and world memory live in
/// a temp folder, never the user's %APPDATA%.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class HostTests
{
    private const int Port = 25942;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    [Test]
    public async Task Start_Join_FollowOnRequest_Stop()
    {
        await using var server = await PaperServer.StartAsync("host", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await server.CommandAsync("gamerule doMobSpawning false");
        var folder = Path.Combine(Path.GetTempPath(), "vertex-host-" + Guid.NewGuid().ToString("N"));
        var host = new VertexHost(folder);
        try
        {
            host.Settings.Server = "127.0.0.1";
            host.Settings.Port = Port;
            host.Settings.Account = "";            // offline
            host.Settings.OfflineName = "VertexHost";
            host.Settings.Voice = false;           // no Simple Voice Chat on this server
            host.Settings.Minecraft = true;
            host.Settings.Reachy = false;          // the robot is not part of this test
            await host.StartAsync().WaitAsync(Timeout);
            foreach (var l in host.Lines) TestContext.Out.WriteLine($"{l.Kind}: {l.Text}");
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);
            Assert.That(host.InGame, host.Status);
            Assert.That(host.Lines.Any(l => l.Text.Contains("Playing as Vertex (VertexHost)")), "said where it plays");
            Assert.That(File.Exists(Path.Combine(folder, "settings.json")), "settings saved in the host's folder");
            var vertex = host.Client!;

            // Aubri (a second client) stands a few blocks away and asks.
            await using var aubs = new MinecraftClient(GameProfile.Offline("Aubs"));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60))) await aubs.ConnectAsync("127.0.0.1", Port, cts.Token);
            var (vx, vy, vz) = vertex.Position;
            await server.CommandAsync($"tp Aubs {vx + 6} {vy} {vz}");
            await Wait.UntilAsync(() => vertex.Entities.Values.Any(e => e.PlayerName == "Aubs"), TimeSpan.FromSeconds(30), () => "Aubs visible");
            await aubs.SendChatAsync("Vertex, follow me!");

            await Wait.UntilAsync(() => host.Lines.Any(l => l.Kind == LineKind.Said), TimeSpan.FromMinutes(2),
                () => string.Join("\n", host.Lines.TakeLast(10).Select(l => $"{l.Kind}: {l.Text}")));
            var lines = host.Lines;
            foreach (var l in lines) TestContext.Out.WriteLine($"{l.Kind}: {l.Text}");
            Assert.That(lines.Any(l => l.Text.Contains("fast: \"follow me\" = follow_player")), "the fast path recognised the command");
            Assert.That(host.Brain!.Tasks.Current, Does.StartWith("following"));
            var aubsEntity = vertex.Entities.Values.First(e => e.PlayerName == "Aubs");
            await Wait.UntilAsync(() => aubsEntity.DistanceTo(vertex.Position.X, vertex.Position.Y, vertex.Position.Z) < 4, TimeSpan.FromSeconds(30),
                () => $"{aubsEntity.DistanceTo(vertex.Position.X, vertex.Position.Y, vertex.Position.Z):0.0} blocks apart");

            await host.StopAsync();
            Assert.That(host.State, Is.EqualTo(HostState.Idle));
        }
        finally
        {
            await host.DisposeAsync();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    /// <summary>
    /// The dashboard path on a modded server it knows nothing about (built-in catalog off): Vertex notices at join,
    /// learns the blocks from the server's modpack in the background, saves them for that world, and knows every
    /// block from then on, the ones already loaded included, without rejoining. The pack is a copy of the real
    /// AubsCraft index (jars from Modrinth's CDN).
    /// </summary>
    [Test]
    public async Task LearnsAModdedServersBlocks_FromItsModpack_WhilePlaying()
    {
        if (!SpookyServer.Available) Assert.Ignore("no spooky server installed here: " + SpookyServer.Template);
        await using var server = await SpookyServer.StartAsync("host-spooky", Port + 1);
        // A spooky door at the spawn point, there before Vertex arrives: loaded while it knows nothing about it.
        await server.CommandAsync("gamerule spawnRadius 0");
        await server.CommandAsync("setworldspawn 0 120 0");
        await server.CommandAsync("forceload add -16 -16 16 16");
        await server.CommandAsync("fill -4 119 -4 4 119 4 minecraft:stone");
        await server.CommandAsync("fill -4 120 -4 4 124 4 minecraft:air");
        await server.CommandAsync("setblock 2 120 0 spookydoors:spooky_oak_door[half=lower]");
        await server.WaitForLineAsync(l => l.Contains("Changed the block"), TimeSpan.FromSeconds(30), includeHistory: true);
        var folder = Path.Combine(Path.GetTempPath(), "vertex-host-" + Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(folder, "aubscraft.mrpack");
        Directory.CreateDirectory(folder);
        using (var zip = System.IO.Compression.ZipFile.Open(pack, System.IO.Compression.ZipArchiveMode.Create))
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, Path.Combine(AppContext.BaseDirectory, "Live", "Fixtures", "aubscraft-1.21.5.modrinth.index.json"), "modrinth.index.json");
        var host = new VertexHost(folder) { UseEmbeddedModdedBlocks = false, ModsCache = PaperServer.CacheRoot };
        try
        {
            host.Settings.Server = "127.0.0.1";
            host.Settings.Port = Port + 1;
            host.Settings.Account = "";
            host.Settings.OfflineName = "VertexLearner";
            host.Settings.Voice = false;
            host.Settings.Minecraft = true;
            host.Settings.Reachy = false;
            host.Settings.Modpack = pack;
            await host.StartAsync().WaitAsync(Timeout);
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);
            var first = host.Client!;
            Assert.That(first.UnknownModdedBlocks, Is.Not.Empty, "nothing known yet");
            Assert.That(host.Lines.Any(l => l.Text.Contains("modded blocks I have no data for")), "said so at join");

            var (fx, fy, fz) = (2, 120, 0);
            await Wait.UntilAsync(() => first.World.GetRawState(fx, fy, fz) >= first.World.Data.VanillaStateCount, TimeSpan.FromSeconds(30),
                () => $"door chunk; Vertex at {first.Position}, raw there {first.World.GetRawState(fx, fy, fz)}");
            var unknownWhenLoaded = first.World.Data.IsUnknownBlock(first.World.GetState(fx, fy, fz));
            TestContext.Out.WriteLine($"Vertex spawned at {first.Position}; the door was unknown when loaded: {unknownWhenLoaded}");
            // It learns, and the same connection knows them all, the door it already had included.
            await Wait.UntilAsync(() => first.UnknownModdedBlocks.Count == 0, TimeSpan.FromMinutes(10),
                () => string.Join(Environment.NewLine, host.Lines.TakeLast(12).Select(l => $"{l.Kind}: {l.Text}")));
            foreach (var l in host.Lines) TestContext.Out.WriteLine($"{l.Kind}: {l.Text}");
            Assert.That(host.Client, Is.SameAs(first), "no rejoin");
            Assert.That(unknownWhenLoaded, "the door was loaded before Vertex knew what it was");
            Assert.That(first.World.GetBlock(fx, fy, fz)?.Name, Is.EqualTo("spookydoors:spooky_oak_door"), "and now it knows it, no rejoin");
            Assert.That(host.Lines.Any(l => l.Text.Contains("I know this server's blocks now")), "said so");
            Assert.That(first.World.Data.ModdedBlockCount, Is.EqualTo(first.FabricRegistries["minecraft:block"].Count - (SpawnDev.Minecraft.Data.GameData.Vanilla.Blocks.Length - 1)));
            Assert.That(Directory.GetFiles(Path.Combine(folder, "worlds"), "*.blocks.json"), Has.Length.EqualTo(1), "saved for this world");
            Assert.That(host.Lines.Any(l => l.Kind == LineKind.Error), Is.False, string.Join(Environment.NewLine, host.Lines.Where(l => l.Kind == LineKind.Error).Select(l => l.Text)));
            await host.StopAsync();
        }
        finally
        {
            await host.DisposeAsync();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    /// <summary>
    /// Curiosity: on a modded server with no modpack (and the built-in catalog off), an idle Vertex studies the
    /// unknown blocks around it by touch on its own, saves what it learned for that world, and starts the next
    /// session knowing them.
    /// </summary>
    [Test]
    public async Task StudiesUnknownBlocks_WhenIdle_AndRemembersThem()
    {
        if (!SpookyServer.Available) Assert.Ignore("no spooky server installed here: " + SpookyServer.Template);
        await using var server = await SpookyServer.StartAsync("host-curious", Port + 2);
        var folder = Path.Combine(Path.GetTempPath(), "vertex-host-" + Guid.NewGuid().ToString("N"));
        var host = new VertexHost(folder) { UseEmbeddedModdedBlocks = false, ModsCache = PaperServer.CacheRoot };
        try
        {
            host.Settings.Server = "127.0.0.1";
            host.Settings.Port = Port + 2;
            host.Settings.Account = "";
            host.Settings.OfflineName = "VertexCurious";
            host.Settings.Voice = false;
            host.Settings.Minecraft = true;
            host.Settings.Reachy = false;
            await host.StartAsync().WaitAsync(Timeout);
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);
            var client = host.Client!;
            host.Brain!.IdleBeforeGoals = TimeSpan.FromSeconds(3);
            var (x, y, z) = client.FeetBlock;
            await server.CommandAsync($"fill {x - 6} {y} {z - 6} {x + 6} {y + 4} {z + 6} minecraft:air");
            await server.CommandAsync($"fill {x - 6} {y - 1} {z - 6} {x + 6} {y - 1} {z + 6} minecraft:stone");
            await server.CommandAsync($"setblock {x + 3} {y} {z} mcwholidays:bat_doormat");
            await server.CommandAsync($"setblock {x} {y} {z + 3} mcwholidays:blue_candy_cane_block");
            // Nobody asks it anything: it gets curious by itself.
            await Wait.UntilAsync(() => client.LearnedStates.Count >= 2 && host.Brain!.Tasks.Current == null, TimeSpan.FromMinutes(5),
                () => string.Join(Environment.NewLine, host.Lines.TakeLast(12).Select(l => $"{l.Kind}: {l.Text}")));
            foreach (var l in host.Lines) TestContext.Out.WriteLine($"{l.Kind}: {l.Text}");
            Assert.That(host.Lines.Any(l => l.Text.Contains("studying blocks I don't know") || l.Text.Contains("studied")), "it said what it was doing");
            await host.StopAsync();
            Assert.That(Directory.GetFiles(Path.Combine(folder, "worlds"), "*.touched.json"), Has.Length.EqualTo(1), "saved for this world");

            // Next session: it knows them from the start.
            await host.StartAsync().WaitAsync(Timeout);
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);
            Assert.That(host.Client!.LearnedStates.Count, Is.GreaterThanOrEqualTo(2));
            await host.StopAsync();
        }
        finally
        {
            await host.DisposeAsync();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
