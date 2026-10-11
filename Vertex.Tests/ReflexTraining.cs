using System.Text.Json;
using System.Text.Json.Nodes;
using ILGPU;
using ILGPU.Runtime.CPU;
using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Trains Vertex's fight-or-flight reflex on real fights (PLAN.md M6: "a learned fast policy for reflexes").
/// CollectFights plays each situation twice on a local Paper server, once fighting and once fleeing, each for 20
/// seconds from the first hit, and writes both outcomes to Training/reflex-fights.jsonl (committed, so the training is
/// reproducible). TrainPolicy labels each situation with the better action, trains a SpawnDev.ILGPU.ML network on
/// ILGPU's CPU accelerator (it is tiny), and compares it with the hand-written rule by 5-fold cross validation: the
/// weights ship (Vertex/Brain/reflex-policy.bin) only if the network's choices come out better on fights it never saw.
/// Explicit: run by hand (CPU only, about a minute per situation).
/// </summary>
[Explicit("trains the reflex policy: run by hand")]
[Category("Training")]
[NonParallelizable]
public class ReflexTraining
{
    private const int Port = 25951;
    private const string Name = "VertexFighter";
    private static readonly string[] Mobs = ["zombie", "husk", "zombie_villager", "skeleton", "stray", "spider", "cave_spider", "pillager", "vindicator", "witch"];
    private static readonly string[] Armor = ["", "leather", "chainmail", "iron", "diamond"];
    private static readonly string[] Weapons = ["", "wooden_sword", "stone_sword", "iron_sword", "diamond_sword"];

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Vertex.slnx"))) dir = dir.Parent;
        return Path.Combine([dir?.FullName ?? throw new DirectoryNotFoundException("Vertex.slnx not found above " + AppContext.BaseDirectory), .. parts]);
    }

    private static string DataFile => RepoFile("Vertex.Tests", "Training", "reflex-fights.jsonl");
    private static string WeightsFile => RepoFile("Vertex", "Brain", "reflex-policy.bin");

    private sealed record Scenario(string Mob, int Count, int Health, string Armor, string Weapon);
    private sealed record Outcome(float[] Features, bool Died, float Health, int Threats, int Left)
    {
        /// <summary>Survive, with as much health as possible, and not still be chased: dying is the worst.</summary>
        public double Score => Died ? -20 : Health - 3 * Threats;
    }

    [Test]
    public async Task CollectFights()
    {
        var count = int.Parse(Environment.GetEnvironmentVariable("VERTEX_FIGHTS") ?? "60");
        Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
        var done = File.Exists(DataFile) ? File.ReadAllLines(DataFile).Length : 0;
        var rng = new Random(1000 + done); // more fights later add new situations, not repeats
        await using var server = await PaperServer.StartAsync("fights", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "normal" });
        foreach (var cmd in new[] { "gamerule doMobSpawning false", "gamerule doDaylightCycle false", "time set midnight", "gamerule doImmediateRespawn true",
            "gamerule doMobLoot false", "gamerule showDeathMessages false" })
            await server.CommandAsync(cmd);
        await using var client = new MinecraftClient(GameProfile.Offline(Name), new ClientOptions());
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1))) await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int cx = (int)Math.Floor(px), cz = (int)Math.Floor(pz);
        // A walled field: room to run, and nobody falls off the edge (a floating platform made fleeing look deadly).
        await server.CommandAsync($"fill {cx - 31} 200 {cz - 31} {cx + 31} 206 {cz + 31} minecraft:air");
        await server.CommandAsync($"fill {cx - 31} 199 {cz - 31} {cx + 31} 199 {cz + 31} minecraft:stone");
        foreach (var (x1, z1, x2, z2) in new[] { (-31, -31, 31, -31), (-31, 31, 31, 31), (-31, -31, -31, 31), (31, -31, 31, 31) })
            await server.CommandAsync($"fill {cx + x1} 200 {cz + z1} {cx + x2} 203 {cz + z2} minecraft:stone");
        await server.CommandAsync($"setworldspawn {cx} 200 {cz}");
        var skills = new Skills(client);

        for (var i = 0; i < count; i++)
        {
            var s = new Scenario(Mobs[rng.Next(Mobs.Length)], 1 + rng.Next(3), 4 + 2 * rng.Next(9), Armor[rng.Next(Armor.Length)], Weapons[rng.Next(Weapons.Length)]);
            if (s.Mob == "vindicator") s = s with { Count = 1 };
            var outcomes = new Dictionary<ReflexAction, Outcome>();
            // Alternate which goes first, so nothing about going first favours one action.
            foreach (var action in i % 2 == 0 ? new[] { ReflexAction.Fight, ReflexAction.Flee } : [ReflexAction.Flee, ReflexAction.Fight])
                if (await EpisodeAsync(server, client, skills, cx, cz, s, action, rng) is { } o) outcomes[action] = o;
            if (outcomes.Count < 2) { TestContext.Out.WriteLine($"{s}: no hit to react to, skipped"); continue; }
            var line = new JsonObject
            {
                ["id"] = done + i,
                ["scenario"] = JsonSerializer.SerializeToNode(s),
                ["fight"] = JsonSerializer.SerializeToNode(outcomes[ReflexAction.Fight]),
                ["flee"] = JsonSerializer.SerializeToNode(outcomes[ReflexAction.Flee]),
            };
            await File.AppendAllTextAsync(DataFile, line.ToJsonString() + "\n");
            TestContext.Out.WriteLine($"{i + 1}/{count} {s}: fight {outcomes[ReflexAction.Fight].Score:0.#} ({Describe(outcomes[ReflexAction.Fight])}), flee {outcomes[ReflexAction.Flee].Score:0.#} ({Describe(outcomes[ReflexAction.Flee])})");
        }
    }

    private static string Describe(Outcome o) => o.Died ? "died" : $"health {o.Health:0}, {o.Threats} near, {o.Left} left";

    /// <summary>
    /// One fight: set the situation up, wait for the first hit, then keep reacting the same way (fight, or run) every
    /// time it is hit, for 20 seconds. Null when nothing hit it within 15 seconds.
    /// </summary>
    private static async Task<Outcome?> EpisodeAsync(PaperServer server, MinecraftClient client, Skills skills, int cx, int cz, Scenario s, ReflexAction action, Random rng)
    {
        await server.CommandAsync("kill @e[type=!minecraft:player]");
        await Task.Delay(1100); // the death animation: killed mobs stay a moment
        await Wait.UntilAsync(() => client.Health > 0, TimeSpan.FromSeconds(10), () => "respawned");
        await server.CommandAsync($"clear {Name}");
        await server.CommandAsync($"effect clear {Name}");
        await server.CommandAsync($"tp {Name} {cx + 0.5} 200 {cz + 0.5}");
        await server.CommandAsync($"effect give {Name} minecraft:instant_health 1 10 true");
        await server.CommandAsync($"effect give {Name} minecraft:saturation 1 10 true");
        await Wait.UntilAsync(() => client.Health >= 20, TimeSpan.FromSeconds(10), () => $"healed: {client.Health}");
        // Right after joining or respawning a player cannot be hurt for a moment: try again until it takes.
        for (var tries = 0; s.Health < 20 && Math.Abs(client.Health - s.Health) >= 0.6; tries++)
        {
            if (tries == 10) Assert.Fail($"health {client.Health}, want {s.Health}");
            await server.CommandAsync($"damage {Name} {client.Health - s.Health} minecraft:generic_kill");
            await client.WaitUntilAsync(() => Math.Abs(client.Health - s.Health) < 0.6, 20);
        }
        if (s.Armor != "")
            foreach (var (slot, piece) in new[] { ("head", "helmet"), ("chest", "chestplate"), ("legs", "leggings"), ("feet", "boots") })
                await server.CommandAsync($"item replace entity {Name} armor.{slot} with minecraft:{s.Armor}_{piece}");
        if (s.Weapon != "")
        {
            await server.CommandAsync($"give {Name} minecraft:{s.Weapon}");
            await Wait.UntilAsync(() => client.Inventory.Count(s.Weapon) > 0, TimeSpan.FromSeconds(10), () => "weapon");
        }
        await client.WaitTicksAsync(10);
        for (var k = 0; k < s.Count; k++)
        {
            var a = rng.NextDouble() * 2 * Math.PI;
            var d = 4 + rng.NextDouble() * 2;
            await server.CommandAsync($"summon minecraft:{s.Mob} {cx + 0.5 + Math.Cos(a) * d:0.##} 200 {cz + 0.5 + Math.Sin(a) * d:0.##} {{PersistenceRequired:1b}}");
        }

        // The first hit.
        var before = client.Health;
        if (!await client.WaitUntilAsync(() => client.Health < before - 0.01f, 300)) return null;
        var died = false;
        void Watch() { if (client.Health <= 0) died = true; }
        client.Tick += Watch;
        try
        {
            var attacker = Nearest(client);
            if (attacker == null) return null;
            var features = ReflexFeatures.From(client, attacker);
            using var round = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!round.IsCancellationRequested && !died && attacker != null)
            {
                try
                {
                    if (action == ReflexAction.Fight) await skills.AttackAsync(attacker.Name.Bare(), round.Token);
                    else await skills.FleeFromAsync(attacker, round.Token);
                }
                catch (OperationCanceledException) { break; }
                // Then whatever comes: the next hit is reacted to the same way.
                var health = client.Health;
                try { await client.WaitUntilAsync(() => client.Health < health - 0.01f || died, 400, round.Token); }
                catch (OperationCanceledException) { break; }
                attacker = Nearest(client);
            }
            client.Controls.Clear();
            var (x, y, z) = client.Position;
            var mobs = client.Entities.Values.Where(e => e.Name == "minecraft:" + s.Mob).ToList();
            return new Outcome(features, died, died ? 0 : client.Health, mobs.Count(e => e.DistanceTo(x, y, z) < 6), mobs.Count);
        }
        finally { client.Tick -= Watch; }
    }

    private static SpawnDev.Minecraft.Entities.Entity? Nearest(MinecraftClient c)
    {
        var (x, y, z) = c.Position;
        return c.Entities.Values.Where(e => Skills.Hostile.Contains(e.Name) && e.DistanceTo(x, y, z) < 16).OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
    }

    // ---------------------------------------------------------------- training

    private sealed record Fight(int Id, float[] FightFeatures, float[] FleeFeatures, double FightScore, double FleeScore)
    {
        public ReflexAction Better => FightScore >= FleeScore ? ReflexAction.Fight : ReflexAction.Flee;
        public double Score(ReflexAction a) => a == ReflexAction.Fight ? FightScore : FleeScore;
    }

    private static List<Fight> ReadFights() => File.ReadAllLines(DataFile).Where(l => l.Length > 0).Select(l =>
    {
        var n = JsonNode.Parse(l)!;
        Outcome O(string k) => n[k].Deserialize<Outcome>()!;
        var (f, r) = (O("fight"), O("flee"));
        return new Fight(n["id"]!.GetValue<int>(), f.Features, r.Features, f.Score, r.Score);
    }).ToList();

    [Test]
    public async Task TrainPolicy()
    {
        var fights = ReadFights();
        TestContext.Out.WriteLine($"{fights.Count} situations; fighting was better in {fights.Count(f => f.Better == ReflexAction.Fight)}");
        using var context = Context.Create(b => b.CPU());
        using var cpu = context.CreateCPUAccelerator(0);

        // 5-fold cross validation: every situation is judged by a network that never saw it.
        double rule = 0, learned = 0, fight = 0, flee = 0, best = 0;
        int differ = 0, learnedBetter = 0, ruleBetter = 0;
        var differences = new List<string>();
        for (var fold = 0; fold < 5; fold++)
        {
            var test = fights.Where(f => f.Id % 5 == fold).ToList();
            using var model = await TrainAsync(cpu, fights.Where(f => f.Id % 5 != fold).ToList());
            foreach (var f in test)
            {
                var p = (await model.PredictProbsAsync(f.FightFeatures, 1))[(int)ReflexAction.Fight];
                var l = p >= 0.5 ? ReflexAction.Fight : ReflexAction.Flee;
                var r = await RuleReflexPolicy.Instance.DecideAsync(f.FightFeatures, 0.5);
                learned += f.Score(l);
                rule += f.Score(r);
                if (l != r)
                {
                    differ++;
                    if (f.Score(l) > f.Score(r)) learnedBetter++;
                    if (f.Score(r) > f.Score(l)) ruleBetter++;
                    differences.Add($"  #{f.Id}: health {f.FightFeatures[0] * 20:0}, learned {l} ({f.Score(l):0.#}) vs rule {r} ({f.Score(r):0.#})");
                }
                fight += f.FightScore;
                flee += f.FleeScore;
                best += Math.Max(f.FightScore, f.FleeScore);
            }
        }
        var n = fights.Count;
        TestContext.Out.WriteLine($"the two chose differently in {differ} of {fights.Count}: the network's choice came out better in {learnedBetter}, the rule's in {ruleBetter}");
        foreach (var d in differences) TestContext.Out.WriteLine(d);
        TestContext.Out.WriteLine($"mean score on unseen fights: learned {learned / n:0.00}, rule {rule / n:0.00}, always fight {fight / n:0.00}, always flee {flee / n:0.00}, best possible {best / n:0.00}");

        if (learned > rule)
        {
            using var final = await TrainAsync(cpu, fights);
            await File.WriteAllBytesAsync(WeightsFile, await final.ExportWeightsAsync());
            TestContext.Out.WriteLine($"the network beat the rule: wrote {WeightsFile}");
        }
        else
        {
            if (File.Exists(WeightsFile)) File.Delete(WeightsFile);
            TestContext.Out.WriteLine("the network did not beat the rule: Vertex keeps the rule (no weights shipped)");
        }
    }

    private static async Task<SpawnDev.ILGPU.ML.Training.TrainableModel> TrainAsync(ILGPU.Runtime.Accelerator acc, List<Fight> train)
    {
        // Both moments a situation was seen (the fight run's first hit and the flee run's) carry its label. A tie
        // (died either way, or both came out the same) says nothing about which is better: left out.
        var samples = train.Where(f => Math.Abs(f.FightScore - f.FleeScore) >= 1)
            .SelectMany(f => new[] { (f.FightFeatures, f.Better), (f.FleeFeatures, f.Better) }).ToList();
        const int batch = 16;
        var model = LearnedReflexPolicy.CreateModel(acc, batch);
        var rng = new Random(7);
        for (var epoch = 0; epoch < 400; epoch++)
        {
            var order = samples.OrderBy(_ => rng.Next()).ToList();
            for (var b = 0; b + batch <= order.Count; b += batch)
            {
                var x = order.Skip(b).Take(batch).SelectMany(s => s.Item1).ToArray();
                var y = order.Skip(b).Take(batch).Select(s => (int)s.Item2).ToArray();
                model.TrainStep(x, y, batch, 0.02f);
            }
        }
        await model.FlushAsync();
        return model;
    }
}
