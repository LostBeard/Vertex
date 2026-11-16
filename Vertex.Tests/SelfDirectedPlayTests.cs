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
    public void NoProjectOfHisOwn_InWater_OrWithAMonsterClose()
    {
        Assert.That(VertexBrain.SafeForOwnProject(inWater: false, hostileNear: false), Is.True);
        Assert.That(VertexBrain.SafeForOwnProject(inWater: true, hostileNear: false), Is.False, "standing in a river");
        Assert.That(VertexBrain.SafeForOwnProject(inWater: false, hostileNear: true), Is.False, "four creepers around him");
    }

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

    /// <param name="workUp">Whether he works his way up (tools, food, a bed...) when idle, as in the game. Off for the tests of choosing a project: the way up comes first and would keep him busy throughout.</param>
    private async Task<(MinecraftClient Client, VertexBrain Brain, ConcurrentQueue<string> Log)> JoinAsync(string name, bool selfDirected, bool workUp = false)
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
            WorkUpWhenIdle = workUp,
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

    /// <summary>Live on AubsCraft 2026-11-01: he stood in a river among four creepers and chose to build a platform. In water he chooses nothing; on dry land he does (the control).</summary>
    [Test]
    public async Task InWater_HeDoesNotChooseAProject_ButOnLandHeDoes()
    {
        var (client, brain, log) = await JoinAsync("VertexSwim", selfDirected: false);
        await using var _c = client;
        await using var _b = brain;
        brain.Reflexes.Enabled = false; // the reflex would climb out by itself; this is about the choosing
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - 8} 197 {az - 8} {ax + 8} 199 {az + 8} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 8} 200 {az - 8} {ax + 8} 206 {az + 8} minecraft:air");
        await _server.CommandAsync($"fill {ax - 3} 199 {az - 3} {ax + 3} 199 {az + 3} minecraft:water");
        await _server.CommandAsync($"tp VertexSwim {ax + 0.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.BodySnapshot().InWater, TimeSpan.FromSeconds(30), () => $"at {client.Position}");
        log.Clear();
        brain.SelfDirectedPlay = true;
        await Task.Delay(TimeSpan.FromSeconds(40));
        Assert.That(log.Any(l => l.Contains("choosing a project of my own")), Is.False, "in the water he chooses nothing: " + string.Join(" | ", log.TakeLast(5)));
        Assert.That(log.Any(l => l.Contains("not starting a project of my own: he is in the water")), Is.True, "and says so: " + string.Join(" | ", log.TakeLast(5)));

        await _server.CommandAsync($"tp VertexSwim {ax + 6.5} 200 {az + 6.5}");
        await Wait.UntilAsync(() => log.Any(l => l.Contains("choosing a project of my own")), Slow, () => "on dry land he never chose; in water " + client.BodySnapshot().InWater + "; log: " + string.Join(" | ", log.TakeLast(6)));
    }

    [Test]
    public void AtNight_TheProjectsAreCloseToHome_NoWanderingOffOverOpenGround()
    {
        Assert.That(string.Join(" | ", VertexBrain.OwnProjectIdeas(_ => 0, night: false)), Does.Contain("explore"));
        var night = string.Join(" | ", VertexBrain.OwnProjectIdeas(_ => 0, night: true));
        Assert.That(night, Does.Not.Contain("explore"), "at night he builds and gathers close by: " + night);
        Assert.That(night, Does.Contain("build").And.Contain("gather"));
    }

    /// <summary>TJ, 2026-11-03: "he should either stay busy at night or, if he is near a house he has built, he can sleep." Night, nothing to do, a bed 3 blocks away: he goes to bed by himself.</summary>
    [Test]
    public async Task AtNight_WithABedNear_HeGoesToSleepByHimself()
    {
        var (client, brain, log) = await JoinAsync("VertexSleepy", selfDirected: true);
        await using var _c = client;
        await using var _b = brain;
        brain.SelfDirectedIdle = TimeSpan.FromMinutes(5);       // as in the game: he waits 5 minutes to choose a project, and goes to bed after 20 seconds
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - 8} 197 {az - 8} {ax + 8} 199 {az + 8} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 8} 200 {az - 8} {ax + 8} 206 {az + 8} minecraft:air");
        await _server.CommandAsync($"setblock {ax + 3} 200 {az} minecraft:white_bed[facing=east,part=foot]");
        await _server.CommandAsync($"setblock {ax + 4} 200 {az} minecraft:white_bed[facing=east,part=head]");
        await _server.CommandAsync($"tp VertexSleepy {ax + 0.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.OnGround && client.FindBeds(48, 1).Count > 0, TimeSpan.FromSeconds(30), () => $"at {client.Position}");
        try
        {
            await _server.CommandAsync("time set 13500");
            await Wait.UntilAsync(() => log.Any(l => l.Contains("going to sleep")), TimeSpan.FromSeconds(90), () => "never went to bed; log: " + string.Join(" | ", log.TakeLast(6)));
            await Wait.UntilAsync(() => client.Sleeping, TimeSpan.FromSeconds(30), () => "not in bed; log: " + string.Join(" | ", log.TakeLast(6)));
        }
        finally
        {
            // Day again and the bed gone: the clock stands still on this server and every test stands at the same spot, so the next one (in the water, by day) found night and a bed, and went to bed.
            brain.SelfDirectedPlay = false;
            brain.Tasks.Stop();
            await _server.CommandAsync("time set day");
            await _server.CommandAsync($"fill {ax + 3} 200 {az} {ax + 4} 200 {az} minecraft:air");
        }
    }

    /// <summary>
    /// Live 2026-11-06 and 07: 106 of 151 deaths in 19 hours were at night, 38 of them on the way to a wooden pickaxe. Nothing tells him nights are dangerous: he finds out. Two deaths at night change
    /// nothing; from the third he leaves off what he was doing up there, digs in, and is still under the ground half a minute later; when day comes he is back up.
    /// </summary>
    [Test]
    public async Task AfterThreeDeathsAtNight_HeSpendsTheNightUnderground_ByHimself_AndComesUpByDay()
    {
        const string name = "VertexDark";
        var (client, brain, log) = await JoinAsync(name, selfDirected: true, workUp: true);
        await using var _c = client;
        await using var _b = brain;
        brain.SelfDirectedIdle = TimeSpan.FromMinutes(5);       // as in the game: no project of his own for 5 minutes; getting on in the world starts at once
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - 8} 190 {az - 8} {ax + 8} 199 {az + 8} minecraft:dirt");
        await _server.CommandAsync($"fill {ax - 8} 200 {az - 8} {ax + 8} 206 {az + 8} minecraft:air");
        var spawns = 0;
        client.Spawned += () => Interlocked.Increment(ref spawns);
        async Task ToTheStageAsync(int spawnsBefore)
        {
            // Back to life first: a teleport or a kill sent to someone still dead does nothing, and the test then waited on a death that never came.
            await Wait.UntilAsync(() => Volatile.Read(ref spawns) > spawnsBefore && client.IsSpawned && client.Health > 0, TimeSpan.FromSeconds(30), () => $"not back to life: health {client.Health} at {client.Position}");
            await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5}");
            await Wait.UntilAsync(() => client.OnGround && client.Health > 0 && client.FeetBlock.Y == 200 && Math.Abs(client.Position.X - ax - 0.5) < 3, TimeSpan.FromSeconds(30), () => $"at {client.Position}");
        }
        try
        {
            await _server.CommandAsync("time set 14000");
            await ToTheStageAsync(-1);
            for (var death = 1; death <= 3; death++)
            {
                if (death == 3)
                {
                    // Two deaths at night are not yet a rule: he is out working, and has not gone under.
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    Assert.That(brain.NightsKill, Is.False, "two deaths: not believed yet");
                    Assert.That(log.Any(l => l.Contains("going underground")), Is.False, "and he has not hidden: " + string.Join(" | ", log.TakeLast(6)));
                }
                var before = brain.DeathsThisRun;
                var spawnsBefore = Volatile.Read(ref spawns);
                await _server.CommandAsync($"kill {name}");
                await Wait.UntilAsync(() => brain.DeathsThisRun > before, TimeSpan.FromSeconds(30), () => $"death {death} was not noticed; log: " + string.Join(" | ", log.TakeLast(4)));
                await ToTheStageAsync(spawnsBefore);
            }
            Assert.That(brain.NightsKill, Is.True, "three deaths at night: " + string.Join(" | ", log.Where(l => l.Contains("learned")).TakeLast(4)));
            Assert.That(log.Any(l => l.Contains("learned from 3 deaths at night")), Is.True, "and he says what he learned");
            await Wait.UntilAsync(() => log.Any(l => l.Contains("going underground until morning")), TimeSpan.FromSeconds(60), () => "never went under; log: " + string.Join(" | ", log.TakeLast(8)));
            await Wait.UntilAsync(() => client.FeetBlock.Y <= 197, TimeSpan.FromSeconds(60), () => $"not down: at {client.Position}, doing {brain.Tasks.Current}; log: " + string.Join(" | ", log.TakeLast(8)));
            await Task.Delay(TimeSpan.FromSeconds(30));
            Assert.That(client.FeetBlock.Y, Is.LessThanOrEqualTo(197), $"still under the ground half a minute later (doing {brain.Tasks.Current}): " + string.Join(" | ", log.TakeLast(6)));
            Assert.That(brain.Tasks.Current, Is.EqualTo("spending the night underground"));
            Assert.That(client.Health, Is.GreaterThan(0));
            // Day: he comes up.
            await _server.CommandAsync("time set 500");
            await Wait.UntilAsync(() => client.FeetBlock.Y >= 200, TimeSpan.FromSeconds(90), () => $"not up: at {client.Position}, doing {brain.Tasks.Current}; log: " + string.Join(" | ", log.TakeLast(8)));
            Assert.That(log.Any(l => l.Contains("until morning")), Is.True, "and the job's ending says so: " + string.Join(" | ", log.TakeLast(8)));
        }
        finally
        {
            await _server.CommandAsync("time set day");
            // The earth goes again: every test here stands at the same spot, and left in place it gave the next one ground to dig down through for ten minutes (the bed test, which then never got to bed).
            brain.SelfDirectedPlay = false;
            brain.Tasks.Stop();
            await _server.CommandAsync($"fill {ax - 8} 190 {az - 8} {ax + 8} 199 {az + 8} minecraft:air");
        }
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
        Assert.That(log.Any(l => l.Contains("not starting a project of my own: the game mode is creative, not survival")), Is.True,
            "and he says why (2026-11-03: idle for minutes with no way to tell what stopped him): " + string.Join(" | ", log.TakeLast(5)));
    }
}
