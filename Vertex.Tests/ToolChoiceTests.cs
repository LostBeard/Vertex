using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SpawnDev.Minecraft;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// With this many tools on offer, does the language model still pick the right one for what a child says? The real model (llama3.1:8b through SpawnDev.AI) is asked a dozen things in chat
/// and the tool it calls first is read from the log; each tool is stopped right away (this tests the choice, not the doing, which has its own tests on Paper).
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class ToolChoiceTests
{
    private const int Port = 25960;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private PaperServer _server = null!;
    private VertexAi _ai = null!;
    private string _model = "";
    private readonly List<string> _folders = [];

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("toolchoice", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
        _ai = TestAi.Create();
        var models = await _ai.ModelNamesAsync();
        _model = models.FirstOrDefault(m => m.StartsWith("llama3.1")) ?? throw new AssertionException("llama3.1 not in the Ollama cache: " + string.Join(", ", models));
        await _ai.EnsureChatReadyAsync(_model);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_ai != null) await _ai.DisposeAsync();
        if (_server != null) await _server.DisposeAsync();
        foreach (var f in _folders) if (Directory.Exists(f)) Directory.Delete(f, true);
    }

    // What a child (or a parent) might say, and the tool that is the right answer; a set of acceptable tools where more than one is right.
    private static readonly (string Says, string[] Tools)[] Cases =
    [
        ("Vertex, can you build me a castle?", ["build_shape"]),
        ("Vertex, build a little cabin please", ["build_shape"]),
        ("Vertex, make a platform to stand on", ["build_shape"]),
        ("Vertex, keep building where you left off", ["continue_building"]),
        ("Vertex, what does that villager sell?", ["villager_trades"]),
        ("Vertex, sell all my wheat to the villager", ["trade"]),
        ("Vertex, buy some bread from the villager", ["trade"]),
        ("Vertex, make me a wheat farm", ["plant_field"]),
        ("Vertex, harvest the crops", ["farm"]),
        ("Vertex, breed the cows", ["breed_animals"]),
        ("Vertex, shear the sheep", ["shear_sheep"]),
        ("Vertex, can you milk the cow?", ["milk_cow"]),
        ("Vertex, follow me", ["follow_player"]),
        ("Vertex, get me ten wood", ["collect"]),
    ];

    [Test]
    public async Task EachRequestGetsTheRightTool()
    {
        var log = new ConcurrentQueue<string>();
        var vertex = new MinecraftClient(GameProfile.Offline("VertexTools"), new ClientOptions());
        var aubs = new MinecraftClient(GameProfile.Offline("Aubri"));
        using var cts = new CancellationTokenSource(Timeout);
        await vertex.ConnectAsync("127.0.0.1", Port, cts.Token);
        await aubs.ConnectAsync("127.0.0.1", Port, cts.Token);
        await using var _v = vertex;
        await using var _a = aubs;
        var root = Path.Combine(Path.GetTempPath(), $"vertex-tests-tools-{Guid.NewGuid():N}");
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
        await _server.CommandAsync($"fill {ax - 6} 200 {az - 6} {ax + 6} 210 {az + 6} minecraft:air");
        await _server.CommandAsync($"fill {ax - 6} 199 {az - 6} {ax + 6} 199 {az + 6} minecraft:stone");
        await _server.CommandAsync($"tp VertexTools {ax + 0.5} 200 {az + 0.5}");
        await _server.CommandAsync($"tp Aubri {ax + 3.5} 200 {az + 0.5}");
        await vertex.WaitTicksAsync(20);

        var results = new List<(string Says, string Wanted, string? Got, bool Ok)>();
        foreach (var (says, tools) in Cases)
        {
            brain.Tasks.Stop();
            await Wait.UntilAsync(() => !brain.Companion.IsThinking && !brain.Tasks.IsBusy, Timeout, () => "still busy before: " + says);
            while (log.TryDequeue(out _)) { }
            await aubs.SendChatAsync(says);
            string? chosen = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline && chosen == null)
            {
                await Task.Delay(300);
                foreach (var line in log.ToArray())
                    if (Regex.Match(line, @"tool (\w+)") is { Success: true } m) { chosen = m.Groups[1].Value; break; }
            }
            var ok = chosen != null && tools.Contains(chosen);
            results.Add((says, string.Join("|", tools), chosen, ok));
            TestContext.Out.WriteLine($"=> \"{says}\": wanted {string.Join("|", tools)}, got {chosen ?? "(no tool)"} {(ok ? "ok" : "WRONG")}");
        }
        brain.Tasks.Stop();

        var summary = string.Join("\n", results.Select(r => $"  {(r.Ok ? "ok   " : "WRONG")} \"{r.Says}\": wanted {r.Wanted}, got {r.Got ?? "(no tool)"}"));
        TestContext.Out.WriteLine("\n" + summary);
        Assert.That(results.Count(r => r.Ok), Is.GreaterThanOrEqualTo(results.Count - 1), "at most one wrong choice of " + results.Count + ":\n" + summary);
    }
}
