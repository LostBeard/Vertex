using System.Collections.Concurrent;
using SpawnDev.Minecraft;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// The whole thing, end to end, on a REAL Paper server with the REAL language model: a child says "build me a little house" in chat with nothing in Vertex's pockets, and a house of planks
/// is standing a few minutes later (the model picks the tool, Vertex cuts logs, makes planks and builds, then says how it went).
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class BrainEndToEndTests
{
    private const int Port = 25974;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;
    private VertexAi _ai = null!;
    private string _model = "";
    private readonly List<string> _folders = [];

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("brain-e2e", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
        _ai = TestAi.Create();
        var models = await _ai.ModelNamesAsync();
        _model = models.FirstOrDefault(m => m.StartsWith(Environment.GetEnvironmentVariable("VERTEX_TEST_MODEL") ?? "llama3.1")) ?? throw new AssertionException("the model (VERTEX_TEST_MODEL, default llama3.1) is not in the Ollama cache: " + string.Join(", ", models));
        await _ai.EnsureChatReadyAsync(_model);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_ai != null) await _ai.DisposeAsync();
        if (_server != null) await _server.DisposeAsync();
        foreach (var f in _folders) if (Directory.Exists(f)) Directory.Delete(f, true);
    }

    [Test]
    public async Task AChildAsksForAHouse_WithEmptyPockets_AndGetsOne()
    {
        var log = new ConcurrentQueue<string>();
        var vertex = new MinecraftClient(GameProfile.Offline("VertexBrain"), new ClientOptions());
        var aubs = new MinecraftClient(GameProfile.Offline("Aubri"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await vertex.ConnectAsync("127.0.0.1", Port, cts.Token);
        await Task.Delay(1500);
        await aubs.ConnectAsync("127.0.0.1", Port, cts.Token);
        await using var _v = vertex;
        await using var _a = aubs;
        var root = Path.Combine(Path.GetTempPath(), $"vertex-tests-e2e-{Guid.NewGuid():N}");
        _folders.Add(root);
        var store = new CharacterStore(Path.Combine(root, "characters"));
        var brain = new VertexBrain(vertex, null, _ai, store, Character.BuiltIn[0], _model);
        brain.Log += m => { log.Enqueue(m); TestContext.Out.WriteLine(m); };
        brain.Channel.EchoText = true;
        brain.Start();
        await using var _b = brain;
        await Wait.UntilAsync(() => vertex.Entities.Values.Any(e => e.PlayerName == "Aubri"), Timeout, () => "Vertex sees Aubri");

        var (vx, _, vz) = vertex.Position;
        int ax = (int)Math.Floor(vx), az = (int)Math.Floor(vz);
        const int r = 20;
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax + r} 215 {az + r} minecraft:air");
        await _server.CommandAsync($"fill {ax - r} 199 {az - r} {ax + r} 199 {az + r} minecraft:stone");
        for (var i = 0; i < 5; i++)
            await _server.CommandAsync($"fill {ax + 12} 200 {az - 6 + i * 3} {ax + 12} 203 {az - 6 + i * 3} minecraft:oak_log");
        await _server.CommandAsync("clear VertexBrain");
        await Task.Delay(1500);
        await _server.CommandAsync($"tp VertexBrain {ax + 0.5} 200 {az + 0.5} -90 0");
        await _server.CommandAsync($"tp Aubri {ax - 4.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => vertex.World.IsLoaded(ax + 12, az) && vertex.World.GetBlock(ax + 12, 202, az - 6)?.Name == "minecraft:oak_log", Timeout, () => "the trees");
        await vertex.WaitTicksAsync(20);

        await aubs.SendChatAsync("Vertex, can you build me a little house?");

        int Planks()
        {
            var n = 0;
            for (var x = ax - 10; x <= ax + 12; x++)
            for (var z = az - 10; z <= az + 10; z++)
                for (var y = 200; y <= 205; y++)
                    if (vertex.World.GetBlock(x, y, z)?.Name.EndsWith("_planks") == true) n++;
            return n;
        }
        await Wait.UntilAsync(() => Planks() >= 30, TimeSpan.FromMinutes(6), () => $"a house of planks ({Planks()} plank blocks) | " + string.Join(" | ", log.TakeLast(6)));
        TestContext.Out.WriteLine($"{Planks()} plank blocks standing");
        Assert.That(Planks(), Is.GreaterThanOrEqualTo(30));
    }
}
