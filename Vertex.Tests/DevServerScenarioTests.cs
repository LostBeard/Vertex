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
}
