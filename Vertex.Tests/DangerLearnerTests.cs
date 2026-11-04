using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// He chooses what to do about monsters from what has happened to him (TJ, 2026-11-03: "figure out how to make him learn"). The learner itself with scripted outcomes; below, the choice driving a real body on a real
/// Paper server.
/// </summary>
public class DangerLearnerTests
{
    private static readonly DangerSituation BareMelee = new("melee", "bare", "high");
    private static readonly DangerAction[] All = [DangerAction.Fight, DangerAction.Flee, DangerAction.DigIn, DangerAction.SwimOut];

    private static int HowOften(DangerLearner learner, DangerSituation s, DangerAction a, int rounds = 500) => Enumerable.Range(0, rounds).Count(_ => learner.Choose(s, All) == a);

    [Test]
    public void BeforeAnyExperience_AGoodSwordFightsAndBareHandsMostlyDoNot()
    {
        var learner = new DangerLearner(random: new Random(1));
        Assert.That(learner.Expected(new DangerSituation("melee", "good", "high"), DangerAction.Fight), Is.GreaterThan(learner.Expected(new DangerSituation("melee", "good", "high"), DangerAction.Flee)));
        Assert.That(learner.Expected(BareMelee, DangerAction.Fight), Is.LessThan(learner.Expected(BareMelee, DangerAction.Flee)));
    }

    [Test]
    public void WhatKeepsKillingHim_StopsBeingChosen()
    {
        var learner = new DangerLearner(random: new Random(2));
        var before = HowOften(learner, new DangerSituation("melee", "good", "high"), DangerAction.Fight);
        // With a good weapon fighting starts as the favourite, and it does not work on this server: he dies each time.
        for (var i = 0; i < 10; i++) learner.Record(new DangerSituation("melee", "good", "high"), DangerAction.Fight, 0, true, 20, 0, 5);
        var after = HowOften(learner, new DangerSituation("melee", "good", "high"), DangerAction.Fight);
        TestContext.Out.WriteLine($"fight chosen {before} of 500 before, {after} of 500 after 10 deaths");
        Assert.That(before, Is.GreaterThan(200));
        Assert.That(after, Is.LessThan(25));
    }

    [Test]
    public void WhatWorks_GetsChosenMore_EvenAgainstThePrior()
    {
        var learner = new DangerLearner(random: new Random(3));
        var before = HowOften(learner, BareMelee, DangerAction.Fight);
        for (var i = 0; i < 12; i++) learner.Record(BareMelee, DangerAction.Fight, 1, false, 20, 20, 5);
        var after = HowOften(learner, BareMelee, DangerAction.Fight);
        TestContext.Out.WriteLine($"fight chosen {before} of 500 before, {after} of 500 after 12 wins");
        Assert.That(after, Is.GreaterThan(before + 150));
    }

    [Test]
    public void WhatHappenedInOneSituation_CarriesOverToSimilarOnes()
    {
        var learner = new DangerLearner(random: new Random(4));
        var similar = new DangerSituation("melee", "bare", "mid");
        var before = learner.Expected(similar, DangerAction.DigIn);
        for (var i = 0; i < 8; i++) learner.Record(BareMelee, DangerAction.DigIn, 1, false, 20, 20, 5);
        Assert.That(learner.Expected(similar, DangerAction.DigIn), Is.GreaterThan(before + 0.1), "same foe and kit, other health");
        var unrelated = new DangerSituation("archer", "good", "high");
        Assert.That(learner.Expected(unrelated, DangerAction.DigIn), Is.EqualTo(new DangerLearner().Expected(unrelated, DangerAction.DigIn)).Within(1e-9), "another foe is another matter");
    }

    [Test]
    public void ThePlayerStillTriesTheUnproven_NotOnlyTheBestSoFar()
    {
        var learner = new DangerLearner(random: new Random(5));
        for (var i = 0; i < 4; i++) learner.Record(BareMelee, DangerAction.Flee, 0.7, false, 20, 14, 5);
        Assert.That(HowOften(learner, BareMelee, DangerAction.SwimOut), Is.GreaterThan(25), "never tried: it still gets its chance");
    }

