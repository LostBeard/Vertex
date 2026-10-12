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
        await _ai.EnsureChatReadyAsync(_model);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_ai != null) await _ai.DisposeAsync();
        if (_server != null) await _server.DisposeAsync();
        foreach (var f in _folders) if (Directory.Exists(f)) Directory.Delete(f, true);
    }

    private readonly List<string> _folders = [];

    private async Task<(MinecraftClient Vertex, VertexBrain Brain, MinecraftClient Aubs, ConcurrentQueue<string> Log)> JoinAsync(string folder, Action<VertexBrain>? configure = null)
    {
        var log = new ConcurrentQueue<string>();
        var vertex = new MinecraftClient(GameProfile.Offline("VertexBrain"), new ClientOptions
        {
            Log = m => { if (m.Contains("path") || m.Contains("replan")) { log.Enqueue("[client] " + m); TestContext.Out.WriteLine("[client] " + m); } },
        });
        var aubs = new MinecraftClient(GameProfile.Offline("Aubri"));
        using var cts = new CancellationTokenSource(Timeout);
        await vertex.ConnectAsync("127.0.0.1", Port, cts.Token);
        await aubs.ConnectAsync("127.0.0.1", Port, cts.Token);
        // Its own folder per test: world memory (facts, people, goals) lives next to the characters and persists.
        var root = Path.Combine(Path.GetTempPath(), $"vertex-tests-{folder}-{Guid.NewGuid():N}");
        _folders.Add(root);
        var store = new CharacterStore(Path.Combine(root, "characters"));
        var brain = new VertexBrain(vertex, null, _ai, store, Character.BuiltIn[0], _model);
        brain.Log += m => { log.Enqueue(m); TestContext.Out.WriteLine(m); };
        brain.Channel.EchoText = true;
        configure?.Invoke(brain);
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

        // A fixed stage (natural terrain made this flaky: players rejoin where they left, and spreadplayers can put
        // Aubri on a tree top). Vertex at one end of a sky platform, Aubri 12 blocks along it.
        var (vx, _, vz) = vertex.Position;
        int ax = (int)Math.Floor(vx), az = (int)Math.Floor(vz);
        await _server.CommandAsync($"fill {ax - 2} 200 {az - 4} {ax + 16} 204 {az + 4} minecraft:air");
        await _server.CommandAsync($"fill {ax - 2} 199 {az - 4} {ax + 16} 199 {az + 4} minecraft:stone");
        await _server.CommandAsync($"tp VertexBrain {ax + 0.5} 200 {az + 0.5}");
        await _server.CommandAsync($"tp Aubri {ax + 12.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(aubs.Position.X - (ax + 12.5)) < 0.1 && Math.Abs(vertex.Position.Y - 200) < 1e-9 && vertex.OnGround, Timeout, () => $"aubs at {aubs.Position}, vertex at {vertex.Position}");
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

    /// <summary>
    /// Per-player memory: a fact a player tells Vertex about themselves is kept WITH that player (by UUID), shows up
    /// in the prompt when they are around, and when they come back to play later Vertex greets them by name.
    /// </summary>
    [Test]
    public async Task RemembersAboutAPlayer_AndGreetsThemWhenTheyComeBack()
    {
        var (vertex, brain, aubs, log) = await JoinAsync("people");
        await using var _v = vertex;
        await using var _b = brain;
        var aubriId = aubs.Profile.Id;
        await Wait.UntilAsync(() => brain.Memory.People.ContainsKey(aubriId), Timeout, () => "met Aubri");

        await aubs.SendChatAsync("Vertex, remember that I love pink axolotls!");
        await Wait.UntilAsync(() => brain.Memory.People[aubriId].Facts.Any(f => f.Contains("axolotl", StringComparison.OrdinalIgnoreCase)), Timeout,
            () => $"facts about Aubri: [{string.Join("; ", brain.Memory.People[aubriId].Facts)}], world facts: [{string.Join("; ", brain.Memory.Facts)}]; log: {string.Join(" | ", log)}");
        Assert.That(brain.NowPrompt(), Does.Contain("About Aubri:").And.Contain("axolotl"));

        // Aubri goes; two hours later she is back.
        await aubs.DisposeAsync();
        await Wait.UntilAsync(() => !vertex.Players.Values.Any(p => p.Name == "Aubri"), Timeout, () => "Aubri left");
        var later = DateTime.UtcNow.AddHours(2);
        brain.Now = () => later + (DateTime.UtcNow - later.AddHours(-2));
        await using var back = new MinecraftClient(GameProfile.Offline("Aubri"));
        var heard = new ConcurrentQueue<ChatMessage>();
        back.ChatReceived += heard.Enqueue;
        using (var cts = new CancellationTokenSource(Timeout)) await back.ConnectAsync("127.0.0.1", Port, cts.Token);
        // The reply to the "came to play" update (not some earlier line that reached chat late) greets her by name,
        // and she hears it.
        string? Greeting()
        {
            var lines = log.ToList();
            var at = lines.FindIndex(l => l.Contains("Aubri just came to play"));
            return at < 0 ? null : lines.Skip(at + 1).FirstOrDefault(l => l.StartsWith("< "))?[2..];
        }
        await Wait.UntilAsync(() => Greeting() != null, Timeout, () => "no greeting; log: " + string.Join(" | ", log));
        var greeting = Greeting()!;
        TestContext.Out.WriteLine("Greeting: " + greeting);
        Assert.That(greeting, Does.Contain("Aubri"));
        await Wait.UntilAsync(() => heard.Any(m => m.SenderId == vertex.Profile.Id && greeting.StartsWith(m.Content.TrimEnd('.', '!'))), Timeout,
            () => "greeting not heard in chat: " + string.Join(" | ", heard.Select(m => m.Content)));
        Assert.That(brain.Memory.People[aubriId].Visits, Is.EqualTo(2));
    }

    /// <summary>
    /// A long-running goal: a player gives Vertex a goal, and when nobody needs it Vertex works on it by itself (the
    /// idle drive picks the goal, the language model picks the step), and the step is remembered as progress.
    /// </summary>
    [Test]
    public async Task WorksOnAGoal_WhenNobodyNeedsIt()
    {
        var (vertex, brain, aubs, log) = await JoinAsync("goal", b => { b.IdleBeforeGoals = TimeSpan.FromSeconds(5); b.IdleCheckEvery = TimeSpan.FromSeconds(1); });
        await using var _v = vertex;
        await using var _b = brain;
        await using var _a = aubs;
        var (x, y, z) = vertex.Position;
        int bx = (int)Math.Floor(x), by = (int)Math.Floor(y), bz = (int)Math.Floor(z);
        await _server.CommandAsync($"fill {bx + 2} {by} {bz - 1} {bx + 3} {by} {bz + 1} minecraft:dirt");
        await vertex.WaitTicksAsync(10);

        await aubs.SendChatAsync("Vertex, here is a long-term goal for you: collect 5 dirt blocks for me. Work on it whenever you are not busy.");
        await Wait.UntilAsync(() => brain.Memory.Goals.Count > 0, Timeout, () => "no goal set; log: " + string.Join(" | ", log));
        var goal = brain.Memory.Goals[0];
        TestContext.Out.WriteLine($"goal: {goal.Text} (for {goal.For})");
        Assert.That(goal.For, Is.EqualTo("Aubri"));

        // Nobody says anything more: the idle drive must take a step on its own.
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("working on goal:")) && goal.Progress.Count > 0, Timeout,
            () => $"progress [{string.Join("; ", goal.Progress)}], done {goal.Done}; log: {string.Join(" | ", log)}");
        // And once it has the dirt, it calls the goal done instead of collecting again and again.
        await Wait.UntilAsync(() => goal.Done != null, Timeout,
            () => $"never finished: progress [{string.Join("; ", goal.Progress)}]; log: {string.Join(" | ", log)}");
        TestContext.Out.WriteLine("progress: " + string.Join("; ", goal.Progress));
        Assert.That(vertex.Inventory.Count("dirt") + aubs.Inventory.Count("dirt"), Is.GreaterThanOrEqualTo(3), "it did the work (the 6-block pile leaves 3+ after drops)");
        Assert.That(brain.Memory.People[aubs.Profile.Id].History, Has.Some.Contains("dirt"), "Aubri's history has it");
    }

    /// <summary>
    /// Calling Vertex by name while it is still answering cuts that answer short and answers the new line at once
    /// (2026-10-12 live: one slow turn left everything TJ said after it unheard). Every turn logs where its time went.
    /// </summary>
    [Test]
    public async Task CalledByNameWhileAnswering_StopsAndAnswersTheNewLine()
    {
        var (vertex, brain, aubs, log) = await JoinAsync("bargein");
        await using var _v = vertex;
        await using var _b = brain;
        await using var _a = aubs;

        await aubs.SendChatAsync("Vertex, tell me a long story about a dragon who builds a castle out of diamonds.");
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("> ") && l.Contains("dragon")), Timeout, () => "the story never started; log: " + string.Join(" | ", log));
        await aubs.SendChatAsync("Vertex, stop! What is your name?");
        await Wait.UntilAsync(() => log.Any(l => l.Contains("(interrupted by Aubri)")), Timeout, () => "not interrupted; log: " + string.Join(" | ", log));
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("> ") && l.Contains("What is your name")), Timeout, () => "the new line was not heard; log: " + string.Join(" | ", log));
        await Wait.UntilAsync(() => log.SkipWhile(l => !(l.StartsWith("> ") && l.Contains("What is your name"))).Any(l => l.StartsWith("< ")), Timeout,
            () => "no answer to the new line; log: " + string.Join(" | ", log));
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("timing: spoke")), Timeout, () => "no timing line; log: " + string.Join(" | ", log));
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, log.Where(l => l.StartsWith("timing:"))));
    }

    /// <summary>A turn that runs out of time says so, and the next thing anybody says is still heard.</summary>
    [Test]
    public async Task ATurnThatTakesTooLong_GivesUpAndKeepsListening()
    {
        var (vertex, brain, aubs, log) = await JoinAsync("timeout");
        await using var _v = vertex;
        await using var _b = brain;
        await using var _a = aubs;
        var heard = new ConcurrentQueue<ChatMessage>();
        aubs.ChatReceived += heard.Enqueue;

        brain.TurnTimeout = TimeSpan.FromMilliseconds(300); // far too short for any reply
        await aubs.SendChatAsync("Vertex, what do you like to build?");
        await Wait.UntilAsync(() => log.Any(l => l.Contains("(gave up thinking")), Timeout, () => "never gave up; log: " + string.Join(" | ", log));
        await Wait.UntilAsync(() => heard.Any(m => m.SenderId == vertex.Profile.Id && m.Content.Contains("train of thought")), Timeout, () => "did not say it lost the thread");

        brain.TurnTimeout = TimeSpan.FromSeconds(60);
        await aubs.SendChatAsync("Vertex, what is your favourite block?");
        await Wait.UntilAsync(() => log.SkipWhile(l => !(l.StartsWith("> ") && l.Contains("favourite block"))).Any(l => l.StartsWith("< ") && l.Length > 3), Timeout,
            () => "deaf after the timeout; log: " + string.Join(" | ", log));
    }
}
