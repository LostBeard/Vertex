using System.Collections.Concurrent;
using SpawnDev.Minecraft;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Self-directed play (TJ, 2026-11-01: Vertex "playing on his own, building and exploring" for Aubs to watch): with no goal and nobody needing him for a while, he chooses ONE small project
/// and starts it with the same tools as any request. Off by default, survival only, by day, healthy and fed.
/// </summary>
public class SelfDirectedPolicyTests
{
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5), Sixty = TimeSpan.FromSeconds(60);

    private static IdleAction Decide(bool on = true, bool fit = true, double idleSeconds = 400, bool staying = false, bool busy = false, bool queued = false, bool spawned = true,
        bool hasGoal = false, bool unstudied = false)
        => IdlePolicy.Decide(staying, busy, queued, spawned, TimeSpan.FromSeconds(idleSeconds), Sixty, hasGoal, studyWhenIdle: true, hasUnstudiedBlocks: unstudied, selfDirected: on, idleBeforeProject: Five, fit: fit);

    [Test] public void On_Fit_AndIdleLongEnough_HeChoosesAProject() => Assert.That(Decide(), Is.EqualTo(IdleAction.ChooseProject));
    [Test] public void Off_HeDoesNothing_HoweverLongHeIsIdle() => Assert.That(Decide(on: false, idleSeconds: 100000), Is.EqualTo(IdleAction.Nothing));
    [Test] public void NotFit_NightOrHurtOrCreativeOrCoolingDown_HeDoesNothing() => Assert.That(Decide(fit: false), Is.EqualTo(IdleAction.Nothing));
    [Test] public void NotIdleLongEnough_HeWaits() => Assert.That(Decide(idleSeconds: 299), Is.EqualTo(IdleAction.Nothing));
    [Test] public void WhenToldToStay_OrBusy_OrAlreadyQueued_OrNotSpawned_HeDoesNothing()
    {
        Assert.That(Decide(staying: true), Is.EqualTo(IdleAction.Nothing), "\"stay\" outranks everything");
        Assert.That(Decide(busy: true), Is.EqualTo(IdleAction.Nothing));
        Assert.That(Decide(queued: true), Is.EqualTo(IdleAction.Nothing), "one project at a time");
        Assert.That(Decide(spawned: false), Is.EqualTo(IdleAction.Nothing));
    }
    [Test] public void AGoalHeAlreadyHas_OutranksANewProject() => Assert.That(Decide(hasGoal: true), Is.EqualTo(IdleAction.WorkOnGoal));
    [Test] public void StudyingUnknownBlocks_ComesBeforeAProject() => Assert.That(Decide(unstudied: true), Is.EqualTo(IdleAction.StudyBlocks));

    [Test]
    public void AGoalHeSetsForHimself_IsNobodys_ButAFriendsRequestKeepsItsOwner()
    {
        Assert.That(VertexBrain.GoalOwner(choosingOwnProject: true, requested: "Aubri"), Is.Null, "the model's habitual player 'me' must not make his own project Aubri's");
        Assert.That(VertexBrain.GoalOwner(choosingOwnProject: true, requested: null), Is.Null);
        Assert.That(VertexBrain.GoalOwner(choosingOwnProject: false, requested: "Aubri"), Is.EqualTo("Aubri"), "a goal a friend gives is still for that friend");
        Assert.That(VertexBrain.GoalOwner(choosingOwnProject: false, requested: null), Is.Null);
    }
}

