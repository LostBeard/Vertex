using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using SpawnDev.Minecraft.Pathing;
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
    public async Task InTheHeat_HeWorksWhileHisHealthHolds_ThenFindsWhereItIsCool_HealsAndGoesBack()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("HotHead");
        await using var _ = client;
        // What the plugin does on mc.spawndev.com, for real on the wire: the line on the action bar goes with the place. Only the far side of this floor is cool, and nothing marks it: he has to find it.
        double? shown = null;
        client.ChatReceived += m => { if (m.Kind == ChatKind.ActionBar && VertexBrain.TemperatureIn(m.Content) is double d) { shown = d; skills.NoteHeat(d); } };
        using var stop = new CancellationTokenSource();
        var plugin = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var degrees = client.FeetBlock.X <= ax - 12 ? 25.0 : 56.6;
                await _server.CommandAsync($"title HotHead actionbar {{\"text\":\"Temperature: {degrees:0.0} C\"}}");
                await Task.Delay(500);
            }
        });
        try
        {
            var started = 0;
            tasks.Start("a long job", async ct => { Interlocked.Increment(ref started); await Task.Delay(-1, ct); return (true, "done"); });
            using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style) { Temperature = () => shown };
            await Wait.UntilAsync(() => shown >= 50, Timeout, () => "the heat to show");
            await client.WaitTicksAsync(60);
            Assert.That(tasks.Current, Is.EqualTo("a long job"), "at full health he keeps working in it");

            await _server.CommandAsync("damage HotHead 12 minecraft:generic");
            await Wait.UntilAsync(() => tasks.Current == "cooling off", Timeout, () => $"going to cool off (health {client.Health}, doing {tasks.Current})");
            await Wait.UntilAsync(() => client.FeetBlock.X <= ax - 12, TimeSpan.FromSeconds(90), () => $"finding the cool side, at {client.FeetBlock}, shown {shown}");
            TestContext.Out.WriteLine($"found it at {client.FeetBlock}");
            await Wait.UntilAsync(() => skills.NearestCool(44) != null, Timeout, () => "the place on his map, at the next reading");
            await Wait.UntilAsync(() => tasks.Current == "a long job", TimeSpan.FromSeconds(120), () => $"healing and back to the job (health {client.Health}, doing {tasks.Current})");
            // Natural healing stops under 18 food, and he has nothing to eat here: then a short rest is all there is to wait for.
            Assert.That(client.Health >= Reflexes.HealTo || client.Food < 18, Is.True, $"he stayed until he had healed, or could heal no more (health {client.Health}, food {client.Food})");
            Assert.That(client.Health, Is.GreaterThan(8f), "the 12 points of damage left him 8: he healed some");
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
    public async Task ALandThatBurnsHim_HeLearnsItIsTheBiome_AndLeavesForLandThatDoesNot()
    {
        // Real biomes in the world, real readings on the wire; the only thing faked is that this test server has no temperature plugin, so the test says the number the way it does: by the biome under him. Nothing tells him that rule.
        var (client, skills, tasks, ax, az) = await JoinAsync("Leaver",
            (x, z) => $"fillbiome {x - 20} 196 {z - 20} {x + 12} 206 {z + 20} minecraft:desert",      // a fillbiome counts blocks, and past 32,768 it does nothing but say so on the console
            (x, z) => $"fill {x + 13} 197 {z - 20} {x + 44} 199 {z + 20} minecraft:stone",
            (x, z) => $"fill {x + 13} 200 {z - 20} {x + 44} 203 {z + 20} minecraft:air",
            (x, z) => $"fillbiome {x + 13} 196 {z - 20} {x + 44} 206 {z + 20} minecraft:plains");
        await using var _ = client;
        await _server.CommandAsync("give Leaver minecraft:wooden_pickaxe");       // with tools he has no need of trees here: any land that does not burn him will do
        await Wait.UntilAsync(() => client.Inventory.Count("wooden_pickaxe") > 0, Timeout, () => "the pickaxe");
        Assert.That(skills.BiomeHere, Is.EqualTo("desert"), "he can read the biome he stands in");
        double? shown = null;
        client.ChatReceived += m => { if (m.Kind == ChatKind.ActionBar && VertexBrain.TemperatureIn(m.Content) is double d) { shown = d; skills.NoteHeat(d); } };
        using var stop = new CancellationTokenSource();
        var plugin = Task.Run(async () =>
        {
            // Once a second, for where he is (the plugin, read by Geordi 2026-11-04).
            while (!stop.IsCancellationRequested)
            {
                var degrees = skills.BiomeHere == "desert" ? 56.6 : 25.0;
                await _server.CommandAsync($"title Leaver actionbar {{\"text\":\"Temperature: {degrees:0.0} C\"}}");
                await Task.Delay(1000);
            }
        });
        try
        {
            skills.HotAt = () => 50;
            await Wait.UntilAsync(() => skills.KnownTooHot("desert", 50), TimeSpan.FromSeconds(40), () => "the reading on his map, after he has been in the biome a while");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var (ok, text) = await skills.FindLivableLandAsync(() => shown, 50, cts.Token);
            TestContext.Out.WriteLine(text);
            Assert.That(ok, Is.True, text);
            Assert.That(skills.BiomeHere, Is.EqualTo("plains"), $"at {client.FeetBlock}");
            Assert.That(skills.KnownTooHot("plains", 50), Is.False);
            // The reading that followed him across (the number lags) did not get pinned on the plains. And he remembers where it was cool.
            Assert.That(skills.BiomeHeat().Single(b => b.Biome == "plains").Degrees, Is.LessThan(50));
            Assert.That(skills.NearestCoolLandKnown(), Is.Not.Null);
        }
        finally
        {
            await stop.CancelAsync();
            try { await plugin; } catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task ALongSwim_IsOneSteadySwim_NotThreeBlocksAndStop()
    {
        // TJ, watching live 2026-11-04: "he swims about 3 blocks. stops. swims 3 blocks and stops." A lake 90 blocks long, and his speed sampled ten times a second over the swim.
        var name = "Swimmer";
        var logs = new List<string>();
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => { lock (logs) logs.Add($"{DateTime.UtcNow:mm:ss.f} {m}"); } });
        await using var _ = client;
        using var cts0 = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts0.Token);
        await _server.CommandAsync($"effect give {name} minecraft:resistance 200 4 true");
        var ax = 1000 + 200 * _nextStage++;
        const int az = 0;
        await _server.CommandAsync($"forceload add {ax - 16} {az - 16} {ax + 112} {az + 16}");
        await Task.Delay(1500);
        await _server.CommandAsync($"fill {ax - 4} 190 {az - 8} {ax + 100} 199 {az + 8} minecraft:water");
        await _server.CommandAsync($"fill {ax - 4} 189 {az - 8} {ax + 100} 189 {az + 8} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 4} 200 {az - 8} {ax + 100} 204 {az + 8} minecraft:air");
        await _server.CommandAsync($"fill {ax + 101} 195 {az - 8} {ax + 110} 199 {az + 8} minecraft:stone");
        await _server.CommandAsync($"fill {ax + 101} 200 {az - 8} {ax + 110} 204 {az + 8} minecraft:air");
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax + 60, az) && Math.Abs(client.Position.X - ax) < 2, Timeout, () => $"at the lake, {client.FeetBlock}");
        await client.WaitTicksAsync(40);

        var samples = new List<(double T, double X)>();
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!stop.IsCancellationRequested)
            {
                lock (samples) samples.Add((clock.Elapsed.TotalSeconds, client.Position.X));
                await Task.Delay(100);
            }
        });
        using var run = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ok = await client.GoToAsync(SpawnDev.Minecraft.Pathing.Goal.XZ(ax + 60, az), new SpawnDev.Minecraft.Pathing.PathOptions { MaxNodes = 15_000 }, run.Token);
        await stop.CancelAsync();
        await sampler;
        var pauses = 0;
        var longest = 0.0;
        var still = 0.0;
        List<(double T, double X)> s;
        lock (samples) s = samples.ToList();
        for (var i = 1; i < s.Count; i++)
        {
            if (Math.Abs(s[i].X - s[i - 1].X) < 0.02) { still += s[i].T - s[i - 1].T; longest = Math.Max(longest, still); }
            else { if (still >= 0.4) pauses++; still = 0; }
        }
        var seconds = s[^1].T - s[0].T;
        var speed = (s[^1].X - s[0].X) / seconds;
        lock (logs) foreach (var line in logs.Where(l => l.Contains("path")).Take(25)) TestContext.Out.WriteLine(line);
        TestContext.Out.WriteLine($"reached {ok}, {s[^1].X - s[0].X:0.0} blocks in {seconds:0.0} s = {speed:0.00} blocks/s; {pauses} pauses of 0.4 s or more, longest {longest:0.0} s");
        Assert.That(ok, Is.True, $"ended at {client.FeetBlock}");
        Assert.That(pauses, Is.LessThanOrEqualTo(1), "a swim does not stop and start");
        Assert.That(speed, Is.GreaterThan(1.8), "a player swims about 2.2 blocks a second on the surface");

        // Out in the middle of it he knows he is at sea, and he goes ashore: the only land is the stone at the far end, 41 blocks on. This server shows no temperature, so there is nothing to wait for once he is there.
        var skills = new Skills(client);
        await _server.CommandAsync($"give {name} minecraft:wooden_pickaxe");      // any land will do for this one
        await Wait.UntilAsync(() => client.Inventory.Count("wooden_pickaxe") > 0, Timeout, () => "the pickaxe");
        Assert.That(skills.AtSea, Is.True, $"at {client.FeetBlock}");
        using var ashore = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (landed, text) = await skills.FindLivableLandAsync(() => null, 50, ashore.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(landed, Is.True, text);
        Assert.That(client.FeetBlock.X, Is.GreaterThanOrEqualTo(ax + 101), "on the far shore");
        Assert.That(client.FeetBlock.Y, Is.EqualTo(200));
        Assert.That(skills.AtSea, Is.False);
    }

    [Test]
    public async Task BurnedLowOnAMilestone_HeLeavesTheHeatWhetherOrNotHeCanHeal_AndDoesNotGoBackToIt()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("Leaves");
        await using var _ = client;
        double? shown = null;
        client.ChatReceived += m => { if (m.Kind == ChatKind.ActionBar && VertexBrain.TemperatureIn(m.Content) is double d) { shown = d; skills.NoteHeat(d); } };
        using var stop = new CancellationTokenSource();
        var plugin = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await _server.CommandAsync($"title Leaves actionbar {{\"text\":\"Temperature: {(client.FeetBlock.X <= ax - 12 ? 25.0 : 56.6):0.0} C\"}}");
                await Task.Delay(500);
            }
        });
        try
        {
            // Food under 18 with nothing to eat, as he always is by then: healing has stopped.
            await _server.CommandAsync("effect give Leaves minecraft:hunger 60 200 true");
            await Wait.UntilAsync(() => client.Food < 17, TimeSpan.FromSeconds(40), () => $"the food bar to fall (food {client.Food})");
            await _server.CommandAsync("effect clear Leaves");
            var started = 0;
            tasks.StartReflex("working toward a wooden pickaxe", async ct => { Interlocked.Increment(ref started); await Task.Delay(-1, ct); return (true, "done"); });
            using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style) { Temperature = () => shown };
            await _server.CommandAsync("damage Leaves 12 minecraft:generic");
            await Wait.UntilAsync(() => tasks.Current == "cooling off", Timeout, () => $"going to cool off (health {client.Health}, food {client.Food}, doing {tasks.Current})");
            await Wait.UntilAsync(() => client.FeetBlock.X <= ax - 12, TimeSpan.FromSeconds(60), () => $"out of the heat, at {client.FeetBlock}");
            await Wait.UntilAsync(() => tasks.Current != "cooling off", TimeSpan.FromSeconds(60), () => "the short rest to end");
            Assert.That(Volatile.Read(ref started), Is.EqualTo(1), "the milestone is not restarted: what comes next gets chosen afresh");
            Assert.That(tasks.Current, Is.Null);
        }
        finally
        {
            await stop.CancelAsync();
            try { await plugin; } catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task HurtAndHungry_HeEatsWhileWorking_AndWithNothingToEatFoodComesBeforeTools()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("Hungry");
        await using var _ = client;
        // Real hunger: the effect burns the bar down until natural healing has stopped.
        await _server.CommandAsync("effect give Hungry minecraft:hunger 60 200 true");
        await Wait.UntilAsync(() => client.Food < 17, TimeSpan.FromSeconds(40), () => $"the food bar to fall (food {client.Food})");
        await _server.CommandAsync("effect clear Hungry");
        await _server.CommandAsync("damage Hungry 10 minecraft:generic");
        await Wait.UntilAsync(() => client.Health < 14, Timeout, () => $"the hurt (health {client.Health})");
        Assert.That(skills.NextMilestone()?.Item, Is.EqualTo("food"), "he cannot heal and has nothing to eat: tools can wait");

        await _server.CommandAsync("give Hungry minecraft:bread 8");
        await Wait.UntilAsync(() => skills.HasFood, Timeout, () => "the bread");
        var foodBefore = client.Food;
        var started = 0;
        tasks.StartReflex("working toward a stone pickaxe", async ct => { Interlocked.Increment(ref started); await Task.Delay(-1, ct); return (true, "done"); });
        using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style);
        await Wait.UntilAsync(() => client.Food > foodBefore, TimeSpan.FromSeconds(30), () => $"eating (food {client.Food}, doing {tasks.Current})");
        await Wait.UntilAsync(() => tasks.Current == "working toward a stone pickaxe" && Volatile.Read(ref started) >= 2, TimeSpan.FromSeconds(30), () => $"back to the job (doing {tasks.Current})");
        tasks.Stop();
    }

    [Test]
    public async Task AtTwoHearts_HeStillHuntsACow()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("LowHunter", (x, z) => $"summon minecraft:cow {x + 4} 200 {z} {{NoAI:1b}}");
        await using var _ = client;
        await _server.CommandAsync("damage LowHunter 16 minecraft:generic");
        await Wait.UntilAsync(() => client.Health <= 4f, Timeout, () => $"the hurt (health {client.Health})");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (ok, text) = await skills.ObtainAsync("beef", 1, cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);
        Assert.That(client.Inventory.Count("beef"), Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task UnderWaterOnAJob_HeComesUpForAirBeforeHeDrowns_AndGoesBackToIt()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("Diver",
            (x, z) => $"fill {x + 4} 186 {z - 4} {x + 12} 199 {z + 4} minecraft:water",
            (x, z) => $"fill {x + 4} 185 {z - 4} {x + 12} 185 {z + 4} minecraft:stone");
        await using var _ = client;
        var hurts = new List<string>();
        client.Damaged += d => { lock (hurts) hurts.Add(d.Type); };
        var started = 0;
        tasks.Start("a long job", async ct => { Interlocked.Increment(ref started); await Task.Delay(-1, ct); return (true, "done"); });
        using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style);
        await _server.CommandAsync($"tp Diver {ax + 8.5} 188 {az + 0.5}");
        await Wait.UntilAsync(() => client.FeetBlock.Y < 195, Timeout, () => $"going under, at {client.FeetBlock}");
        await Wait.UntilAsync(() => client.Air < 280, Timeout, () => $"the breath to fall ({client.Air})");
        await Wait.UntilAsync(() => tasks.Current == "coming up for air", Timeout, () => $"coming up (air {client.Air}, doing {tasks.Current})");
        await Wait.UntilAsync(() => Volatile.Read(ref started) >= 2, TimeSpan.FromSeconds(40), () => $"his breath back and the job again (air {client.Air}, at {client.FeetBlock}, doing {tasks.Current})");
        Assert.That(client.Air, Is.GreaterThan(250));
        lock (hurts) Assert.That(hurts, Does.Not.Contain("drown"), "he never got as far as drowning");
        tasks.Stop();
    }

    /// <summary>Teaches him what a place reads: he stands there and it shows, as the plugin does, once a second.</summary>
    private async Task TeachHeatAsync(MinecraftClient client, Skills skills, string name, double x, double z, double degrees)
    {
        await _server.CommandAsync($"tp {name} {x} 200 {z}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.X - x) < 1 && Math.Abs(client.Position.Z - z) < 1, Timeout, () => $"at {client.FeetBlock}");
        for (var i = 0; i < 5; i++) { skills.NoteHeat(degrees); await Task.Delay(1000); }
    }

    [Test]
    public async Task HisPaths_GoRoundLandHeKnowsBurns_AndStraightOverItWhenNothingDoes()
    {
        var (client, skills, _, ax, az) = await JoinAsync("Rounder",
            (x, z) => $"fillbiome {x - 20} 196 {z - 20} {x + 20} 206 {z + 20} minecraft:plains",
            (x, z) => $"fillbiome {x + 6} 196 {z - 20} {x + 10} 206 {z + 10} minecraft:desert");
        await using var _ = client;
        var start = (ax + 0, 200, az);
        var goal = Goal.XZ(ax + 16, az);
        bool OverIt(List<PathStep> path) => path.Any(s => s.X >= ax + 6 && s.X <= ax + 10 && s.Z <= az + 10);
        var options = new PathOptions { MaxNodes = 20_000 };

        var (straight, _) = client.Pathfinder.FindPath(start, goal, options);
        Assert.That(OverIt(straight), Is.True, "with nothing learned it is the straight way");

        skills.HotAt = () => 50;
        await TeachHeatAsync(client, skills, "Rounder", ax + 8.5, az + 0.5, 56.6);
        Assert.That(skills.KnownTooHot("desert", 50), Is.True);
        await TeachHeatAsync(client, skills, "Rounder", ax + 0.5, az + 0.5, 25);
        skills.NoteHeat(47);        // one at the uneven edge of a biome says nothing about it
        Assert.That(skills.KnownTooHot("plains", 40), Is.False, "the middle reading, not the highest");

        var (round, complete) = client.Pathfinder.FindPath(start, goal, options);
        Assert.That(complete, Is.True);
        Assert.That(OverIt(round), Is.False, "the way round: " + string.Join(" ", round.Select(s => $"{s.X - ax},{s.Z - az}")));
        skills.HotAt = null;
    }

    [Test]
    public async Task TooHot_HeLeavesTheBiome_RatherThanStoppingInTheNearestWater()
    {
        var (client, skills, _, ax, az) = await JoinAsync("Leaves",
            (x, z) => $"fillbiome {x - 20} 196 {z - 20} {x + 20} 206 {z + 20} minecraft:desert",
            (x, z) => $"fillbiome {x + 18} 196 {z - 20} {x + 20} 206 {z + 20} minecraft:plains",
            (x, z) => $"fill {x + 49} 197 {z - 20} {x + 21} 199 {z + 20} minecraft:stone",
            (x, z) => $"fill {x + 49} 200 {z - 20} {x + 21} 203 {z + 20} minecraft:air",
            (x, z) => $"fillbiome {x + 21} 196 {z - 20} {x + 49} 206 {z + 20} minecraft:plains",
            (x, z) => $"fill {x + 4} 197 {z - 2} {x + 8} 199 {z + 2} minecraft:water");
        await using var _ = client;
        skills.Trace += m => TestContext.Out.WriteLine(m);
        skills.HotAt = () => 50;
        await TeachHeatAsync(client, skills, "Leaves", ax + 38.5, az + 0.5, 25);
        await TeachHeatAsync(client, skills, "Leaves", ax + 0.5, az + 0.5, 56.6);
        // Hot wherever the biome is, water or no water, as the plugin does.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (ok, said) = await skills.CoolOffAsync(() => skills.BiomeHere == "desert" ? 56.6 : 25.0, 37.6, 18, cts.Token);
        TestContext.Out.WriteLine(said);
        Assert.That(ok, Is.True, said);
        Assert.That(skills.BiomeHere, Is.EqualTo("plains"));
        Assert.That(client.FeetBlock.X, Is.GreaterThanOrEqualTo(ax + 16), "past the pool, out in the plains (biomes come in cells of 4: the plains begin at +16)");
        skills.HotAt = null;
    }

    [Test]
    public async Task LitByTheHeat_HeDoesNotRunToTheWater()
    {
        // The same pool as OnFire_HeRunsIntoTheWater: reading too hot, the water is no use.
        var (client, skills, tasks, ax, az) = await JoinAsync("HotFire", (x, z) => $"fill {x + 5} 197 {z - 2} {x + 8} 199 {z + 2} minecraft:water");
        await using var _ = client;
        using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style) { Enabled = true, Temperature = () => 56.6 };
        var went = false;
        using var stop = new CancellationTokenSource();
        var watch = Task.Run(async () => { while (!stop.IsCancellationRequested) { if (tasks.Current == "putting out the fire") went = true; await Task.Delay(20); } });
        await _server.CommandAsync($"setblock {ax} 200 {az} minecraft:fire");
        await Wait.UntilAsync(() => client.OnFire, Timeout, () => "the fire to catch");
        await Task.Delay(3000);
        stop.Cancel();
        await watch;
        Assert.That(went, Is.False, "the water does nothing about the heat that lit him");
        tasks.Stop();
    }

    [Test]
    public async Task UnderARoofInAFloodedCave_HeSwimsToTheAirThereIs()
    {
        // Two-high flooded tunnel in solid rock, open water nowhere over him; a pocket of air six or so blocks along. Straight up is all he used to know: he drowned like this in iron tools, live.
        var (client, skills, tasks, ax, az) = await JoinAsync("Caver",
            (x, z) => $"fill {x + 2} 183 {z - 4} {x + 18} 199 {z + 4} minecraft:stone",
            (x, z) => $"fill {x + 4} 187 {z - 1} {x + 11} 188 {z + 1} minecraft:water",
            (x, z) => $"fill {x + 12} 187 {z - 1} {x + 14} 191 {z + 1} minecraft:air");
        await using var _ = client;
        var hurts = new List<string>();
        client.Damaged += d => { lock (hurts) hurts.Add(d.Type); };
        using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style);
        skills.Trace += m => TestContext.Out.WriteLine(m);
        await _server.CommandAsync($"tp Caver {ax + 5.5} 187 {az + 0.5}");
        await Wait.UntilAsync(() => client.FeetBlock.Y == 187 && client.Air < 280, Timeout, () => $"going under, at {client.FeetBlock}, air {client.Air}");
        await Wait.UntilAsync(() => tasks.Current == "coming up for air", Timeout, () => $"coming up (air {client.Air}, doing {tasks.Current})");
        await Wait.UntilAsync(() => client.Air >= 280, TimeSpan.FromSeconds(30), () => $"breath back (air {client.Air}, at {client.FeetBlock}, doing {tasks.Current})");
        Assert.That(client.FeetBlock.X, Is.GreaterThanOrEqualTo(ax + 11), "he went to the pocket");
        lock (hurts) Assert.That(hurts, Does.Not.Contain("drown"));
        tasks.Stop();
    }

    [Test]
    public async Task WithNoAirInReach_HeDigsUpThroughTheRoof()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("Roofed",
            (x, z) => $"fill {x + 2} 183 {z - 4} {x + 12} 199 {z + 4} minecraft:stone",
            (x, z) => $"fill {x + 4} 187 {z - 2} {x + 8} 188 {z + 2} minecraft:water",
            (x, z) => $"fill {x + 4} 190 {z - 2} {x + 8} 192 {z + 2} minecraft:air");
        await using var _ = client;
        await _server.CommandAsync("give Roofed minecraft:iron_pickaxe");
        await Wait.UntilAsync(() => skills.InventorySummary().Contains("iron pickaxe"), Timeout, () => skills.InventorySummary());
        var hurts = new List<string>();
        client.Damaged += d => { lock (hurts) hurts.Add(d.Type); };
        using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style);
        skills.Trace += m => TestContext.Out.WriteLine(m);
        await _server.CommandAsync($"tp Roofed {ax + 6.5} 187 {az + 0.5}");
        await Wait.UntilAsync(() => client.FeetBlock.Y == 187 && client.Air < 280, Timeout, () => $"going under, at {client.FeetBlock}, air {client.Air}");
        await Wait.UntilAsync(() => client.Air >= 280, TimeSpan.FromSeconds(30), () => $"breath back (air {client.Air}, at {client.FeetBlock}, doing {tasks.Current})");
        Assert.That(client.World.GetState(ax + 6, 189, az), Is.EqualTo(client.World.GetState(ax + 6, 190, az)), "the roof over him is gone");
        lock (hurts) Assert.That(hurts, Does.Not.Contain("drown"));
        tasks.Stop();
    }

    [Test]
    public async Task HeDies_AndGoesBackForHisThings_AndWithNothingOnHimThereIsNothingToGoBackFor()
    {
        var (client, skills, _, ax, az) = await JoinAsync("Fetcher");
        await using var __ = client;
        skills.Trace += m => TestContext.Out.WriteLine(m);
        client.Died += _ => { var (x, y, z) = client.Position; skills.NoteDiedHere(x, y, z); };
        var spawned = 0;
        client.Spawned += () => Interlocked.Increment(ref spawned);

        // Nothing on him: nothing to fetch.
        skills.NoteKit();
        Assert.That(skills.ThingsToFetch, Is.False);

        await _server.CommandAsync("give Fetcher minecraft:iron_pickaxe");
        await _server.CommandAsync("give Fetcher minecraft:cobblestone 12");
        await Wait.UntilAsync(() => skills.InventorySummary().Contains("iron pickaxe"), Timeout, () => skills.InventorySummary());
        skills.NoteKit();
        await _server.CommandAsync($"tp Fetcher {ax + 15.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.FeetBlock.X == ax + 15, Timeout, () => $"at {client.FeetBlock}");
        await _server.CommandAsync("damage Fetcher 100 minecraft:arrow");
        await Wait.UntilAsync(() => Volatile.Read(ref spawned) > 0 && client.Health >= 20f, Timeout, () => $"the respawn (health {client.Health})");
        Assert.That(skills.ThingsToFetch, Is.True);
        Assert.That(skills.InventorySummary(), Does.Not.Contain("iron pickaxe"));
        // Comes back on the far side of the stage, 30 blocks off: a hop first, then the drops.
        await _server.CommandAsync($"tp Fetcher {ax - 15.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.FeetBlock.X == ax - 16 && client.OnGround, Timeout, () => $"at {client.FeetBlock}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var (ok, said) = await skills.FetchThingsAsync(cts.Token);
        TestContext.Out.WriteLine(said);
        Assert.That(ok, Is.True, said);
        Assert.That(skills.InventorySummary(), Does.Contain("iron pickaxe"));
        Assert.That(skills.InventorySummary(), Does.Contain("cobblestone"));
        Assert.That(skills.ThingsToFetch, Is.False, "once is enough");
    }

    [Test]
    public async Task WithNoTools_HeDoesNotSettleOnBareCoolGround_HeGoesOnToCoolTrees()
    {
        // From burning desert with a tree in it, past cool bare ground, to a cool tree further on. The nearest wood burns; the nearest cool ground has none.
        var (client, skills, tasks, ax, az) = await JoinAsync("WoodFirst",
            (x, z) => $"forceload add {x - 24} {z - 24} {x + 72} {z + 24}",
            (x, z) => $"fillbiome {x - 20} 196 {z - 20} {x + 12} 206 {z + 20} minecraft:desert",
            (x, z) => $"fill {x + 13} 197 {z - 20} {x + 60} 199 {z + 20} minecraft:stone",
            (x, z) => $"fill {x + 13} 200 {z - 20} {x + 60} 203 {z + 20} minecraft:air",
            (x, z) => $"fillbiome {x + 13} 196 {z - 20} {x + 60} 206 {z + 20} minecraft:plains",
            (x, z) => $"fill {x - 8} 200 {z} {x - 8} 203 {z} minecraft:oak_log",
            (x, z) => $"fill {x + 50} 200 {z + 2} {x + 50} 203 {z + 2} minecraft:oak_log",
            // A wreck on the way: a log on the bottom of a pool, nearer than the real tree, in the cool. Live he sat over one for 15 minutes.
            (x, z) => $"fill {x + 18} 196 {z - 4} {x + 24} 199 {z + 4} minecraft:water",
            (x, z) => $"setblock {x + 21} 196 {z} minecraft:oak_log");
        await using var _ = client;
        skills.Trace += m => TestContext.Out.WriteLine(m);
        double? shown = null;
        client.ChatReceived += m => { if (m.Kind == ChatKind.ActionBar && VertexBrain.TemperatureIn(m.Content) is double d) { shown = d; skills.NoteHeat(d); } };
        using var stop = new CancellationTokenSource();
        var plugin = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await _server.CommandAsync($"title WoodFirst actionbar {{\"text\":\"Temperature: {(skills.BiomeHere == "desert" ? 56.6 : 25.0):0.0} C\"}}");
                await Task.Delay(500);
            }
        });
        try
        {
            await Wait.UntilAsync(() => skills.KnownTooHot("desert", 50), TimeSpan.FromSeconds(40), () => "the desert's reading on his map");
            Assert.That(skills.NearestCoolWood(50)?.X, Is.EqualTo(ax + 50), "the tree in the desert is nearer and does not count, nor does the log under water");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var (ok, text) = await skills.FindLivableLandAsync(() => shown, 50, cts.Token);
            TestContext.Out.WriteLine(text);
            Assert.That(ok, Is.True, text);
            Assert.That(client.FeetBlock.X, Is.GreaterThanOrEqualTo(ax + 34), $"past the bare ground, to the tree; at {client.FeetBlock}");
        }
        finally
        {
            await stop.CancelAsync();
            try { await plugin; } catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task Buried_HeDigsHimselfOut_AndGoesBackToHisJob()
    {
        var (client, skills, tasks, ax, az) = await JoinAsync("Buried");
        await using var _ = client;
        var started = 0;
        tasks.Start("a long job", async ct => { Interlocked.Increment(ref started); await Task.Delay(-1, ct); return (true, "done"); });
        using var reflexes = new Reflexes(client, skills, tasks, () => Character.BuiltIn[0].Style);
        // Sand round his head and feet, held up by nothing falling: the fill puts it right where he stands.
        await _server.CommandAsync($"fill {ax - 1} 200 {az - 1} {ax + 1} 202 {az + 1} minecraft:sand");
        await Wait.UntilAsync(() => client.World.GetBlock(ax, 201, az)?.Name == "minecraft:sand", Timeout, () => "the sand to land on him");
        await Wait.UntilAsync(() => client.World.GetBlock(ax, 201, az)?.Name != "minecraft:sand", TimeSpan.FromSeconds(20), () => $"digging out (health {client.Health}, doing {tasks.Current})");
        await Wait.UntilAsync(() => Volatile.Read(ref started) >= 2, TimeSpan.FromSeconds(30), () => $"back to the job (doing {tasks.Current})");
        Assert.That(client.Health, Is.GreaterThan(4f), "he got out with most of his health: it costs a heart a second");
        tasks.Stop();
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
        // Something on the shore keeps him out there for the length of the test: nothing near, and he comes back to work after a few seconds.
        await _server.CommandAsync($"summon minecraft:zombie {ax - 2} 200 {az} {{NoAI:1b}}");
        Task<(bool, string)>? job = skills.SwimOutAsync(run.Token);
        try { await job; } catch (OperationCanceledException) { }
        var feet = client.FeetBlock;
        // Not the in-water flag: treading water he bobs, and it flickers. The block he is in, or the one under him, is water.
        var inWaterColumn = new[] { feet.Y, feet.Y - 1 }.Any(y => client.World.GetBlock(feet.X, y, feet.Z)?.Name == "minecraft:water");
        TestContext.Out.WriteLine($"ended at {feet}, water here {inWaterColumn}, health {client.Health}");
        Assert.That(inWaterColumn, Is.True);
        Assert.That(feet.X, Is.GreaterThanOrEqualTo(ax + 11), "out from the shore");
        Assert.That(client.Health, Is.EqualTo(20f).Within(0.01f), "30-odd seconds in the water and no drowning: air runs out in 15");
        await _server.CommandAsync("kill @e[type=minecraft:zombie]");
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
