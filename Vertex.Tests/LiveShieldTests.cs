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
        // A wall two high round the edge: the floor hangs in the air, and a stray backing away from him stepped off it and was gone ("I couldn't get to the stray", 2026-11-17, in a run of the classes).
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax + r} 201 {az - r} minecraft:stone");
        await _server.CommandAsync($"fill {ax - r} 200 {az + r} {ax + r} 201 {az + r} minecraft:stone");
        await _server.CommandAsync($"fill {ax - r} 200 {az - r} {ax - r} 201 {az + r} minecraft:stone");
        await _server.CommandAsync($"fill {ax + r} 200 {az - r} {ax + r} 201 {az + r} minecraft:stone");
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
            // Without the shield, one blow landed is the whole point of the control. Standing on for the rest of the time killed him once (2026-11-17: 18.5 health gone by tick 80), and he came back
            // to life without his shield, which failed the half of the test that is about the shield.
            if (!shield && before - lowest >= 4) break;
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
        TestContext.Out.WriteLine($"without the shield up: lost {without} health to the first blow");
        Assert.That(without, Is.GreaterThan(2), "a husk on hard hurts a player standing still (this is the control: if nothing hurts, the shield proves nothing)");

        var with = await StandFacingAHuskAsync(client, name, ax, az, shield: true, ticks: 120);
        TestContext.Out.WriteLine($"with the shield up: lost {with} health in 6 s");
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
    /// What a fight costs him against the monsters arm's length has not been measured against: four fights the old way and four at arm's length, turn about, at night (a zombie burns by day and a
    /// spider is not hostile in the light). A measurement to read, run by name; it asserts nothing about the outcome.
    /// </summary>
    [TestCase("zombie", "", 1, "VertexFoeZom", "open")]
    [TestCase("spider", "", 1, "VertexFoeSpi", "open")]
    [TestCase("zombie", "{IsBaby:1b}", 1, "VertexFoeBaby", "open")]
    [TestCase("husk", "", 2, "VertexFoeTwo", "open")]
    [TestCase("husk", "", 3, "VertexFoeThree", "open")]
    // Where there is no ground to give: a wall three blocks behind him, and a tunnel one block wide (they come in a line, and he can only back straight down it).
    [TestCase("husk", "", 3, "VertexFoeWall", "wall")]
    [TestCase("husk", "", 3, "VertexFoeTunl", "tunnel")]
    // Sixteen fights giving ground and nothing else, to catch the one in ten or so that costs him: such a fight is printed tick by tick.
    [TestCase("husk", "", 3, "VertexFoeMany", "open")]
    [Explicit("a measurement to read: what a fight costs him against other monsters")]
    public async Task AgainstOtherMonsters_WhatAFightCosts(string mob, string nbt, int count, string name, string place) =>
        await FightsAsync(mob, nbt, count, name, name == "VertexFoeMany" ? "DDDDDDDDDDDDDDDD" : place != "open" ? "CDDCCDDC" : count > 1 ? "BCDDCBCDBBDC" : "ABBAABBA", night: true, place);

    /// <summary>
    /// TJ, 2026-11-16: good at fighting "so that when he plays with others he can join them and help protect the group". A group is where more than one monster comes at once. Against three husks he
    /// hits whichever is nearest in reach and gives ground while his sword comes round: measured 2026-11-17, 0 health lost in four fights of four, where standing at arm's length and fighting them one
    /// at a time had to be broken off at 2 or 3 hearts four times of four. Three fights that way here, every husk down and at most one blow taken in all; two fights the other way are the control.
    /// </summary>
    [Test]
    public async Task AgainstThreeHusksAtOnce_HeGivesGround_AndIsHardlyTouched()
    {
        var (lost, ends) = await FightsAsync("husk", "", 3, "VertexThree", "DBDBD", night: false);
        Assert.That(ends['D'], Has.All.Contain("defeated 3"), "giving ground, he brings down all three every time");
        Assert.That(lost['D'].Sum(), Is.LessThanOrEqualTo(4.5f), "and takes at most one of their blows in three fights");
        Assert.That(lost['B'].Sum(), Is.GreaterThan(9f), "the control: three husks cost a player who stands and takes them one at a time, or this proves nothing");
    }

    /// <summary>
    /// Live on 733a172 (15:51, 2026-11-17): a zombie at 2 blocks hit him three times in three seconds where he stood, with a second zombie about. The log cannot say why he stood. What the code
    /// could do, and this test shows: waiting on the nearest of the others (so as not to walk into them) also held him still for two seconds when the one he had set out for was right beside
    /// him and only out of view. Here a husk stands two blocks off behind a post, and another in plain view six blocks the other way; neither moves. He goes for the one beside him at once.
    /// </summary>
    [Test]
    public async Task AMonsterBesideHimButOutOfView_IsGoneFor_NotLeftWhileHeWaitsOnOneFartherOff()
    {
        const string name = "VertexHidden";
        var (client, ax, az) = await JoinOnArenaAsync(name, 20);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:iron_sword", 1);
        var skills = new Skills(client);
        try
        {
            await _server.CommandAsync("kill @e[type=minecraft:husk]");
            await _server.CommandAsync($"fill {ax + 1} 200 {az} {ax + 1} 201 {az} minecraft:stone");
            await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
            await Wait.UntilAsync(() => client.World.GetBlock(ax + 1, 201, az)?.Name == "minecraft:stone" && Math.Abs(client.Position.X - ax - 0.5) < 0.1 && client.OnGround, Timeout, () => $"the post, and him beside it; at {client.Position}");
            await _server.CommandAsync($"summon minecraft:husk {ax + 2.5} 200 {az + 0.5} {{NoAI:1b,PersistenceRequired:1b}}");
            await _server.CommandAsync($"summon minecraft:husk {ax - 5.5} 200 {az + 0.5} {{NoAI:1b,PersistenceRequired:1b}}");
            await Wait.UntilAsync(() => client.Entities.Values.Count(e => e.Name == "minecraft:husk") == 2, Timeout, () => "the two husks");
            var beside = client.Entities.Values.Where(e => e.Name == "minecraft:husk").OrderBy(e => Math.Abs(e.X - ax - 2.5)).First();
            var farther = client.Entities.Values.First(e => e.Name == "minecraft:husk" && e.Id != beside.Id);
            Assert.That(client.HasClearLineTo(beside), Is.False, "the setup: the one beside him is behind the post");
            Assert.That(client.HasClearLineTo(farther), Is.True, "the setup: the other is in plain view");
            var (sx, _, sz) = client.Position;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var began = DateTime.UtcNow;
            var fight = skills.AttackAsync("husk", cts.Token);
            // With the fault he stood two seconds facing the far one before he moved.
            await Wait.UntilAsync(() => Math.Abs(client.Position.X - sx) + Math.Abs(client.Position.Z - sz) > 0.5 || !client.Entities.ContainsKey(beside.Id), TimeSpan.FromSeconds(1.2),
                () => $"he has not moved in {(DateTime.UtcNow - began).TotalSeconds:0.0} s; at {client.Position}");
            await Wait.UntilAsync(() => !client.Entities.ContainsKey(beside.Id), TimeSpan.FromSeconds(20), () => "the one beside him is still there");
            Assert.That(client.Entities.ContainsKey(farther.Id), Is.True, "and it was the one beside him he went for first");
            var (ok, text) = await fight;
            TestContext.Out.WriteLine(text);
            Assert.That(ok, Is.True, text);
        }
        finally
        {
            await _server.CommandAsync("kill @e[type=minecraft:husk]");
            await _server.CommandAsync($"fill {ax + 1} 200 {az} {ax + 1} 201 {az} minecraft:air");
        }
    }

    /// <summary>
    /// Fights with `count` of a monster that come for him, one fight for each letter of `order`: A as he fought; B at arm's length; C: B and the swing goes to whichever is nearest in reach; D: C and
    /// he gives ground while the sword comes round. Returns the health each fight cost (all 20 for a death) and how each ended, by letter.
    /// </summary>
    private async Task<(Dictionary<char, List<float>> Lost, Dictionary<char, List<string>> Ends)> FightsAsync(string mob, string nbt, int count, string name, string order, bool night, string place = "open")
    {
        var (client, ax, az) = await JoinOnArenaAsync(name, 20);
        await using var _ = client;
        var skills = new Skills(client);
        // "wall": a wall three blocks behind where he stands. "tunnel": walls either side of the line between him and them, one block apart.
        var walls = place switch
        {
            "wall" => new[] { $"fill {ax - 3} 200 {az - 8} {ax - 3} 202 {az + 8}" },
            "tunnel" => new[] { $"fill {ax - 12} 200 {az - 1} {ax + 14} 202 {az - 1}", $"fill {ax - 12} 200 {az + 1} {ax + 14} 202 {az + 1}" },
            _ => Array.Empty<string>(),
        };
        var ways = new[] { ('A', "as he fought"), ('B', "at arm's length"), ('C', "at arm's length, hitting the nearest in reach"), ('D', "the same, and giving ground between swings") };
        var lost = ways.ToDictionary(w => w.Item1, _ => new List<float>());
        var ends = ways.ToDictionary(w => w.Item1, _ => new List<string>());
        var lastDeath = "";
        client.Died += m => lastDeath = $"{m} at {client.Position.X:0.0} {client.Position.Y:0.0} {client.Position.Z:0.0} (the arena's middle is {ax} 200 {az})";
        try
        {
            foreach (var w in walls) await _server.CommandAsync(w + " minecraft:stone");
            if (place == "wall") await Wait.UntilAsync(() => client.World.GetBlock(ax - 3, 201, az)?.Name == "minecraft:stone", Timeout, () => "the wall");
            if (place == "tunnel") await Wait.UntilAsync(() => client.World.GetBlock(ax + 5, 201, az + 1)?.Name == "minecraft:stone", Timeout, () => "the tunnel");
            if (night) await _server.CommandAsync("time set midnight");
            foreach (var way in order)
            {
                skills.FightAtArmsLength = way != 'A';
                skills.HitWhatIsInReach = way is 'C' or 'D';
                skills.StepBackWhenCrowded = way == 'D';
                await _server.CommandAsync($"kill @e[type=minecraft:{mob}]");
                await _server.CommandAsync($"effect give {name} minecraft:instant_health 1 10 true");
                await _server.CommandAsync($"effect give {name} minecraft:saturation 1 10 true");
                await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
                await client.WaitTicksAsync(40);
                if (client.Inventory.Count("iron_sword") == 0) await GiveAsync(client, name, "minecraft:iron_sword", 1);      // a death takes it
                for (var n = 0; n < count; n++)
                    await _server.CommandAsync(place == "tunnel" ? $"summon minecraft:{mob} {ax + 8.5 + n * 1.5} 200 {az + 0.5} {nbt}" : $"summon minecraft:{mob} {ax + 8.5} 200 {az + 0.5 + n * 2} {nbt}");
                await Wait.UntilAsync(() => client.Entities.Values.Count(e => e.Name == "minecraft:" + mob) >= count, Timeout, () => "the " + mob);
                var before = client.Health;
                var lowest = before;
                var deaths = 0;
                void Died() => deaths++;
                client.Spawned += Died;
                using var watch = new CancellationTokenSource();
                var far = 0.0;      // how far from the arena's middle the fight took him (its floor ends 20 blocks out)
                var ticks = new List<string>();     // the fight tick by tick, printed only when giving ground cost him: where each of them was, his sword, the keys he held
                var began = client.Ticks;
                var watcher = Task.Run(async () =>
                {
                    var seen = client.Health;
                    while (!watch.IsCancellationRequested)
                    {
                        lowest = Math.Min(lowest, client.Health);
                        var (wx, wy, wz) = client.Position;
                        if (wy > 199) far = Math.Max(far, Math.Sqrt((wx - ax - 0.5) * (wx - ax - 0.5) + (wz - az - 0.5) * (wz - az - 0.5)));
                        var foes = client.Entities.Values.Where(e => e.Name == "minecraft:" + mob).Select(e => e.DistanceTo(wx, wy, wz)).OrderBy(d => d).Select(d => d.ToString("0.0"));
                        var k = client.Controls;
                        ticks.Add($"t{client.Ticks - began,4} at {wx - ax:0.0} {wz - az:0.0} health {client.Health,4:0.#} sword in {client.TicksUntilAttackReady,2} keys {(k.Forward ? "F" : "")}{(k.Back ? "B" : "")}{(k.Left ? "L" : "")}{(k.Right ? "R" : "")}{(k.Jump ? "J" : "")} {(client.OnGround ? "" : "AIR ")}foes at {string.Join(" ", foes)}{(client.Health < seen ? "  <-- HIT" : "")}");
                        seen = client.Health;
                        await Task.Delay(50);
                    }
                });
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                string text;
                try { text = (await skills.AttackAsync(mob, limit.Token)).Item2; }
                catch (OperationCanceledException) { text = "(still fighting after a minute)"; }
                watch.Cancel();
                await watcher;
                client.Spawned -= Died;
                var cost = deaths > 0 ? before : before - lowest;
                lost[way].Add(cost);
                ends[way].Add(deaths > 0 ? "DIED: " + lastDeath : text);
                TestContext.Out.WriteLine($"{way}: {(deaths > 0 ? "died: " + lastDeath : text)} lost {cost}; went {far:0.0} blocks from the middle");
                if (way == 'D' && cost > 0)
                {
                    var firstHit = ticks.FindIndex(l => l.Contains("<-- HIT"));
                    foreach (var l in ticks.Skip(Math.Max(0, firstHit - 50)).Take(110)) TestContext.Out.WriteLine("   " + l);
                }
            }
        }
        finally
        {
            await _server.CommandAsync($"kill @e[type=minecraft:{mob}]");
            if (night) await _server.CommandAsync("time set day");
            foreach (var w in walls) await _server.CommandAsync(w + " minecraft:air");
        }
        foreach (var (way, what) in ways.Where(w => lost[w.Item1].Count > 0))
            TestContext.Out.WriteLine($"{count} {mob}{nbt} {way} {what}: health lost {string.Join(", ", lost[way])} (sum {lost[way].Sum()}); {string.Join(" | ", ends[way])}");
        return (lost, ends);
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

    /// <summary>
    /// Where the blow in every fight comes from: three fights with a husk, written out ten times a second (how far it is, his health, whether his feet are on the ground, how long until his sword is
    /// ready). A measurement to read, run by name; it asserts only that he won.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    [Explicit("a measurement to read: where in a fight he is hit")]
    public async Task WhereInAFightHeIsHit(bool atArmsLength)
    {
        var name = atArmsLength ? "VertexBlowArm" : "VertexBlow";
        var (client, ax, az) = await JoinOnArenaAsync(name, 20);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:iron_sword", 1);
        var skills = new Skills(client) { FightAtArmsLength = atArmsLength };
        for (var fight = 1; fight <= 3; fight++)
        {
            await _server.CommandAsync("kill @e[type=minecraft:husk]");
            await _server.CommandAsync($"effect give {name} minecraft:instant_health 1 10 true");
            await _server.CommandAsync($"effect give {name} minecraft:saturation 1 10 true");
            await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0");
            await client.WaitTicksAsync(40);
            await _server.CommandAsync($"summon minecraft:husk {ax + 8.5} 200 {az + 0.5} ");
            await Wait.UntilAsync(() => client.Entities.Values.Any(e => e.Name == "minecraft:husk"), Timeout, () => "the husk");
            var lines = new List<string>();
            using var watch = new CancellationTokenSource();
            var started = client.Ticks;
            var watcher = Task.Run(async () =>
            {
                var last = client.Health;
                while (!watch.IsCancellationRequested)
                {
                    var husk = client.Entities.Values.FirstOrDefault(e => e.Name == "minecraft:husk");
                    var (px, py, pz) = client.Position;
                    var d = husk?.DistanceTo(px, py, pz) ?? -1;
                    var hit = client.Health < last;
                    lines.Add($"t{client.Ticks - started,3} d {d,4:0.0} health {client.Health,4:0.#} {(client.OnGround ? "ground" : "AIR   ")} sword in {client.TicksUntilAttackReady,2} husk health {husk?.Health,4:0.#}{(hit ? "  <-- HIT" : "")}");
                    last = client.Health;
                    await Task.Delay(100);
                }
            });
            var (ok, text) = await skills.AttackAsync("husk", CancellationToken.None);
            watch.Cancel();
            await watcher;
            TestContext.Out.WriteLine($"=== fight {fight}: {text}");
            foreach (var l in lines) TestContext.Out.WriteLine(l);
            Assert.That(ok, Is.True, text);
        }
    }

    /// <summary>
    /// TJ, 2026-11-16: "I really want him to be good at fighting the monsters." Willing is one half; the other is what a fight costs him. He walked to two blocks from a monster and stood there between
    /// swings, inside its reach: about one blow of a husk's in every fight (4.5 of his 20 on hard), 2.4 hearts a fight live. Stepping back out of its reach while his sword comes round, as a player does,
    /// against the same husk, the same sword and no shield, turn about so that no way always goes first.
    /// What it found (2026-11-16 and 17): stepping back alone was no better (4.5 a fight either way, once 14.5). The tick-by-tick trace (`WhereInAFightHeIsHit`) showed why: the blow came from
    /// walking into the husk with his sword ready and unswung, and from jumping for a critical hit beside it. Fighting at arm's length (stand, let it come to the sword's full reach, swing without
    /// a jump, short steps near it): as he fought 8, 8, 0, 4.5; at arm's length 0, 0, 0, 0; with stepping back as well 0, 0, 0, 0. So arm's length is on, stepping back off, and this test holds
    /// him to at most one blow in three fights at arm's length, every one won. Three fought the old way and two with stepping back are in its output for comparison.
    /// </summary>
    [Test]
    public async Task AgainstAHusk_HeFightsAtArmsLength_AndIsHardlyTouched()
    {
        const string name = "VertexStepBack";
        var (client, ax, az) = await JoinOnArenaAsync(name, 20);
        await using var _ = client;
        await GiveAsync(client, name, "minecraft:iron_sword", 1);
        var skills = new Skills(client);
        // Three ways, four fights each, in an order that gives none of them the first or the last place every time: A as he fought (walks in, jumps for a critical hit), B at arm's length (stands,
        // lets it come to the sword's full reach, no jump), C at arm's length and stepping back while the sword comes round.
        var lost = new Dictionary<char, List<float>> { ['A'] = [], ['B'] = [], ['C'] = [] };
        var wonAll = new Dictionary<char, bool> { ['A'] = true, ['B'] = true, ['C'] = true };
        foreach (var way in "ABCBACBA")
        {
            skills.FightAtArmsLength = way != 'A';
            skills.StepBackBetweenSwings = way == 'C';
            var (won, cost) = await FightAHuskAsync(client, name, ax, az, skills);
            lost[way].Add(cost);
            wonAll[way] &= won;
        }
        foreach (var (way, what) in new[] { ('A', "as he fought"), ('B', "at arm's length"), ('C', "at arm's length, stepping back") })
            TestContext.Out.WriteLine($"{way} {what}: health lost {string.Join(", ", lost[way])} (sum {lost[way].Sum()}), won all {wonAll[way]}");
        Assert.That(wonAll['B'], Is.True, "at arm's length he wins every fight");
        Assert.That(lost['B'].Sum(), Is.LessThanOrEqualTo(4.5f), "and takes at most one of a husk's blows in the three");
        if (lost['A'].Sum() <= lost['B'].Sum()) Assert.Warn($"the old way cost no more this time ({lost['A'].Sum()} against {lost['B'].Sum()}): by itself that is luck (it cost 20.5 in four fights when measured), but twice in a row is worth a look");
    }
}