    [Test]
    public void OutcomesSurviveARestart_AndATornLineIsSkipped()
    {
        var file = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"danger-{Guid.NewGuid():N}.jsonl");
        try
        {
            var first = new DangerLearner(file);
            for (var i = 0; i < 5; i++) first.Record(BareMelee, DangerAction.Fight, 0, true, 20, 0, 3);
            var expected = first.Expected(BareMelee, DangerAction.Fight);
            File.AppendAllText(file, "{\"At\":\"2026-11-03T22:0");     // the process died while writing
            var second = new DangerLearner(file);
            Assert.That(second.Outcomes, Is.EqualTo(5));
            Assert.That(second.Expected(BareMelee, DangerAction.Fight), Is.EqualTo(expected).Within(1e-9));
        }
        finally { File.Delete(file); }
    }

    [Test]
    public void TheScore_DeathIsZero_KeepingHealthIsHigh_BeingCaughtOrFailingCosts()
    {
        Assert.That(DangerLearner.Reward(true, 20, 0, false, true), Is.EqualTo(0));
        Assert.That(DangerLearner.Reward(false, 20, 20, false, true), Is.EqualTo(1));
        Assert.That(DangerLearner.Reward(false, 20, 10, false, true), Is.EqualTo(0.25).Within(1e-9));
        Assert.That(DangerLearner.Reward(false, 20, 20, true, true), Is.EqualTo(0.6).Within(1e-9));
        Assert.That(DangerLearner.Reward(false, 20, 20, false, false), Is.EqualTo(0.5).Within(1e-9));
    }

    [Test]
    public void WhatHeSaysHeLearned_IsCounted()
    {
        var learner = new DangerLearner();
        Assert.That(learner.Summary(), Is.Empty, "nothing tried: nothing to say");
        for (var i = 0; i < 3; i++) learner.Record(BareMelee, DangerAction.Fight, 0, true, 20, 0, 3);
        Assert.That(learner.Summary(), Does.Contain("fighting has gone badly (3 tries)").And.Contain("bare hands"));
    }
}

