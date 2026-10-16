using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>A shield against a REAL Paper server: it goes to the off hand, and a raised shield facing a husk takes the hits that otherwise hurt.</summary>
[NonParallelizable]
public class LiveShieldTests
{
    private const int Port = 25968;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("shield", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "hard" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    private async Task<(MinecraftClient Client, int X, int Z)> JoinOnArenaAsync(string name, int r = 14)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax + r} 215 {az + r} minecraft:air");
        await _server.CommandAsync($"fill {ax - r} 199 {az - r} {ax + r} 199 {az + r} minecraft:stone");
        await _server.CommandAsync($"clear {name}");
        await Task.Delay(1500);
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 200) < 1e-9 && client.OnGround, Timeout, () => $"{client.Position}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax - r, az - r) && client.World.IsLoaded(ax + r, az + r), Timeout, () => "arena chunks");
        await client.WaitTicksAsync(10);
        return (client, ax, az);
    }

    private async Task GiveAsync(MinecraftClient client, string name, string item, int count)
    {
        var before = client.Inventory.Count(item.Replace("minecraft:", ""));
        await _server.CommandAsync($"give {name} {item} {count}");
        await Wait.UntilAsync(() => client.Inventory.Count(item.Replace("minecraft:", "")) == before + count, Timeout, () => client.Inventory.ToString());
    }

    /// <summary>Stands still, always facing the nearest husk, for a number of ticks; returns the health lost. The husk is summoned with its wits, so it walks up and hits.</summary>
    private async Task<float> StandFacingAHuskAsync(MinecraftClient client, string name, int ax, int az, bool shield, int ticks)
    {
        await _server.CommandAsync("kill @e[type=minecraft:husk]");
        await _server.CommandAsync($"effect give {name} minecraft:instant_health 1 10 true");
        await _server.CommandAsync($"effect give {name} minecraft:saturation 1 10 true");
        await client.WaitTicksAsync(20);
        await _server.CommandAsync($"summon minecraft:husk {ax + 6.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:husk"), Timeout, () => "the husk");
        var skills = new Skills(client);
        var before = client.Health;
        var lowest = before;       // the lowest, not the last: on hard a husk can kill a player standing still within 8 s, and the respawn would read as no damage at all
        if (shield) Assert.That(await skills.ShieldUpAsync(CancellationToken.None), Is.True);
        for (var i = 0; i < ticks; i++)
        {
            var husk = client.Entities.Values.FirstOrDefault(e => e.Name == "minecraft:husk");
            if (husk != null) client.LookAt(husk.X, husk.Y + 1.0, husk.Z);
            await client.WaitTicksAsync(1);
            lowest = Math.Min(lowest, client.Health);
            if (i % 40 == 0) TestContext.Out.WriteLine($"tick {i}: health {client.Health}, husk {(husk == null ? "gone" : husk.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z).ToString("0.0") + " away")}");
        }
        if (shield) await skills.ShieldDownAsync(CancellationToken.None);
        var lost = before - lowest;
        await _server.CommandAsync("kill @e[type=minecraft:husk]");
        return lost;
    }

    [Test]
    public async Task AShieldGoesToTheOffHand()
    {
        const string name = "VertexShieldEq";
        var (client, _, _) = await JoinOnArenaAsync(name);
        await using var _ = client;
        var skills = new Skills(client);
        Assert.That(await skills.EquipShieldAsync(CancellationToken.None), Is.False, "none to equip");
        Assert.That(await skills.ShieldUpAsync(CancellationToken.None), Is.False, "none to raise");
        await GiveAsync(client, name, "minecraft:shield", 1);
        Assert.That(await skills.EquipShieldAsync(CancellationToken.None), Is.True);
        Assert.That(client.Inventory.OffHandItem.Name, Is.EqualTo("minecraft:shield"));
        Assert.That(client.Inventory.Count("shield"), Is.EqualTo(1), "the one shield, moved and not copied");
    }

    [Test]
    public async Task ARaisedShield_TakesTheHitsAHuskLandsOnSomeoneWithout()
    {
        const string name = "VertexShieldHusk";
        var (client, ax, az) = await JoinOnArenaAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:shield", 1);
        var skills = new Skills(client);
        Assert.That(await skills.EquipShieldAsync(CancellationToken.None), Is.True);

        var without = await StandFacingAHuskAsync(client, name, ax, az, shield: false, ticks: 120);
        TestContext.Out.WriteLine($"without the shield up: lost {without} health in 10 s");
        Assert.That(without, Is.GreaterThan(2), "a husk on hard hurts a player standing still (this is the control: if nothing hurts, the shield proves nothing)");

        var with = await StandFacingAHuskAsync(client, name, ax, az, shield: true, ticks: 120);
        TestContext.Out.WriteLine($"with the shield up: lost {with} health in 10 s");
        Assert.That(with, Is.LessThanOrEqualTo(without / 4), "the raised shield takes the hits");
    }

    /// <summary>One melee fight with a husk that comes for us; returns (won, the lowest health reached relative to the start).</summary>
    private async Task<(bool Won, float Lost)> FightAHuskAsync(MinecraftClient client, string name, int ax, int az, Skills skills)
    {
        await _server.CommandAsync("kill @e[type=minecraft:husk]");
        await _server.CommandAsync($"effect give {name} minecraft:instant_health 1 10 true");
        await _server.CommandAsync($"effect give {name} minecraft:saturation 1 10 true");
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
        await client.WaitTicksAsync(40);
        await _server.CommandAsync($"summon minecraft:husk {ax + 8.5} 200 {az + 0.5} ");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:husk"), Timeout, () => "the husk");
        var before = client.Health;
        var lowest = before;
        using var watch = new CancellationTokenSource();
        var watcher = Task.Run(async () => { while (!watch.IsCancellationRequested) { lowest = Math.Min(lowest, client.Health); await Task.Delay(20); } });
        var (ok, text) = await skills.AttackAsync("husk", CancellationToken.None);
        watch.Cancel();
        await watcher;
        TestContext.Out.WriteLine($"fight: {text} lost {before - lowest} (health now {client.Health})");
        return (ok && text.Contains("defeated 1"), before - lowest);
    }

    /// <summary>
    /// Melee with a shield still wins (the shield is up between swings and down for each one). It is NOT asserted to cost less health than without: measured 2026-10-15, a husk's fight is one
    /// or two blows of 4.5 either way (4.5 four times without, 4.5/4.5/4.5/6 with), too short to tell a difference from noise; the stand-still test above is the one that proves the block.
    /// </summary>
    [Test]
    public async Task FightingWithAShield_StillWinsAndSurvives()
    {
        const string name = "VertexShieldFgt";
        var (client, ax, az) = await JoinOnArenaAsync(name);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:iron_sword", 1);
        await GiveAsync(client, name, "minecraft:shield", 1);
        var skills = new Skills(client);
        Assert.That(await skills.EquipShieldAsync(CancellationToken.None), Is.True);
        for (var i = 0; i < 3; i++)
        {
            var (won, lost) = await FightAHuskAsync(client, name, ax, az, skills);
            Assert.That(won, Is.True, $"it beat the husk (fight {i + 1})");
            Assert.That(lost, Is.LessThan(12), "and was not nearly killed doing it");
        }
    }

    /// <summary>A stray (it shoots slowness arrows and, unlike a skeleton, does not burn in daylight) 14 blocks away: walked up to and killed, with and without a shield.</summary>
    private async Task<(bool Won, float Lost)> FightAStrayAsync(MinecraftClient client, string name, int ax, int az, Skills skills)
    {
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        await _server.CommandAsync($"effect clear {name}");
        await _server.CommandAsync($"effect give {name} minecraft:instant_health 1 10 true");
        await _server.CommandAsync($"effect give {name} minecraft:saturation 1 10 true");
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
        await client.WaitTicksAsync(40);
        await _server.CommandAsync($"summon minecraft:stray {ax + 12.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:stray"), Timeout, () => "the stray");
        var before = client.Health;
        var lowest = before;
        using var watch = new CancellationTokenSource();
        var watcher = Task.Run(async () => { while (!watch.IsCancellationRequested) { lowest = Math.Min(lowest, client.Health); await Task.Delay(20); } });
        var (ok, text) = await skills.AttackAsync("stray", CancellationToken.None);
        watch.Cancel();
        await watcher;
        TestContext.Out.WriteLine($"stray fight: {text} lost {before - lowest}");
        return (ok && text.Contains("defeated 1"), before - lowest);
    }

    [Test]
    public async Task AStrayShootingFromAfar_IsWalkedUpToAndBeaten_WithAShield()
    {
        const string name = "VertexShieldStry";
        var (client, ax, az) = await JoinOnArenaAsync(name, 16);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:iron_sword", 1);
        await GiveAsync(client, name, "minecraft:shield", 1);
        var skills = new Skills(client);
        var plain = new List<float>();
        for (var i = 0; i < 2; i++) plain.Add((await FightAStrayAsync(client, name, ax, az, skills)).Lost);
        Assert.That(await skills.EquipShieldAsync(CancellationToken.None), Is.True);
        var shielded = new List<float>();
        for (var i = 0; i < 2; i++)
        {
            var (won, lost) = await FightAStrayAsync(client, name, ax, az, skills);
            Assert.That(won, Is.True, $"it beat the stray (fight {i + 1})");
            shielded.Add(lost);
        }
        TestContext.Out.WriteLine($"health lost without a shield: {string.Join(", ", plain)}; with: {string.Join(", ", shielded)}");
    }
}