/// <summary>The real thing: a real Paper server and the real model; he decides what to do with nobody asking. Settings and memory live in a temp folder.</summary>
[Category("GPU")]
[NonParallelizable]
public class SelfDirectedPlayTests
{
    private const int Port = 25982;
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(4);
    private PaperServer _server = null!;
    private VertexAi _ai = null!;
    private string _model = "";
    private readonly List<string> _folders = [];

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("solo", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("gamerule doDaylightCycle false");
        await _server.CommandAsync("time set day");
        _ai = TestAi.Create();
        _model = TestModel.Pick(await _ai.ModelNamesAsync());
        await _ai.EnsureChatReadyAsync(_model);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_ai != null) await _ai.DisposeAsync();
        if (_server != null) await _server.DisposeAsync();
        foreach (var f in _folders) { try { if (Directory.Exists(f)) Directory.Delete(f, true); } catch (IOException) { } }
    }

    private async Task<(MinecraftClient Client, VertexBrain Brain, ConcurrentQueue<string> Log)> JoinAsync(string name, bool selfDirected)
    {
        var log = new ConcurrentQueue<string>();
        var client = new MinecraftClient(GameProfile.Offline(name));
        using var cts = new CancellationTokenSource(Slow);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var root = Path.Combine(Path.GetTempPath(), $"vertex-tests-solo-{Guid.NewGuid():N}");
        _folders.Add(root);
        var brain = new VertexBrain(client, null, _ai, new CharacterStore(Path.Combine(root, "characters")), Character.BuiltIn[0], _model)
        {
            SelfDirectedPlay = selfDirected,
            SelfDirectedIdle = TimeSpan.FromSeconds(5),
            ProjectCooldown = TimeSpan.FromSeconds(60),
            IdleBeforeGoals = TimeSpan.FromSeconds(5),
            IdleCheckEvery = TimeSpan.FromSeconds(1),
            StudyWhenIdle = false,
        };
        brain.Log += m => { log.Enqueue(m); TestContext.Out.WriteLine(m); };
        brain.Start();
        return (client, brain, log);
    }

    [Test]
    public async Task WithItOn_AndNobodyAround_HeChoosesAProjectAndStartsItWithATool()
    {
        var (client, brain, log) = await JoinAsync("VertexSolo", selfDirected: true);
        await using var _c = client;
        await using var _b = brain;
        Assert.That(client.GameMode, Is.EqualTo(GameMode.Survival));

        await Wait.UntilAsync(() => log.Any(l => l.Contains("choosing a project of my own")), Slow, () => "never chose a project; log: " + string.Join(" | ", log.TakeLast(8)));
        // What he chose is whatever the model decides; what matters is that it became a real action through a tool, not talk.
        await Wait.UntilAsync(() => log.SkipWhile(l => !l.Contains("choosing a project of my own")).Any(l => l.StartsWith("  tool ")), Slow,
            () => "chose, but no tool was called; log: " + string.Join(" | ", log.TakeLast(10)));
        var tool = log.SkipWhile(l => !l.Contains("choosing a project of my own")).First(l => l.StartsWith("  tool "));
        TestContext.Out.WriteLine("HE CHOSE: " + tool);
        Assert.That(tool, Does.Not.Contain("tool stop"), "a project, not a stop");
        Assert.That(brain.Memory.Goals.Where(g => g.For != null), Is.Empty, "nothing he chose for himself is recorded as being FOR someone: " + string.Join("; ", brain.Memory.Goals.Select(g => $"{g.Text} (for {g.For})")));
    }

    [Test]
    public async Task WithItOff_NothingStartsOnItsOwn()
    {
        var (client, brain, log) = await JoinAsync("VertexOff", selfDirected: false);
        await using var _c = client;
        await using var _b = brain;
        await Task.Delay(TimeSpan.FromSeconds(45));
        Assert.That(log.Any(l => l.Contains("choosing a project of my own")), Is.False, "off means off: " + string.Join(" | ", log.TakeLast(5)));
        Assert.That(log.Any(l => l.StartsWith("  tool ")), Is.False, "and no tool ran by itself");
    }

    [Test]
    public async Task InCreative_HeNeverChoosesAProject_EvenWithItOn()
    {
        var (client, brain, log) = await JoinAsync("VertexCreative", selfDirected: true);
        await using var _c = client;
        await using var _b = brain;
        await _server.CommandAsync("gamemode creative VertexCreative");
        await Wait.UntilAsync(() => client.GameMode == GameMode.Creative, TimeSpan.FromSeconds(30), () => "game mode is " + client.GameMode);
        log.Clear();
        await Task.Delay(TimeSpan.FromSeconds(45));
        Assert.That(log.Any(l => l.Contains("choosing a project of my own")), Is.False, "the skills' rules are survival's: in creative he leaves it alone");
    }
}
