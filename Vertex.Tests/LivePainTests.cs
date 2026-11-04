using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// He has to feel what hurts him (TJ, 2026-11-03: "the logs simply say he died with no reason"; 17 deaths in 9.4 minutes, 5 of them a bare "died"). The client now reads the server's damage event and death
/// packet. Real Paper server: real damage of a named type, real fire, a real death.
/// </summary>
[NonParallelizable]
public class LivePainTests
{
    private const int Port = 25971;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("pain", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "easy" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task Stop() { if (_server != null) await _server.DisposeAsync(); }

    private async Task<MinecraftClient> JoinAsync(string name)
    {
        var client = new MinecraftClient(GameProfile.Offline(name));
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        await Wait.UntilAsync(() => client.IsSpawned && client.OnGround, Timeout, () => "on the ground");
        await _server.CommandAsync($"effect give {name} minecraft:instant_health 1 10 true");
        await Wait.UntilAsync(() => client.Health >= 20f, Timeout, () => $"health {client.Health}");
        await client.WaitTicksAsync(80);      // a player who has just joined cannot be hurt for 3 seconds: damage in that time is ignored
        return client;
    }

    [Test]
    public async Task HeKnowsWhatKindOfDamageHurtHim_AndWhoDidIt_AndTheHealthItCost()
    {
        await using var client = await JoinAsync("PainKind");
        var damages = new List<DamageInfo>();
        var drops = new List<float>();
        client.Damaged += d => { lock (damages) damages.Add(d); };
        client.HealthChanged += (a, b) => { lock (drops) if (b < a) drops.Add(a - b); };

        await _server.CommandAsync("damage PainKind 4 minecraft:arrow");
        await Wait.UntilAsync(() => { lock (drops) return drops.Count > 0; }, Timeout, () => "health to fall");
        lock (damages) Assert.That(damages.Single().Type, Is.EqualTo("arrow"));
        lock (drops) Assert.That(drops.Single(), Is.EqualTo(4f).Within(0.01f));

        // A named attacker: the cause carries the entity, so "who did it" can be answered.
        await _server.CommandAsync("summon minecraft:zombie ~ ~ ~ {NoAI:1b,Tags:[\"hitter\"]}");
        await client.WaitTicksAsync(10);
        lock (damages) damages.Clear();
        await _server.CommandAsync("execute at PainKind run tp @e[tag=hitter,limit=1] ~2 ~ ~");
        await _server.CommandAsync("damage PainKind 2 minecraft:mob_attack by @e[tag=hitter,limit=1]");
        await Wait.UntilAsync(() => { lock (damages) return damages.Count > 0; }, Timeout, () => "the zombie's hit");
        DamageInfo hit;
        lock (damages) hit = damages.Single();
        Assert.That(hit.Type, Is.EqualTo("mob_attack"));
        Assert.That(client.Entities.TryGetValue(hit.CauseEntityId, out var who) ? who.Name : "(not found)", Is.EqualTo("minecraft:zombie"));
        await _server.CommandAsync("kill @e[tag=hitter]");
    }

    [Test]
    public async Task HeKnowsHeIsOnFire()
    {
        await using var client = await JoinAsync("PainFire");
        var (x, y, z) = client.FeetBlock;
        Assert.That(client.OnFire, Is.False);
        var kinds = new HashSet<string>();
        client.Damaged += d => { lock (kinds) kinds.Add(d.Type); };
        await _server.CommandAsync($"setblock {x} {y} {z} minecraft:fire");
        await Wait.UntilAsync(() => client.OnFire, Timeout, () => "the on-fire flag");
        await Wait.UntilAsync(() => { lock (kinds) return kinds.Contains("in_fire") || kinds.Contains("on_fire"); }, Timeout, () => "fire damage");
        await _server.CommandAsync($"setblock {x} {y} {z} minecraft:air");
        await Wait.UntilAsync(() => !client.OnFire, TimeSpan.FromSeconds(30), () => "the fire to go out");
    }

    [Test]
    public async Task TheDeathPacket_ComesWithTheMessage_WhileHeIsStillWhereItHappened()
    {
        await using var client = await JoinAsync("PainDeath");
        await _server.CommandAsync("tp PainDeath ~20 ~ ~");
        await client.WaitTicksAsync(40);
        string? message = null;
        (double X, double Y, double Z) where = default, before = client.Position;
        client.Died += m => { message = m; where = client.Position; };
        var respawned = false;
        client.Spawned += () => respawned = message != null;

        await _server.CommandAsync("damage PainDeath 100 minecraft:arrow");
        await Wait.UntilAsync(() => message != null, Timeout, () => "the death packet");
        TestContext.Out.WriteLine(message);
        Assert.That(message, Does.Contain("PainDeath"));
        Assert.That(Math.Abs(where.X - before.X), Is.LessThan(2), "the position is still where he died: it says where his things are");
        await Wait.UntilAsync(() => respawned, Timeout, () => "the respawn");
        await Wait.UntilAsync(() => client.Health >= 20f, Timeout, () => $"health after the respawn {client.Health}");
    }

    [Test]
    public void ADeath_FromThePacket_SaysWhatHurtHim_AndDoesNotBecomeAGotThroughIt()
    {
        var t0 = new DateTime(2026, 11, 3, 21, 27, 0, DateTimeKind.Utc);
        Moment At(int s, float health) => new(t0.AddSeconds(s), health, 20, "getting a weapon, tools and food", 15000, "clear", "240 64 -256", [], false, false);
        var log = new ExperienceLog();
        var lessons = new List<Skills.Lesson>();
        for (var s = 0; s < 5; s++) lessons.AddRange(log.Observe(At(s, 20)));
        log.NotePain(new Pain(t0.AddSeconds(5), 4, "arrow", "skeleton"));
        log.NotePain(new Pain(t0.AddSeconds(6), 4, "arrow", "skeleton"));
        log.NotePain(new Pain(t0.AddSeconds(7), 12, "on_fire", null));
        var death = log.Died(At(7, 8), "HereticSpawn was shot by Skeleton");
        TestContext.Out.WriteLine(death.Text);
        Assert.That(death.Key, Is.EqualTo("death"));
        Assert.That(death.Text, Does.Contain("was shot by Skeleton").And.Contain("on fire 6 hearts").And.Contain("arrow from skeleton 4 hearts (2 times)"));
        Assert.That(death.Tags, Does.Contain("hurt:arrow").And.Contain("hurt:on_fire"));

        // The respawn at full health straight after: the live log turned this into "I lost 9.7 hearts and got through it (10 hearts left)", 49 times.
        for (var s = 8; s < 40; s++) lessons.AddRange(log.Observe(At(s, 20)));
        Assert.That(lessons, Is.Empty, "a respawn is not surviving");
        Assert.That(log.RecentPains, Is.Empty, "each life starts its own record");
    }

    [Test]
    public void ADeath_WithNoDamageEvent_SaysSo()
    {
        var t0 = new DateTime(2026, 11, 3, 21, 34, 0, DateTimeKind.Utc);
        var log = new ExperienceLog();
        var death = log.Died(new Moment(t0, 20, 20, null, 6000, "clear", "240 64 -256", [], false, false), "HereticSpawn died");
        Assert.That(death.Text, Does.Contain("reported no damage"));
    }

    [Test]
    public async Task AJobEndsWhenHeDies_AndWhoeverAskedHears()
    {
        var tasks = new TaskRunner { Requester = () => "Aubri" };
        var ended = new TaskCompletionSource<TaskOutcome>();
        tasks.Finished += o => ended.TrySetResult(o);
        var cancelled = new TaskCompletionSource();
        tasks.Start("bringing wood", async ct => { try { await Task.Delay(-1, ct); } catch (OperationCanceledException) { cancelled.TrySetResult(); throw; } return (true, "done"); });
        tasks.Abort("I died: shot");
        var outcome = await ended.Task.WaitAsync(Timeout);
        await cancelled.Task.WaitAsync(Timeout);
        Assert.That(outcome.Success, Is.False);
        Assert.That(outcome.For, Is.EqualTo("Aubri"));
        Assert.That(outcome.Quiet, Is.False, "somebody asked for it: they hear");
        Assert.That(tasks.IsBusy, Is.False);
    }
}