/// <summary>The same on a real body: real water, real fire, a real zombie.</summary>
[NonParallelizable]
public class LiveDangerChoiceTests
{
    private const int Port = 25975;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;
    private int _nextStage;

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("danger", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "easy" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("gamerule randomTickSpeed 0");       // a cold-biome lake freezes otherwise (Docs/Testing.md)
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task Stop() { if (_server != null) await _server.DisposeAsync(); }

    /// <summary>Each test on its own stage, far from the last: a 41 by 41 stone floor at y 199 with him on it.</summary>
    private async Task<(MinecraftClient Client, Skills Skills, TaskRunner Tasks, int X, int Z)> JoinAsync(string name, params Func<int, int, string>[] extra)
    {
        var client = new MinecraftClient(GameProfile.Offline(name));
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        await _server.CommandAsync($"effect give {name} minecraft:resistance 30 4 true");
        var ax = 1000 + 200 * _nextStage++;
        const int az = 0;
        await _server.CommandAsync($"forceload add {ax - 48} {az - 48} {ax + 48} {az + 48}");
        await Task.Delay(1500);
        await _server.CommandAsync($"fill {ax - 20} 197 {az - 20} {ax + 20} 199 {az + 20} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 20} 200 {az - 20} {ax + 20} 203 {az + 20} minecraft:air");
        foreach (var build in extra) await _server.CommandAsync(build(ax, az));
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax - 20, az - 20) && client.World.IsLoaded(ax + 20, az + 20) && client.OnGround && client.FeetBlock.Y == 200, Timeout, () => $"on the floor, at {client.FeetBlock}");
        await client.WaitTicksAsync(20);
        await _server.CommandAsync($"effect clear {name}");
        await _server.CommandAsync($"effect give {name} minecraft:instant_health 1 10 true");
        await Wait.UntilAsync(() => client.Health >= 20f, Timeout, () => $"health {client.Health}");
        await client.WaitTicksAsync(70);      // a player who has just joined cannot be hurt for 3 seconds
        return (client, new Skills(client), new TaskRunner(), ax, az);
    }

    [Test]
    public async Task OnFire_HeRunsIntoTheWater_AndTheFireGoesOut()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("FireRun", (x, z) => $"fill {x + 5} 197 {z - 2} {x + 8} 199 {z + 2} minecraft:water");
        await using var _ = client;
        using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style) { Enabled = true };
        await _server.CommandAsync($"setblock {ax} 200 {az} minecraft:fire");
        await Wait.UntilAsync(() => client.OnFire, Timeout, () => "the fire to catch");
        var caught = DateTime.UtcNow;
        await Wait.UntilAsync(() => !client.OnFire, TimeSpan.FromSeconds(6), () => $"the fire to go out (he is at {client.FeetBlock}, on fire {client.OnFire})");
        var took = DateTime.UtcNow - caught;
        TestContext.Out.WriteLine($"out after {took.TotalSeconds:0.0} s, at {client.FeetBlock}, health {client.Health}");
        Assert.That(took.TotalSeconds, Is.LessThan(5), "standing still it burns for 8 seconds");
        Assert.That(client.FeetBlock.X, Is.GreaterThanOrEqualTo(ax + 4), "he went to the water");
    }

    [Test]
    public async Task WhenTheFireKeepsComingBack_HeStopsRunningToTheWater_AndGetsOnWithIt()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("FireAgain", (x, z) => $"fill {x + 5} 197 {z - 2} {x + 8} 199 {z + 2} minecraft:water");
        await using var _ = client;
        using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style);
        var said = new List<string>();
        reflexes.Decided += m => { lock (said) said.Add(m); };
        // Set alight again and again wherever he stands, as the plugin does.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var lighter = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var (x, y, z) = client.FeetBlock;
                if (!client.OnFire && client.World.GetBlock(x, y, z)?.Name != "minecraft:water")
                {
                    await _server.CommandAsync($"setblock {x} {y} {z} minecraft:fire");
                    await Task.Delay(300);
                    await _server.CommandAsync($"setblock {x} {y} {z} minecraft:air");
                }
                await Task.Delay(1000);
            }
        });
        await Wait.UntilAsync(() => { lock (said) return said.Any(s => s.Contains("keeps coming back")); }, TimeSpan.FromSeconds(50), () => $"giving up on the water (at {client.FeetBlock}, on fire {client.OnFire}, doing {tasks.Current})");
        await stop.CancelAsync();
        try { await lighter; } catch (OperationCanceledException) { }
    }

    [Test]
    public async Task TooHot_HeCoolsOffInTheWater_ThenGoesBackToHisJob()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("HotHead", (x, z) => $"fill {x + 5} 197 {z - 2} {x + 8} 199 {z + 2} minecraft:water");
        await using var _ = client;
        // What the plugin does, for real on the wire: the line on the action bar, hot on land and cooling in the water.
        double? shown = null;
        client.ChatReceived += m => { if (m.Kind == ChatKind.ActionBar && VertexBrain.TemperatureIn(m.Content) is double d) shown = d; };
        using var stop = new CancellationTokenSource();
        var degrees = 60.0;
        var plugin = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var (x, y, z) = client.FeetBlock;
                var wet = client.World.GetBlock(x, y, z)?.Name == "minecraft:water" || client.World.GetBlock(x, y - 1, z)?.Name == "minecraft:water";
                if (wet) degrees = Math.Max(30, degrees - 3);
                await _server.CommandAsync($"title HotHead actionbar {{\"text\":\"Temperature: {degrees:0.0} C\"}}");
                await Task.Delay(500);
            }
        });
        try
        {
            var started = 0;
            tasks.Start("a long job", async ct => { Interlocked.Increment(ref started); await Task.Delay(-1, ct); return (true, "done"); });
            using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style) { Temperature = () => shown };
            await Wait.UntilAsync(() => tasks.Current == "cooling off in the water", Timeout, () => $"going to cool off (shown {shown}, doing {tasks.Current})");
            await Wait.UntilAsync(() => client.FeetBlock.X >= ax + 5, Timeout, () => $"reaching the water, at {client.FeetBlock}");
            await Wait.UntilAsync(() => tasks.Current == "a long job", TimeSpan.FromSeconds(60), () => $"back to the job (shown {shown}, doing {tasks.Current})");
            Assert.That(degrees, Is.LessThanOrEqualTo(reflexes.HotAt - Reflexes.CoolBy + 3), "he stayed until he had cooled");
            Assert.That(Volatile.Read(ref started), Is.GreaterThanOrEqualTo(2), "the job restarted, from where things stand");
            tasks.Stop();
        }
        finally
        {
            await stop.CancelAsync();
            try { await plugin; } catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task HeSwimsOutToOpenWater_AndTreadsWater_WithoutDrowning()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("SwimOut",
            (x, z) => $"fill {x + 3} 196 {z - 20} {x + 32} 199 {z + 20} minecraft:water",
            (x, z) => $"fill {x + 33} 196 {z - 20} {x + 47} 199 {z + 20} minecraft:water");
        await using var _ = client;
        Assert.That(skills.FindOpenWater(), Is.Not.Null, "a lake 45 wide, right here");
        using var run = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await _server.CommandAsync("time set midnight");
        Task<(bool, string)>? job = skills.SwimOutAsync(run.Token);
        try { await job; } catch (OperationCanceledException) { }
        var feet = client.FeetBlock;
        // Not the in-water flag: treading water he bobs, and it flickers. The block he is in, or the one under him, is water.
        var inWaterColumn = new[] { feet.Y, feet.Y - 1 }.Any(y => client.World.GetBlock(feet.X, y, feet.Z)?.Name == "minecraft:water");
        TestContext.Out.WriteLine($"ended at {feet}, water here {inWaterColumn}, health {client.Health}");
        Assert.That(inWaterColumn, Is.True);
        Assert.That(feet.X, Is.GreaterThanOrEqualTo(ax + 11), "out from the shore");
        Assert.That(client.Health, Is.EqualTo(20f).Within(0.01f), "30-odd seconds in the water and no drowning: air runs out in 15");
        await _server.CommandAsync("time set day");
    }

    [Test]
    public async Task AChoiceIsMade_RunsOnTheBody_AndItsResultIsWrittenDown()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("Chooser");
        await using var _ = client;
        var file = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"danger-{Guid.NewGuid():N}.jsonl");
        try
        {
            var learner = new DangerLearner(file, new Random(7));
            using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style) { Learner = learner };
            var decisions = new List<string>();
            reflexes.Decided += m => { lock (decisions) decisions.Add(m); TestContext.Out.WriteLine(m); };
            await _server.CommandAsync($"summon minecraft:zombie {ax + 4} 200 {az}");
            await Wait.UntilAsync(() => { lock (decisions) return decisions.Any(d => d.StartsWith("result:")); }, TimeSpan.FromSeconds(120), () => "a choice and its result");
            Assert.That(File.ReadAllLines(file).Length, Is.GreaterThanOrEqualTo(1));
            Assert.That(learner.Outcomes, Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            await _server.CommandAsync("kill @e[type=minecraft:zombie]");
            File.Delete(file);
        }
    }

    [Test]
    public async Task HurtOnAnotherJob_HeDealsWithIt_AndGoesBackToTheJob()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("Busy");
        await using var _ = client;
        var learner = new DangerLearner(random: new Random(11));
        using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style) { Learner = learner };
        var decisions = new List<string>();
        reflexes.Decided += m => { lock (decisions) decisions.Add(m); };
        var started = 0;
        tasks.Start("a long job", async ct => { Interlocked.Increment(ref started); await Task.Delay(-1, ct); return (true, "done"); });
        await _server.CommandAsync($"summon minecraft:zombie {ax + 1} 200 {az}");
        await Wait.UntilAsync(() => { lock (decisions) return decisions.Count > 0; }, TimeSpan.FromSeconds(40), () => $"a choice once he was hit (health {client.Health})");
        await Wait.UntilAsync(() => Volatile.Read(ref started) >= 2, TimeSpan.FromSeconds(120), () => $"the job to resume (now: {tasks.Current})");
        Assert.That(tasks.Current, Is.EqualTo("a long job"));
        tasks.Stop();
        await _server.CommandAsync("kill @e[type=minecraft:zombie]");
    }
}
