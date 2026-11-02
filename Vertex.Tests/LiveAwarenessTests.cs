using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Looking around. Live on AubsCraft 2026-11-02 (TJ): "he is swimming out into the water and there is no land in sight in front of him, and he is saying he can't see land. It is behind him. My
/// concern is that he did not think to look around. Looking around is a basic tool that gives tons of useful info. Being aware of surroundings is a large part of existing in a world. Being able
/// and willing to swim across water is needed." So: a survey of the ground around him in the eight compass directions (from the chunks he already has), a look_around tool, a line in his status
/// when it matters (in the water: where the land is), and the way out of the water follows what he sees, however far. Real Paper server; the lake is a stage in the sky.
/// </summary>
[NonParallelizable]
public class LiveAwarenessTests
{
    private const int Port = 25992;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;
    private readonly List<(int X, int Z, int Length)> _stages = [];

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("aware", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful", ["view-distance"] = "8", ["simulation-distance"] = "4" }); // a real server sends far more than the test default of 4 chunks
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("gamerule randomTickSpeed 0");   // the test world is a cold biome: the surface of a built lake froze while he swam and trapped him under the ice (seen 2026-11-02)
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task Stop() { if (_server != null) await _server.DisposeAsync(); }

    /// <summary>A stone floor (top at y 200) 41 wide and `length` long to the south, air above it, forceloaded first (a fill into unloaded chunks does nothing).</summary>
    private async Task<(MinecraftClient Client, int X, int Z)> StageAsync(string name, int length)
    {
        var client = new MinecraftClient(GameProfile.Offline(name), new ClientOptions { Log = m => TestContext.Out.WriteLine($"[{name}] {m}") });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"forceload add {ax - 24} {az - 24} {ax + 24} {az + length + 8}");
        await Task.Delay(3000);
        await _server.CommandAsync("kill @e[type=!minecraft:player]");
        // Every test builds near the same spawn point in this one world, so an earlier test's floor, water and shore would be land (or water) in THIS test's look. Clear every earlier stage
        // first (and remember this one).
        foreach (var (ox, oz, olen) in _stages)
            for (var z0 = oz - 20; z0 <= oz + olen; z0 += 30)   // 41 x 20 x 30 = 24,600 blocks: a fill may touch 32,768 (45,000 was refused, silently, and the old stage stayed)
                await _server.CommandAsync($"fill {ox - 20} 196 {z0} {ox + 20} 215 {Math.Min(z0 + 29, oz + olen)} minecraft:air");
        _stages.Add((ax, az, length));
        for (var z0 = az - 20; z0 <= az + length; z0 += 40)
        {
            var z1 = Math.Min(z0 + 39, az + length);
            await _server.CommandAsync($"fill {ax - 20} 197 {z0} {ax + 20} 199 {z1} minecraft:stone");
            await _server.CommandAsync($"fill {ax - 20} 200 {z0} {ax + 20} 215 {z1} minecraft:air");
        }
        return (client, ax, az);
    }

