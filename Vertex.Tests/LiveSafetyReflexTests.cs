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

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("safety", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "easy" }); // not peaceful: that removes hostile mobs at once
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("gamerule mobGriefing false");
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
        await _server.CommandAsync($"fill {ax - 20} 197 {az - 20} {ax + 20} 199 {az + 20} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 20} 200 {az - 20} {ax + 20} 203 {az + 20} minecraft:air");
        foreach (var build in extra) await _server.CommandAsync(build(ax, az));
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => client.World.IsLoaded(ax - 20, az - 20) && client.World.IsLoaded(ax + 20, az + 20)
            && client.World.IsLoaded(ax - 20, az + 20) && client.World.IsLoaded(ax + 20, az - 20), Timeout, () => "floor chunks");
        await client.WaitTicksAsync(20);
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
        var tasks = new TaskRunner();
        using var reflexes = new Reflexes(client, skills, tasks, () => new PlayStyle { Bravery = 1.0 }) { Enabled = false };
        try
        {
            // Control: reflexes off, he stays right there.
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.That(Away(client, monster), Is.LessThan(6), "with the reflexes off he stands where he is");

            reflexes.Enabled = true;
            await Wait.UntilAsync(() => Away(client, monster) >= 9.5, TimeSpan.FromSeconds(30), () => $"{Away(client, monster):0.0} blocks from the {mob}; task {tasks.Current}");
            Assert.That(client.Health, Is.EqualTo(20f), "never hurt");
            Assert.That(client.Entities.ContainsKey(monster.Id), "and he did not attack it (a brave character does not pick fights)");
            Assert.That(monster.Health ?? 20f, Is.GreaterThanOrEqualTo(20f));
        }
        finally { reflexes.Enabled = false; tasks.Stop(); await _server.CommandAsync("kill @e[tag=vavoid]"); }
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
