using System.Collections.Concurrent;
using SpawnDev.Minecraft;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// The whole brain against a REAL Paper server with a REAL language model on the GPU (llama3.1:8b from the Ollama
/// cache through SpawnDev.AI): a player asks in chat, the model must pick the right tool, and the body must do it.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class BrainTests
{
    private const int Port = 25936;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private PaperServer _server = null!;
    private VertexAi _ai = null!;
    private string _model = "";

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("brain", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
        _ai = new VertexAi(m => TestContext.Progress.WriteLine("[ai] " + m));
        var models = await _ai.ModelNamesAsync();
        var model = models.FirstOrDefault(m => m.StartsWith("llama3.1"));
        if (model == null) Assert.Fail("llama3.1 not in the Ollama cache: " + string.Join(", ", models));
        _model = model!;
        await _ai.Chat.EnsureReadyAsync(_model);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_ai != null) await _ai.DisposeAsync();
        if (_server != null) await _server.DisposeAsync();
    }

    private async Task<(MinecraftClient Vertex, VertexBrain Brain, MinecraftClient Aubs, ConcurrentQueue<string> Log)> JoinAsync(string folder)
    {
        var log = new ConcurrentQueue<string>();
        var vertex = new MinecraftClient(GameProfile.Offline("VertexBrain"));
        var aubs = new MinecraftClient(GameProfile.Offline("Aubri"));
        using var cts = new CancellationTokenSource(Timeout);
        await vertex.ConnectAsync("127.0.0.1", Port, cts.Token);
        await aubs.ConnectAsync("127.0.0.1", Port, cts.Token);
        var store = new CharacterStore(Path.Combine(Path.GetTempPath(), "vertex-tests-" + folder));
        var brain = new VertexBrain(vertex, null, _ai, store, Character.BuiltIn[0], _model);
        brain.Log += m => { log.Enqueue(m); TestContext.Out.WriteLine(m); };
        brain.Mouth.EchoToChat = true;
        brain.Start();
        await Wait.UntilAsync(() => vertex.Entities.Values.Any(e => e.PlayerName == "Aubri"), Timeout, () => "Vertex sees Aubri");
        return (vertex, brain, aubs, log);
    }

    [Test]
    public async Task ComeHere_WalksOverToThePlayer()
    {
        var (vertex, brain, aubs, log) = await JoinAsync("come");
        await using var _v = vertex;
        await using var _b = brain;
        await using var _a = aubs;
        var heard = new ConcurrentQueue<ChatMessage>();
        aubs.ChatReceived += heard.Enqueue;

        // Put Aubri 12 blocks away on the same ground.
        var (vx, vy, vz) = vertex.Position;
        await _server.CommandAsync($"spreadplayers {vx + 12} {vz} 0 1 false Aubri");
        await Wait.UntilAsync(() => Math.Abs(aubs.Position.X - (vx + 12)) < 2, Timeout, () => $"aubs at {aubs.Position}");
        await aubs.WaitTicksAsync(20);

        await aubs.SendChatAsync("Vertex, come here please!");
        await Wait.UntilAsync(() =>
        {
            var (x, y, z) = vertex.Position;
            var (ax, ay, az) = aubs.Position;
            return Math.Sqrt((x - ax) * (x - ax) + (z - az) * (z - az)) < 3.5;
        }, Timeout, () => $"vertex at {vertex.Position}, aubs at {aubs.Position}; log: {string.Join(" | ", log)}");
        Assert.That(log, Has.Some.Contains("tool come_here").Or.Some.Contains("tool follow_player"));
        // And it said something back in chat.
        await Wait.UntilAsync(() => heard.Any(m => m.SenderId == vertex.Profile.Id), Timeout, () => "no reply");
        TestContext.Out.WriteLine("Vertex said: " + string.Join(" / ", heard.Where(m => m.SenderId == vertex.Profile.Id).Select(m => m.Content)));
    }

    [Test]
    public async Task CollectRequest_MinesAndPicksUpBlocks()
    {
        var (vertex, brain, aubs, log) = await JoinAsync("collect");
        await using var _v = vertex;
        await using var _b = brain;
        await using var _a = aubs;
        var (x, y, z) = vertex.Position;
        int bx = (int)Math.Floor(x), by = (int)Math.Floor(y), bz = (int)Math.Floor(z);
        // A little pile of dirt next to Vertex.
        await _server.CommandAsync($"fill {bx + 2} {by} {bz - 1} {bx + 3} {by} {bz + 1} minecraft:dirt");
        await vertex.WaitTicksAsync(10);

        await aubs.SendChatAsync("Vertex can you get me 3 dirt blocks?");
        await Wait.UntilAsync(() => vertex.Inventory.Count("dirt") >= 3, Timeout,
            () => $"dirt {vertex.Inventory.Count("dirt")}; log: {string.Join(" | ", log)}");
        Assert.That(log, Has.Some.Contains("tool collect").Or.Some.Contains("tool give"));
    }
}
