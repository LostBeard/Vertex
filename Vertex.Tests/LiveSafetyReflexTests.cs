using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Entities;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Live on AubsCraft, 2026-11-01 (TJ, watching): "he is standing in the water doing nothing. he is surrounded by 4 creepers". His only creeper reflex was for one whose fuse was already lit,
/// and nothing told him to leave water. Idle, he now walks away from a monster that is close (creepers and anything that has not hurt him: no picking fights) and out of water. Real Paper
/// server; each test first proves, with the reflexes OFF, that he stays where he is, so it cannot pass by itself.
/// </summary>
[NonParallelizable]
public class LiveSafetyReflexTests
{
    private const int Port = 25988;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;
    private readonly List<(int X, int Z)> _stages = [];

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("safety", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "easy", ["allow-flight"] = "true" }); // not peaceful: that removes hostile mobs at once; flight allowed so a hovering test player is not kicked
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("gamerule mobGriefing false");
        await _server.CommandAsync("gamerule randomTickSpeed 0");   // the test world is a cold biome: a built lake FREEZES within seconds and ice is ground, so a lake test swam to a "shore" nobody built
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task Stop() { if (_server != null) await _server.DisposeAsync(); }

    /// <summary>A 41 by 41 stone floor (top at y 199, three thick so water on it stays put) with air above, the player standing at its middle, plus whatever `extra` builds.</summary>
    private async Task<(MinecraftClient Client, int X, int Z)> JoinAsync(string name, params Func<int, int, string>[] extra)
    {
        var client = new MinecraftClient(GameProfile.Offline(name));
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        // The new player spawns ON the previous test's sky floor (the spawn column's top block), and clearing that stage takes it from under him: he fell ~130 blocks before the test began
        // (health 5.5, 7.5, 0 in the next test). Damage-proof while the stage is rebuilt; the effect is taken off again before the test starts so its health assertions are honest.
        await _server.CommandAsync($"effect give {name} minecraft:resistance 60 4 true");
        // Every test builds near the same spawn point in this one world: an earlier test's pool, floor or strip would be land or water in THIS test (a lake test swam to a leftover floor,
        // 2026-11-02). Clear every earlier stage first, in strips (a fill may touch 32,768 blocks). Each stage is force-loaded when built and stays so: a fill into chunks nobody has loaded
        // does nothing, and by now an earlier stage is out of the player's range and would survive as ground.
        foreach (var (ox, oz) in _stages)
            for (var z0 = oz - 22; z0 <= oz + 22; z0 += 30)
                await _server.CommandAsync($"fill {ox - 22} 196 {z0} {ox + 22} 215 {Math.Min(z0 + 29, oz + 22)} minecraft:air");
        _stages.Add((ax, az));
        await _server.CommandAsync($"forceload add {ax - 24} {az - 24} {ax + 24} {az + 24}");
        await Task.Delay(1500);
        await _server.CommandAsync($"fill {ax - 20} 197 {az - 20} {ax + 20} 199 {az + 20} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 20} 200 {az - 20} {ax + 20} 203 {az + 20} minecraft:air");
        foreach (var build in extra) await _server.CommandAsync(build(ax, az));
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax - 20, az - 20) && client.World.IsLoaded(ax + 20, az + 20)
            && client.World.IsLoaded(ax - 20, az + 20) && client.World.IsLoaded(ax + 20, az - 20), Timeout, () => "floor chunks");
        await client.WaitTicksAsync(20);
        await _server.CommandAsync($"effect clear {name}");
        await _server.CommandAsync($"effect give {name} minecraft:instant_health 1 10 true");   // anything the fall took before the effect was on (the first moments after joining)
        await Wait.UntilAsync(() => client.Health >= 20f, Timeout, () => $"health {client.Health}");
        return (client, ax, az);
    }

    private static double Away(MinecraftClient client, Entity e) => Math.Sqrt(Math.Pow(client.Position.X - e.X, 2) + Math.Pow(client.Position.Z - e.Z, 2));

    [TestCase("creeper")]
    [TestCase("zombie")]
    public async Task Idle_WithAMonsterClose_WalksAwayFromIt_EvenWhenBrave(string mob)
    {
        var name = "VertexAvoid" + mob[..2];
        var (client, ax, az) = await JoinAsync(name);
        await using var _ = client;
        // Standing still on a floor, a monster 4.5 blocks away that cannot move (NoAI): not lit, not attacking, just there.
        await _server.CommandAsync($"summon minecraft:{mob} {ax + 5.0} 200 {az + 0.5} {{NoAI:1b,Tags:[\"vavoid\"],active_effects:[{{id:\"minecraft:fire_resistance\",duration:1000000}}]}}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:" + mob), Timeout, () => "the " + mob);
        var monster = client.Entities.Values.First(e => e.Name == "minecraft:" + mob);
        var skills = new Skills(client);
        var traced = new System.Collections.Concurrent.ConcurrentQueue<string>();
        skills.Trace += traced.Enqueue;
        var tasks = new TaskRunner();
        using var reflexes = new Reflexes(client, skills, tasks, () => new PlayStyle { Bravery = 1.0 }) { Enabled = false };
        try
        {
            // Control: reflexes off, he stays right there.
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.That(Away(client, monster), Is.LessThan(6), "with the reflexes off he stands where he is");

            reflexes.Enabled = true;
            await Wait.UntilAsync(() => Away(client, monster) >= 9.5, TimeSpan.FromSeconds(30), () => $"{Away(client, monster):0.0} blocks from the {mob}; task {tasks.Current}");
            Assert.That(traced.Any(t => t.StartsWith("stepping away from: ") && t.Contains(mob + " 5") && !t.Contains("out of sight")), Is.True,
                "he says what he is stepping away from, how far, and that he can see it (2026-11-03: half an hour of \"could not get away\" with no word on which monster): " + string.Join(" | ", traced));
            Assert.That(client.Health, Is.EqualTo(20f), "never hurt");
            Assert.That(client.Entities.ContainsKey(monster.Id), "and he did not attack it (a brave character does not pick fights)");
            Assert.That(monster.Health ?? 20f, Is.GreaterThanOrEqualTo(20f));
        }
        finally { reflexes.Enabled = false; tasks.Stop(); await _server.CommandAsync("kill @e[tag=vavoid]"); }
    }

    /// <summary>
    /// Live 2026-11-03, daytime: 3 to 8 phantoms circled him for half an hour and a "stepping away from monsters" job fired every 30 seconds ("phantom 1.8 blocks away; I am at ..." in the log), each one
    /// resetting his idle timer, so he never chose a project. A phantom flies: walking away gets nowhere. The control above shows he steps away from a zombie; a phantom the same distance away is left alone.
    /// </summary>
    [Test]
    public async Task Idle_WithAPhantomNear_HeDoesNotWalkAwayFromIt_AndNothingIsStarted()
    {
        var (client, ax, az) = await JoinAsync("VertexPhantom");
        await using var _ = client;
        await _server.CommandAsync($"summon minecraft:phantom {ax + 5.0} 200 {az + 0.5} {{NoAI:1b,Tags:[\"vavoid\"],active_effects:[{{id:\"minecraft:fire_resistance\",duration:1000000}}]}}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:phantom"), Timeout, () => "the phantom");
        var monster = client.Entities.Values.First(e => e.Name == "minecraft:phantom");
        var skills = new Skills(client);
        Assert.That(skills.NearestHostile(Reflexes.AvoidWithin), Is.Null, "a flier is not a threat to leave on foot");
        Assert.That(skills.DescribeHostiles(20), Does.StartWith("none"), "and is not listed as one");
        var tasks = new TaskRunner();
        using var reflexes = new Reflexes(client, skills, tasks, () => new PlayStyle { Bravery = 1.0 }) { Enabled = true };
        try
        {
            var started = new List<string>();
            tasks.Starting += started.Add;
            await Task.Delay(TimeSpan.FromSeconds(15));
            Assert.That(started, Is.Empty, "no job was started for it: " + string.Join(", ", started));
            Assert.That(Away(client, monster), Is.LessThan(6), "and he is still where he was");
        }
        finally { reflexes.Enabled = false; tasks.Stop(); await _server.CommandAsync("kill @e[tag=vavoid]"); }
    }

    /// <summary>
    /// Live on AubsCraft 2026-11-02 (TJ): "he is swimming out into the water and there is no land in sight in front of him; it is behind him". Stepping away from monsters aimed at "far from them",
    /// and any far-enough PLACE counted, water included. Swimming across water is needed and wanted (TJ: "being able and willing to swim across water is needed"), so the fix is where "away" ENDS:
    /// on dry land. The stage: a strip of land 3 wide and 11 long in a lake, two creepers (no AI) at one end, him 6 blocks from them. Without a far shore there is nowhere dry to go: he stays on
    /// the strip and says so. With a far shore 16 blocks across the water he swims over to it and ends on dry land.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task StepAwayFromMonsters_SwimsAcrossToAFarShore_ButNeverOutIntoNowhere(bool farShore)
    {
        var name = farShore ? "VertexShore" : "VertexLake";
        var (client, ax, az) = await JoinAsync(name,
            (x, z) => $"fill {x - 20} 198 {z - 20} {x + 20} 199 {z + 20} minecraft:water",      // a lake, 2 deep, over the whole floor
            (x, z) => $"fill {x - 1} 198 {z} {x + 1} 199 {z + 10} minecraft:stone",             // a strip of land in it
            (x, z) => farShore ? $"fill {x + 15} 198 {z + 2} {x + 19} 199 {z + 8} minecraft:stone" : "say no far shore");  // and, sometimes, an island 15 blocks east
        await using var _ = client;
        var skills = new Skills(client);
        var swamIn = false;
        using var watch = new CancellationTokenSource();
        var watcher = Task.Run(async () => { while (!watch.IsCancellationRequested) { if (client.BodySnapshot().InWater) swamIn = true; await Task.Delay(50); } });
        try
        {
            await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 7.5}");
            await Wait.UntilAsync(() => Math.Abs(client.Position.Z - (az + 7.5)) < 0.3 && client.OnGround && !client.BodySnapshot().InWater, Timeout, () => $"at {client.Position}");
            foreach (var dz in new[] { 1.5, 2.5 })
                await _server.CommandAsync($"summon minecraft:creeper {ax + 0.5} 200 {az + dz} {{NoAI:1b,Tags:[\"vlake\"]}}");
            await Wait.UntilAsync(() => client.Entities.Values.Count(e => e.Name == "minecraft:creeper") == 2, Timeout, () => "the creepers");
            swamIn = false;
            TestContext.Out.WriteLine("LOOK from the strip:\n" + skills.LookAround() + $"\nat {client.Position}; stage centre {ax},{az}");

            (bool ok, string text) result;
            using (var c = new CancellationTokenSource(TimeSpan.FromSeconds(60))) result = await skills.StepAwayFromMonstersAsync(10, c.Token);
            TestContext.Out.WriteLine($"far shore {farShore}: {result.ok} {result.text}; at {client.Position}; swam {swamIn}");
            Assert.That(client.Health, Is.EqualTo(20f));
            if (farShore)
            {
                Assert.That(swamIn, Is.True, "he swam across (swimming is allowed and wanted)");
                Assert.That(result.ok, Is.True, result.text);
                Assert.That(client.Position.X, Is.GreaterThan(ax + 14), "he ended on the island");
                Assert.That(client.BodySnapshot().InWater, Is.False, "on dry land, not mid-lake");
            }
            else
            {
                Assert.That(swamIn, Is.False, "there was nowhere dry to go: he did not swim out into the lake");
                Assert.That(result.ok, Is.False, "and he says he could not get away, rather than pretending");
                Assert.That(client.Position.Z, Is.InRange(az - 0.5, az + 11), "still on the strip");
            }
        }
        finally { watch.Cancel(); await watcher; await _server.CommandAsync("kill @e[tag=vlake]"); }
    }

    /// <summary>TJ's picture: four creepers around him, one on each side, none lit (no AI). Note: the old straight-line flee also got out of this flat ring (24 s against 9 s, one run each), so this guards the behaviour but does not reproduce the failure seen live.</summary>
    [Test]
    public async Task Idle_WithFourCreepersAround_GetsOutOfTheRing()
    {
        var (client, ax, az) = await JoinAsync("VertexRing");
        await using var _ = client;
        foreach (var (dx, dz) in new[] { (4.5, 0.0), (-3.5, 0.0), (0.0, 4.5), (0.0, -3.5) })
            await _server.CommandAsync($"summon minecraft:creeper {ax + 0.5 + dx} 200 {az + 0.5 + dz} {{NoAI:1b,Tags:[\"vring\"]}}");
        await Wait.UntilAsync(() => client.Entities.Values.Count(e => e.Name == "minecraft:creeper") == 4, Timeout, () => "the four creepers");
        double Nearest() => client.Entities.Values.Where(e => e.Name == "minecraft:creeper").Min(e => Away(client, e));
        var skills = new Skills(client);
        var tasks = new TaskRunner();
        var ended = new List<TaskOutcome>();
        tasks.Finished += o => { lock (ended) ended.Add(o); };
        using var reflexes = new Reflexes(client, skills, tasks, () => new PlayStyle()) { Enabled = false };
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.That(Nearest(), Is.LessThan(5), "with the reflexes off he stays in the ring");

            reflexes.Enabled = true;
            await Wait.UntilAsync(() => Nearest() >= 9.5, TimeSpan.FromSeconds(40), () => $"nearest creeper {Nearest():0.0} blocks; at {client.Position}; task {tasks.Current}; ended {string.Join("; ", ended.Select(o => o.Summary))}");
            Assert.That(client.Health, Is.EqualTo(20f));
            lock (ended) Assert.That(ended.All(o => o.Quiet), Is.True, "and nothing about it was meant to be spoken");
        }
        finally { reflexes.Enabled = false; tasks.Stop(); await _server.CommandAsync("kill @e[tag=vring]"); }
    }

    /// <summary>
    /// Live on AubsCraft 2026-11-02: a plugin (EssentialsX) turned /fly on for the account at login, the server said "flying", and he hovered 50 blocks up for an hour. The server's message is
    /// reproduced exactly (abilities: may fly + flying). Survival, fall damage on: he must come down by flying down, not fall.
    /// </summary>
    [Test]
    public async Task Idle_FlyingInSurvival_LandsSafely_InsteadOfHoveringFiftyBlocksUp()
    {
        var (client, ax, az) = await JoinAsync("VertexFly");
        await using var _ = client;
        await _server.CommandAsync($"tp VertexFly {ax + 0.5} 250 {az + 0.5}");
        await Wait.UntilAsync(() => client.Position.Y > 245, Timeout, () => $"at {client.Position}");
        client.OnAbilities(2 | 4, 0.05f); // what EssentialsX's /fly makes the server send: may fly, and flying
        Assert.That(client.Flying && client.GameMode == GameMode.Survival, Is.True);
        // The flag is only in HIS client: this test server is never told he can fly (no plugin), so it counts his descent as a FALL and killed him in 2 of 3 runs (health 0). In the real case the
        // server knows he is flying and a flying player takes no fall damage (vanilla; not something this test can show). So: protected from that mistaken accounting, and the point of the test is
        // the descent and the end of the flight.
        await _server.CommandAsync("effect give VertexFly minecraft:resistance 120 4 true");
        var skills = new Skills(client);
        var tasks = new TaskRunner();
        using var reflexes = new Reflexes(client, skills, tasks, () => new PlayStyle()) { Enabled = false };
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.That(client.Position.Y, Is.GreaterThan(245), "with the reflexes off he hovers where he is");
            Assert.That(client.Flying, Is.True);

            reflexes.Enabled = true;
            await Wait.UntilAsync(() => !client.Flying && client.OnGround, TimeSpan.FromSeconds(60), () => $"at {client.Position}, flying {client.Flying}, on ground {client.OnGround}, task {tasks.Current}");
            Assert.That(client.Position.Y, Is.EqualTo(200).Within(1.5), "on the floor");
            Assert.That(client.Health, Is.GreaterThan(0f), "alive (resistance on: see above)");
        }
        finally { reflexes.Enabled = false; tasks.Stop(); }
    }

    /// <summary>The climb: in water with /fly on, the come-up-for-air reflex held "jump", which in flight means up (62 to 86 blocks, then more).</summary>
    [Test]
    public async Task Idle_InWaterWithFlyOn_DoesNotClimbIntoTheAir_AndEndsOnTheGround()
    {
        // As at login on AubsCraft: already flying, hovering IN water deep enough (3 blocks; the come-up-for-air reflex acts above 1.7) and not touching the ground, which would end the flight by itself.
        var (client, ax, az) = await JoinAsync("VertexFlyWet", (x, z) => $"fill {x - 5} 196 {z - 5} {x + 5} 196 {z + 5} minecraft:stone", (x, z) => $"fill {x - 4} 197 {z - 4} {x + 4} 199 {z + 4} minecraft:water");
        await using var _ = client;
        await _server.CommandAsync($"tp VertexFlyWet {ax + 0.5} 198.05 {az + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 198.05) < 0.3 && client.BodySnapshot().InWater, Timeout, () => $"at {client.Position}, water {client.BodySnapshot().WaterHeight}");
        await client.WaitTicksAsync(4); // so a stale "on the ground" from before the teleport is gone: touching the ground ends flight
        client.OnAbilities(2 | 4, 0.05f);
        Assert.That(client.Flying && client.BodySnapshot().WaterHeight > 1.7, Is.True, $"set up: flying {client.Flying}, water {client.BodySnapshot().WaterHeight}");
        var skills = new Skills(client);
        var tasks = new TaskRunner();
        var ended = new List<string>();
        tasks.Finished += o => { lock (ended) ended.Add($"{o.Task}: {(o.Success ? "ok" : "failed")}: {o.Summary}"); };
        using var reflexes = new Reflexes(client, skills, tasks, () => new PlayStyle());
        var highest = client.Position.Y;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < until) { highest = Math.Max(highest, client.Position.Y); await Task.Delay(100); }
            TestContext.Out.WriteLine($"highest {highest:0.0}, now {client.Position}, flying {client.Flying}, in water {client.BodySnapshot().InWater}");
            Assert.That(highest, Is.LessThan(203), "he never rose into the air (the floor is at 200)");
            Assert.That(client.Flying, Is.False, "and the flight is over");
            await Wait.UntilAsync(() => !client.BodySnapshot().InWater && client.OnGround, TimeSpan.FromSeconds(60), () => $"still in the water: at {client.Position} (pool centre {ax + 0.5},{az + 0.5}), task {tasks.Current}; jobs so far: {string.Join(" | ", ended)}");
        }
        finally { reflexes.Enabled = false; tasks.Stop(); }
    }

    [Test]
    public async Task Idle_InWater_GetsOutOfIt()
    {
        // A 9 by 9 pool one block deep in the floor: the shore is a step up, like a river bank.
        var (client, ax, az) = await JoinAsync("VertexWater", (x, z) => $"fill {x - 4} 199 {z - 4} {x + 4} 199 {z + 4} minecraft:water");
        await using var _ = client;
        await Wait.UntilAsync(() => client.BodySnapshot().InWater, Timeout, () => $"at {client.Position}");
        var skills = new Skills(client);
        var tasks = new TaskRunner();
        using var reflexes = new Reflexes(client, skills, tasks, () => new PlayStyle()) { Enabled = false };
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.That(client.BodySnapshot().InWater, Is.True, "with the reflexes off he stays in the water");

            reflexes.Enabled = true;
            await Wait.UntilAsync(() => !client.BodySnapshot().InWater && client.OnGround && Math.Max(Math.Abs(client.Position.X - (ax + 0.5)), Math.Abs(client.Position.Z - (az + 0.5))) > 4.5,
                TimeSpan.FromSeconds(40), () => $"pool centre {ax + 0.5},{az + 0.5}; at {client.Position}, on ground {client.OnGround}, in water {client.BodySnapshot().InWater}, task {tasks.Current}");
        }
        finally { reflexes.Enabled = false; tasks.Stop(); }
    }
}
