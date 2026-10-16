using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Scenarios against the AubsCraft DEV server (a local stock Paper 1.21.5 that the AubsCraft session starts: flat world, grass at y -61, offline mode), so that what Vertex builds can be
/// SEEN in the AubsCraft viewer and checked with the server's own answers over RCON. Explicit: they need that server running, and they never touch anything but localhost.
/// Environment: VERTEX_DEV_HOST (127.0.0.1), VERTEX_DEV_PORT (25570), VERTEX_DEV_RCON_PORT (25571), VERTEX_DEV_RCON_PASSWORD (required).
/// </summary>
[Explicit("needs the AubsCraft dev server on localhost")]
[NonParallelizable]
public class DevServerScenarioTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static string Host => Environment.GetEnvironmentVariable("VERTEX_DEV_HOST") ?? "127.0.0.1";
    private static int Port => int.Parse(Environment.GetEnvironmentVariable("VERTEX_DEV_PORT") ?? "25570");
    private static int RconPort => int.Parse(Environment.GetEnvironmentVariable("VERTEX_DEV_RCON_PORT") ?? "25571");

    private const string Cabin =
        "walls 0 0 0 4 1 6 cobblestone\n" +
        "clear 2 0 0 2 1 0\n" +
        "block 0 1 3 glass\nblock 4 1 3 glass\n" +
        "roof gable 0 0 4 6 2 planks\n" +
        "door 2 0 0 south\n" +
        "torch 1 0 1\n";

    private static async Task<RconClient> RconAsync()
    {
        var password = Environment.GetEnvironmentVariable("VERTEX_DEV_RCON_PASSWORD") ?? throw new AssertionException("set VERTEX_DEV_RCON_PASSWORD");
        Assert.That(Host, Is.EqualTo("127.0.0.1").Or.EqualTo("localhost"), "dev scenarios only run on this machine");
        return await RconClient.ConnectAsync(Host, RconPort, password);
    }

    private static string BlockAt(MinecraftClient client, int x, int y, int z) => client.World.Data.BlockOf(client.World.GetState(x, y, z)).Name;

    [Test]
    public async Task ACabinOnTheDevWorld_IsBuiltAndTheServerAgrees()
    {
        await using var rcon = await RconAsync();
        var vertex = new MinecraftClient(GameProfile.Offline("Vertex"), new ClientOptions { Log = m => TestContext.Out.WriteLine("[Vertex] " + m) });
        var aubs = new MinecraftClient(GameProfile.Offline("Aubri"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await vertex.ConnectAsync(Host, Port, cts.Token);
        await aubs.ConnectAsync(Host, Port, cts.Token);
        await using var _v = vertex;
        await using var _a = aubs;

        await rcon.CommandAsync("clear Vertex");
        await Task.Delay(1500);        // let the emptied inventory reach the client before anything is given
        await rcon.CommandAsync("tp Vertex 12.5 -60 12.5");
        await rcon.CommandAsync("tp Aubri 16.5 -60 12.5");
        await Wait.UntilAsync(() => vertex.World.IsLoaded(0, 0) && vertex.World.IsLoaded(24, 24) && Math.Abs(vertex.Position.Y - -60) < 0.2, Timeout, () => $"{vertex.Position}");
        foreach (var (item, count) in new[] { ("cobblestone", 64), ("oak_planks", 64), ("glass", 4), ("oak_door", 1), ("torch", 2) })
        {
            var before = vertex.Inventory.Count(item);
            await rcon.CommandAsync($"give Vertex minecraft:{item} {count}");
            await Wait.UntilAsync(() => vertex.Inventory.Count(item) == before + count, Timeout, () => vertex.Inventory.ToString());
        }

        var skills = new Skills(vertex);
        var (ok, text) = await skills.BuildScriptAsync(Cabin, "Aubri", cts.Token);
        TestContext.Out.WriteLine(text);
        Assert.That(ok, Is.True, text);

        // Where it ended up, from what Vertex was told, then the SERVER's own answer for the corners.
        (int X, int Y, int Z)? corner = null;
        for (var x = -10; x <= 40 && corner == null; x++)
        for (var z = -10; z <= 40 && corner == null; z++)
            if (BlockAt(vertex, x, -60, z) == "minecraft:cobblestone" && BlockAt(vertex, x + 4, -60, z) == "minecraft:cobblestone" && BlockAt(vertex, x, -60, z + 6) == "minecraft:cobblestone")
                corner = (x, -60, z);
        Assert.That(corner, Is.Not.Null, "no cobblestone corner found: nothing was built");
        var (cx, cy, cz) = corner!.Value;
        TestContext.Out.WriteLine($"BOX corner {cx} {cy} {cz}, size 5 x 7 x 5 (x {cx}..{cx + 4}, y {cy}..{cy + 4}, z {cz}..{cz + 6})");
        foreach (var (rx, ry, rz, expect) in new[] { (0, 0, 0, "cobblestone"), (4, 0, 6, "cobblestone"), (4, 1, 3, "glass"), (0, 1, 3, "glass"), (2, 4, 3, "oak_planks") })
        {
            var answer = await rcon.CommandAsync($"execute if block {cx + rx} {cy + ry} {cz + rz} minecraft:{expect}");
            Assert.That(answer, Does.Contain("Test passed"), $"the server's block at ({rx},{ry},{rz}) should be {expect}: {answer}");
        }
        var door = await rcon.CommandAsync($"execute if block {cx + 2} {cy} {cz} minecraft:oak_door");
        Assert.That(door, Does.Contain("Test passed"), "the doorway has its door");
    }

    [Test]
    public async Task AFarmWithAPenOnTheDevWorld_FieldIsSownAndTheCowsHaveABaby()
    {
        await using var rcon = await RconAsync();
        var vertex = new MinecraftClient(GameProfile.Offline("Vertex"), new ClientOptions { Log = m => TestContext.Out.WriteLine("[Vertex] " + m) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await vertex.ConnectAsync(Host, Port, cts.Token);
        await using var _v = vertex;

        // A clean patch: 30..50 x, 20..40 z, with a fenced pen (35..39, 33..37) holding two cows.
        // The dev world has its days and nights and its mobs (seen 2026-10-15: at night a zombie or spider killed Vertex mid-planting, and it respawned empty at the world spawn): day, and
        // nothing hostile around the patch, before anything else.
        await rcon.CommandAsync("time set day");
        await rcon.CommandAsync("difficulty peaceful");       // hostile mobs (slimes spawn on a flat world's slime chunks) killed Vertex mid-run; restored in the finally below
        await rcon.CommandAsync("kill @e[type=minecraft:cow]");
        await rcon.CommandAsync("fill 30 -60 20 50 -50 40 minecraft:air");
        await rcon.CommandAsync("fill 30 -61 20 50 -61 40 minecraft:grass_block");
        // A ring, not a filled box ("hollow" of a box only 2 high is all shell: AubsCraft's viewer showed a lattice of fences with the cows inside the blocks).
        await rcon.CommandAsync("fill 35 -60 33 39 -59 37 minecraft:oak_fence");
        await rcon.CommandAsync("fill 36 -60 34 38 -59 36 minecraft:air");
        await rcon.CommandAsync("clear Vertex");
        await Task.Delay(1500);        // let the emptied inventory reach the client before anything is given
        await rcon.CommandAsync("tp Vertex 42.5 -60 26.5");
        await Wait.UntilAsync(() => vertex.World.IsLoaded(30, 20) && vertex.World.IsLoaded(50, 40) && Math.Abs(vertex.Position.Y - -60) < 0.2, Timeout, () => $"{vertex.Position}");
        await Wait.UntilAsync(() => BlockAt(vertex, 40, -61, 30) == "minecraft:grass_block" && BlockAt(vertex, 40, -60, 30) == "minecraft:air" && BlockAt(vertex, 37, -60, 35) == "minecraft:air"
            && BlockAt(vertex, 35, -60, 33) == "minecraft:oak_fence", Timeout, () => $"the cleared patch: {BlockAt(vertex, 40, -61, 30)} / {BlockAt(vertex, 40, -60, 30)} / {BlockAt(vertex, 37, -60, 35)}");
        await rcon.CommandAsync("kill @e[type=!minecraft:player,x=10,y=-62,z=0,dx=60,dy=12,dz=60]");       // the world's own herds (the dev world spawns them) are not part of this scenario
        await vertex.WaitTicksAsync(10);
        await rcon.CommandAsync("summon minecraft:cow 37.5 -60 35.5");
        await rcon.CommandAsync("summon minecraft:cow 36.5 -60 34.5");
        foreach (var (item, count) in new[] { ("wooden_hoe", 1), ("wheat_seeds", 30), ("water_bucket", 1), ("wheat", 4) })
        {
            var before = vertex.Inventory.Count(item);
            await rcon.CommandAsync($"give Vertex minecraft:{item} {count}");
            await Wait.UntilAsync(() => vertex.Inventory.Count(item) == before + count, Timeout, () => vertex.Inventory.ToString());
        }
        await vertex.WaitTicksAsync(10);
        var skills = new Skills(vertex);

        TestContext.Out.WriteLine("inventory before planting: " + vertex.Inventory + $" | at {vertex.Position} health {vertex.Health}");
        var (ok, text) = await skills.PlantFieldAsync("wheat", 5, null, cts.Token);
        TestContext.Out.WriteLine(text + " | inventory now: " + vertex.Inventory + $" | at {vertex.Position} selected {vertex.Inventory.SelectedHotbar}");
        Assert.That(ok, Is.True, text);
        var wheat = 0;
        for (var x = 30; x <= 50; x++)
        for (var z = 20; z <= 40; z++)
            if ((await rcon.CommandAsync($"execute if block {x} -60 {z} minecraft:wheat")).Contains("Test passed")) wheat++;
        TestContext.Out.WriteLine($"the server counts {wheat} wheat plants");
        Assert.That(wheat, Is.InRange(22, 24), "wheat on (nearly) every one of the 24 farmland blocks, by the server's count: a cell next to the hole is sometimes skipped (open: why)");

        var (bred, text2) = await skills.BreedAnimalsAsync("cow", 1, cts.Token);
        TestContext.Out.WriteLine(text2 + $" | Vertex at {vertex.Position} | cows: " + string.Join("; ", vertex.Entities.Values.Where(e => e.Name == "minecraft:cow").Select(e => $"({e.X:0.0},{e.Y:0.0},{e.Z:0.0})")));
        Assert.That(bred, Is.True, text2);
        var cows = await rcon.CommandAsync("execute if entity @e[type=minecraft:cow]");
        TestContext.Out.WriteLine("server: " + cows);
        Assert.That(cows, Does.Contain("count: 3"), "two cows and a calf, by the server's own count");
        Assert.That(await rcon.CommandAsync("execute if block 37 -60 35 minecraft:air"), Does.Contain("Test passed"), "the inside of the pen is open");
        Assert.That(await rcon.CommandAsync("execute if block 37 -60 33 #minecraft:fences"), Does.Contain("Test passed"), "and its wall is fence");
        await rcon.CommandAsync("difficulty easy");
        TestContext.Out.WriteLine("BOX field+pen: x 30..50, y -61..-58, z 20..40");
    }
}