    private async Task StandAsync(MinecraftClient client, string name, int ax, double y, int az)
    {
        await _server.CommandAsync($"tp {name} {ax + 0.5} {y} {az + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.X - (ax + 0.5)) < 0.3 && Math.Abs(client.Position.Z - (az + 0.5)) < 0.3, Timeout, () => $"at {client.Position}");
        await client.WaitTicksAsync(30);
    }

    [Test]
    public async Task InAPond_HeSeesWhichWayTheShoreIs_AndIsTold()
    {
        var (client, ax, az) = await StageAsync("VertexPond", 30);
        await using var _ = client;
        // A lake 2 deep over the northern part, a shore (land) from 13 blocks south of him on.
        await _server.CommandAsync($"fill {ax - 20} 198 {az - 20} {ax + 20} 199 {az + 12} minecraft:water");
        await StandAsync(client, "VertexPond", ax, 200, az);
        await Wait.UntilAsync(() => SurroundingsView.Look(client.World, client.Position, 12).InWater, Timeout, () => $"at {client.Position}");
        var skills = new Skills(client);

        var view = skills.Survey(96);
        TestContext.Out.WriteLine(view.Describe());
        Assert.That(view.InWater, Is.True);
        Assert.That(view.WaterDepth, Is.GreaterThanOrEqualTo(1));
        Assert.That(view.NearestLand, Is.Not.Null);
        Assert.That(view.NearestLand!.Value.Direction, Is.EqualTo("S"), "the shore is south");
        Assert.That(view.NearestLand.Value.Distance, Is.InRange(12, 16));
        Assert.That(view.Rays.Single(r => r.Direction == "N").Nearest(SurfaceKind.Land), Is.Null, "no land to the north (water, then nothing)");

        // What the tool says and what his status says.
        var said = skills.LookAround();
        Assert.That(said, Does.Contain("I am in the water").And.Contain("S: water").And.Contain("land"), said);
        var line = skills.Awareness();
        TestContext.Out.WriteLine("status line: " + line);
        Assert.That(line, Does.Contain("You are in the water").And.Contain("nearest land you can see is S"), line);
    }

    [Test]
    public async Task LandFarBeyondTheOldSearch_IsSeenAndSwumTo()
    {
        var (client, ax, az) = await StageAsync("VertexFar", 64);
        await using var _ = client;
        // A lake from him to 55 blocks south: the old search for dry ground (16, then 40 blocks) found nothing, and he said "I can't see dry land close by". The shore starts at 56.
        await _server.CommandAsync($"fill {ax - 20} 198 {az - 20} {ax + 20} 199 {az + 20} minecraft:water");
        await _server.CommandAsync($"fill {ax - 20} 198 {az + 21} {ax + 20} 199 {az + 55} minecraft:water");
        await StandAsync(client, "VertexFar", ax, 200, az);
        await Wait.UntilAsync(() => SurroundingsView.Look(client.World, client.Position, 12).InWater && client.World.IsLoaded(ax, az + 62), Timeout, () => $"at {client.Position}");
        var skills = new Skills(client);
        Assert.That(skills.Survey(96).NearestLand!.Value.Distance, Is.InRange(54, 60), "he can see the shore, 56 blocks away");

        (bool ok, string text) result;
        using (var c = new CancellationTokenSource(TimeSpan.FromSeconds(120))) result = await skills.LeaveWaterAsync(c.Token);
        TestContext.Out.WriteLine($"{result.ok} {result.text}; at {client.Position}");
        Assert.That(result.ok, Is.True, result.text);
        Assert.That(client.Position.Z, Is.GreaterThan(az + 54), "he swam all the way to the shore");
        Assert.That(client.BodySnapshot().InWater, Is.False);
    }

    [Test]
    public async Task WithNoLandInSight_HeSaysSo_AfterLookingAround()
    {
        var (client, ax, az) = await StageAsync("VertexSea", 20);
        await using var _ = client;
        await _server.CommandAsync($"fill {ax - 20} 198 {az - 20} {ax + 20} 199 {az + 20} minecraft:water");   // water to the edge of the stage, then nothing
        await StandAsync(client, "VertexSea", ax, 200, az);
        await Wait.UntilAsync(() => SurroundingsView.Look(client.World, client.Position, 12).InWater, Timeout, () => $"at {client.Position}");
        var skills = new Skills(client);
        TestContext.Out.WriteLine("LOOK:\n" + skills.LookAround() + $"\nat {client.Position}; stage centre {ax},{az}");
        var line = skills.Awareness();
        TestContext.Out.WriteLine("status line: " + line);
        Assert.That(line, Does.Contain("You are in the water").And.Contain("can't see any land").And.Contain("look_around"), line);
        (bool ok, string text) result;
        using (var c = new CancellationTokenSource(TimeSpan.FromSeconds(30))) result = await skills.LeaveWaterAsync(c.Token);
        TestContext.Out.WriteLine(result.text);
        Assert.That(result.ok, Is.False);
        Assert.That(result.text, Does.Contain("I looked around").And.Contain("can't see any land"), "he says he looked, not just that he is stuck");
    }

    /// <summary>
    /// TJ, 2026-11-02, live: "he is standing on a piece of land 1 [block] with water on 3 sides and on the 4th a 3 block high wall of dirt and he has no clue what to do. We can't account for every
    /// situation. He needs to be able to learn and try things." The exact predicament. He must notice he is stranded (the status says so), try to get off with everything he can do (swim, dig),
    /// get onto real land, and what he worked out must come out as a lesson (the brain writes it to his memory).
    /// </summary>
    [Test]
    public async Task StrandedOnAOneBlockIsland_HeTriesWayOff_AndLearnsWhatWorked()
    {
        var (client, ax, az) = await StageAsync("VertexIsland", 30);
        await using var _ = client;
        await _server.CommandAsync($"fill {ax - 20} 198 {az - 20} {ax + 20} 199 {az + 20} minecraft:water");          // a lake
        await _server.CommandAsync($"fill {ax - 20} 198 {az - 20} {ax + 20} 199 {az - 2} minecraft:stone");            // real land to the north
        await _server.CommandAsync($"fill {ax - 6} 198 {az - 1} {ax + 6} 202 {az - 1} minecraft:dirt");                // a wall of dirt, 3 above the ground, between it and him
        await _server.CommandAsync($"fill {ax} 198 {az} {ax} 199 {az} minecraft:stone");                               // his island: one block
        await StandAsync(client, "VertexIsland", ax, 200, az);
        await Wait.UntilAsync(() => client.OnGround && client.World.GetBlock(ax, 202, az - 1)?.Name == "minecraft:dirt", Timeout, () => $"at {client.Position}");
        var skills = new Skills(client);
        var lessons = new List<string>();
        skills.Learned += lessons.Add;

        Assert.That(skills.WalkablePatch().Count, Is.EqualTo(1), "a one-block island");
        var line = skills.Awareness();
        TestContext.Out.WriteLine("status line: " + line);
        Assert.That(line, Does.Contain("tiny patch of land (1 block)").And.Contain("swim, dig"), "the status tells him where he is and what he can do");

        (bool ok, string text) result;
        using (var c = new CancellationTokenSource(TimeSpan.FromSeconds(120))) result = await skills.GetOffATinyPatchAsync(c.Token);
        TestContext.Out.WriteLine($"{result.ok}: {result.text}; at {client.Position}");
        Assert.That(result.ok, Is.True, result.text);
        Assert.That(skills.WalkablePatch().Count, Is.GreaterThanOrEqualTo(Skills.TinyPatch), "he is on real land now");
        Assert.That(client.Position.Z, Is.LessThan(az), "off the island cell, on the wall side: connected to the land beyond");
        Assert.That(lessons, Has.Count.EqualTo(1));
        Assert.That(lessons[0], Does.Contain("tiny patch of land (1 block)").And.Contain("That works."), lessons[0]);
        Assert.That(lessons[0], Does.Contain("digging").Or.Contain("swimming"), "it says HOW: " + lessons[0]);
    }

    /// <summary>The same predicament, with nobody calling the skill: idle on the island, the reflex notices he is stranded (twice, 3 seconds apart) and sets off by itself.</summary>
    [Test]
    public async Task Idle_OnAOneBlockIsland_TheReflexNoticesAndTriesBy_Itself()
    {
        var (client, ax, az) = await StageAsync("VertexIsle", 30);
        await using var _ = client;
        await _server.CommandAsync($"fill {ax - 20} 198 {az - 20} {ax + 20} 199 {az + 20} minecraft:water");
        await _server.CommandAsync($"fill {ax - 20} 198 {az - 20} {ax + 20} 199 {az - 2} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 6} 198 {az - 1} {ax + 6} 202 {az - 1} minecraft:dirt");
        await _server.CommandAsync($"fill {ax} 198 {az} {ax} 199 {az} minecraft:stone");
        await StandAsync(client, "VertexIsle", ax, 200, az);
        await Wait.UntilAsync(() => client.OnGround && client.World.GetBlock(ax, 202, az - 1)?.Name == "minecraft:dirt", Timeout, () => $"at {client.Position}");
        var skills = new Skills(client);
        var tasks = new TaskRunner();
        var lessons = new List<string>();
        skills.Learned += lessons.Add;
        using var reflexes = new Reflexes(client, skills, tasks, () => new PlayStyle()) { Enabled = false };
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            Assert.That(skills.WalkablePatch().Count, Is.EqualTo(1), "with the reflexes off he stays on the island (nothing else moves him)");
            reflexes.Enabled = true;
            // The lesson, not just "on a big patch": he is connected to land as soon as he stands in the tunnel, a moment before the skill has finished and said what it learned.
            await Wait.UntilAsync(() => { lock (lessons) return lessons.Count > 0; }, TimeSpan.FromSeconds(90), () => $"no lesson yet: at {client.Position}, patch {skills.WalkablePatch().Count}, task {tasks.Current}");
            Assert.That(skills.WalkablePatch().Count, Is.GreaterThanOrEqualTo(Skills.TinyPatch));
            lock (lessons) { Assert.That(lessons, Has.Count.EqualTo(1)); Assert.That(lessons[0], Does.Contain("That works."), lessons[0]); }
        }
        finally { reflexes.Enabled = false; tasks.Stop(); }
    }

