using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SpawnDev.Minecraft;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// With this many tools on offer, does the language model still pick the right one for what a child says? The real model (llama3.1:8b through SpawnDev.AI) is asked a dozen things in chat
/// and the first tool it calls (with its arguments) is read from the log; each tool is stopped right away (this tests the choice, not the doing, which has its own tests on Paper).
/// MEASURED 2026-10-15 with 42 tools: 3 of 14 right (garbled arguments, a prompt cache that thrashed, and a history poisoned by "I called X" with no call), which is why there are 35 now,
/// the build, trade, farm and animal tools are merged, and these turns keep the tool list in the prompt.
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

    // What a child (or a parent) might say, the tool that is the right answer, and what its arguments must contain.
    private static readonly (string Says, string Tool, string[] ArgsContain)[] Cases =
    [
        ("Vertex, can you build me a castle?", "build_shape", ["castle"]),
        ("Vertex, build a little cabin please", "build_shape", []),
        ("Vertex, make a platform to stand on", "build_shape", ["platform"]),
        ("Vertex, keep building where you left off", "build_shape", ["continue"]),
        ("Vertex, what does that villager sell?", "trade", []),
        ("Vertex, sell all my wheat to the villager", "trade", ["wheat"]),
        ("Vertex, buy some bread from the villager", "trade", ["bread"]),
        ("Vertex, make me a wheat farm", "farm", ["plant"]),
        ("Vertex, harvest the crops", "farm", []),
        ("Vertex, breed the cows", "animals", ["breed"]),
        ("Vertex, shear the sheep", "animals", ["shear"]),
        ("Vertex, can you milk the cow?", "animals", ["milk"]),
        ("Vertex, stay right here and wait for me", "stay", []),
        ("Vertex, it is too dark, put some torches around", "light_up", []),
        ("Vertex, shoot that zombie with your bow", "shoot", ["zombie"]),
        ("Vertex, follow me", "follow_player", []),
        ("Vertex, get me ten wood", "collect", []),
        ("Vertex, let's go fishing!", "fish", []),
    ];

    private static readonly Regex ToolLine = new(@"^\s+tool (\w+)\((.*)\) ->", RegexOptions.Compiled);

    [Test]
    public async Task EachRequestGetsTheRightTool()
    {
        var log = new ConcurrentQueue<string>();
        var vertex = new MinecraftClient(GameProfile.Offline("VertexTools"), new ClientOptions());
        var aubs = new MinecraftClient(GameProfile.Offline("Aubri"));
        // A client the server drops (a keep-alive timeout while the machine is busy) makes every later prompt read "no tool": say so in the log (a full run once went silent after two prompts).
        vertex.Disconnected += reason => { log.Enqueue("!! Vertex's connection ended: " + reason); TestContext.Out.WriteLine("!! Vertex's connection ended: " + reason); };
        aubs.Disconnected += reason => { log.Enqueue("!! Aubri's connection ended: " + reason); TestContext.Out.WriteLine("!! Aubri's connection ended: " + reason); };
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
        if (Environment.GetEnvironmentVariable("VERTEX_TEST_INSTINCT") == "0") brain.Companion.UseInstinct = false;       // to compare with and without the instinct hint
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

        var results = new List<(string Says, string Wanted, string Got, bool Ok)>();
        foreach (var (says, wanted, argsContain) in Cases)
        {
            brain.Tasks.Stop();
            await Wait.UntilAsync(() => !brain.Companion.IsThinking && !brain.Tasks.IsBusy, Timeout, () => "still busy before: " + says);
            while (log.TryDequeue(out _)) { }
            await aubs.SendChatAsync(says);
            string? chosen = null, args = "";
            var started = DateTime.UtcNow;
            var deadline = started + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline && chosen == null)
            {
                await Task.Delay(300);
                foreach (var line in log.ToArray())
                    if (ToolLine.Match(line) is { Success: true } m) { chosen = m.Groups[1].Value; args = m.Groups[2].Value; break; }
            }
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            var ok = chosen == wanted && argsContain.All(w => args.Contains(w, StringComparison.OrdinalIgnoreCase));
            results.Add((says, wanted + (argsContain.Length > 0 ? " " + string.Join(",", argsContain) : ""), chosen == null ? "(no tool)" : chosen + " " + args, ok));
            TestContext.Out.WriteLine($"=> \"{says}\": wanted {wanted} {string.Join(",", argsContain)}, got {chosen ?? "(no tool)"} {args} [{seconds:0} s] {(ok ? "ok" : "WRONG")}");
        }
        brain.Tasks.Stop();

        var summary = string.Join("\n", results.Select(r => $"  {(r.Ok ? "ok   " : "WRONG")} \"{r.Says}\": wanted {r.Wanted}, got {r.Got}"));
        TestContext.Out.WriteLine("\n" + summary);
        Assert.That(results.Count(r => r.Ok), Is.GreaterThanOrEqualTo(results.Count - 2), "at most two wrong choices of " + results.Count + ":\n" + summary);
    }
}
