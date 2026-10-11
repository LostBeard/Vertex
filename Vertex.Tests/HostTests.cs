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
            await host.StartAsync().WaitAsync(Timeout);
            foreach (var l in host.Lines) TestContext.Out.WriteLine($"{l.Kind}: {l.Text}");
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);
            Assert.That(host.Status, Does.Contain("Playing as Vertex (VertexHost)"));
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
    /// learns the blocks from the server's modpack in the background, saves them for that world, rejoins, and
    /// then knows every block. The pack is a copy of the real AubsCraft index (jars from Modrinth's CDN).
    /// </summary>
    [Test]
    public async Task LearnsAModdedServersBlocks_FromItsModpack_AndRejoins()
    {
        if (!SpookyServer.Available) Assert.Ignore("no spooky server installed here: " + SpookyServer.Template);
        await using var server = await SpookyServer.StartAsync("host-spooky", Port + 1);
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
            host.Settings.Modpack = pack;
            await host.StartAsync().WaitAsync(Timeout);
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);
            var first = host.Client!;
            Assert.That(first.UnknownModdedBlocks, Is.Not.Empty, "nothing known yet");
            Assert.That(host.Lines.Any(l => l.Text.Contains("modded blocks I have no data for")), "said so at join");

            // It learns, then rejoins as a new client that knows them all.
            await Wait.UntilAsync(() => host.Client != null && !ReferenceEquals(host.Client, first) && host.State == HostState.Playing, TimeSpan.FromMinutes(10),
                () => string.Join(Environment.NewLine, host.Lines.TakeLast(12).Select(l => $"{l.Kind}: {l.Text}")));
            foreach (var l in host.Lines) TestContext.Out.WriteLine($"{l.Kind}: {l.Text}");
            Assert.That(host.Client!.UnknownModdedBlocks, Is.Empty);
            Assert.That(host.Client.World.Data.ModdedBlockCount, Is.EqualTo(host.Client.FabricRegistries["minecraft:block"].Count - (SpawnDev.Minecraft.Data.GameData.Vanilla.Blocks.Length - 1)));
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
}