    [Test]
    public async Task OnPlainLand_TheStatusCostsNothing_ButAnEdgeAndLavaAreMentioned()
    {
        var (client, ax, az) = await StageAsync("VertexEdge", 20);
        await using var _ = client;
        var skills = new Skills(client);
        await StandAsync(client, "VertexEdge", ax, 200, az);
        Assert.That(skills.Awareness(), Is.Null, "nothing notable in the middle of a wide floor: the prompt gets no extra line");

        await StandAsync(client, "VertexEdge", ax + 18, 200, az);   // the floor ends 2 blocks east of here, over nothing
        var edge = skills.Awareness();
        TestContext.Out.WriteLine("edge: " + edge);
        Assert.That(edge, Does.Contain("A drop of").And.Match(@", (NE|E|SE), "), edge);   // the floor ends 2 blocks east: the edge is 4 blocks off to the east, north-east or south-east

        await StandAsync(client, "VertexEdge", ax, 200, az);
        await _server.CommandAsync($"fill {ax - 9} 199 {az - 1} {ax - 7} 199 {az + 1} minecraft:lava");       // a lava pool 7 to 9 blocks west
        await client.WaitTicksAsync(20);
        var lava = skills.Awareness();
        TestContext.Out.WriteLine("lava: " + lava);
        Assert.That(lava, Does.Contain("Lava W"), lava);
    }
}
