using SpawnDev.Minecraft;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// "I explored 40 blocks north and found gold, 7 wolfs, 1 cow" (live on AubsCraft, 2026-11-01; TJ, watching: "he's making stuff up"). The skill listed every notable ore in the chunk data within
/// 48 blocks, including ore buried inside rock that no player could see (an X-ray), and every animal the server had told it about, behind walls or not. A friend told "I found gold" thinks
/// he SAW gold. Now a block or an animal counts only if there is a clear line from his eyes to it. A real Paper server, a built stage: gold buried inside a platform and a wolf shut in a
/// stone box (neither can be seen), diamond ore and a cow in plain view (both can).
/// </summary>
[NonParallelizable]
public class LiveExploreHonestyTests
{
    private const int Port = 25986;
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("explore", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        await _server.CommandAsync("time set day");
    }

    [OneTimeTearDown]
    public async Task Stop() { if (_server != null) await _server.DisposeAsync(); }

    /// <summary>Live 2026-11-02: hovering in the air he "finished exploring north: I explored 0 blocks north" twice, and both went into his memory as things that happened. He walked nowhere.</summary>
    [Test]
    public async Task WhenHeCannotGoAnywhere_HeSaysSo_AndDoesNotClaimToHaveExplored()
    {
        await using var client = new MinecraftClient(GameProfile.Offline("VertexBoxed"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        await _server.CommandAsync($"fill {ax - 6} 197 {az - 6} {ax + 6} 199 {az + 6} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 6} 200 {az - 6} {ax + 6} 210 {az + 6} minecraft:air");
        await _server.CommandAsync($"fill {ax - 1} 200 {az - 1} {ax + 1} 202 {az + 1} minecraft:stone");   // a 3 by 3 block...
        await _server.CommandAsync($"fill {ax} 200 {az} {ax} 201 {az} minecraft:air");                      // ...with a hole one wide and two high: a cell
        await _server.CommandAsync($"tp VertexBoxed {ax + 0.5} 200 {az + 0.5}");
        await Wait.UntilAsync(() => Math.Abs(client.Position.X - (ax + 0.5)) < 0.1 && Math.Abs(client.Position.Z - (az + 0.5)) < 0.1 && client.OnGround, TimeSpan.FromSeconds(30), () => $"at {client.Position}");
        await client.WaitTicksAsync(20);

        var (ok, summary) = await new Skills(client).ExploreAsync("north", 40, null, cts.Token);
        TestContext.Out.WriteLine("HE SAID: " + summary);
        Assert.That(ok, Is.False, "he went nowhere: " + summary);
        Assert.That(summary, Does.Contain("couldn't really go anywhere").And.Not.Contain("finished"), summary);
    }

    [Test]
    public async Task HeReportsWhatHeCouldSee_NotWhatIsBuriedOrWalledIn()
    {
        await using var client = new MinecraftClient(GameProfile.Offline("VertexExplore"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);

        // The stage, all in the sky (natural terrain is far below): a platform 25 wide and 80 long, air above it. Forceloaded first: a fill into chunks nobody has loaded does nothing.
        await _server.CommandAsync($"forceload add {ax - 16} {az - 80} {ax + 16} {az + 16}");
        await Task.Delay(3000);
        await _server.CommandAsync($"fill {ax - 12} 197 {az - 75} {ax + 12} 199 {az + 5} minecraft:stone");
        await _server.CommandAsync($"fill {ax - 12} 200 {az - 75} {ax + 12} 215 {az + 5} minecraft:air");
        await _server.CommandAsync($"tp VertexExplore {ax + 0.5} 200 {az + 0.5}");
        // Far enough north (50 to 60 blocks) that none of it is in range when he starts: it comes into range as he walks, so it is NEWS.
        await _server.CommandAsync($"setblock {ax} 198 {az - 55} minecraft:gold_ore");                          // buried in the platform: invisible
        await _server.CommandAsync($"setblock {ax + 2} 200 {az - 58} minecraft:diamond_ore");                   // on top of the platform: in plain view
        await _server.CommandAsync($"summon minecraft:cow {ax + 3.5} 200 {az - 56.5} {{NoAI:1b}}");            // in plain view
        await _server.CommandAsync($"fill {ax + 8} 200 {az - 62} {ax + 12} 203 {az - 58} minecraft:stone");     // a closed stone box...
        await _server.CommandAsync($"fill {ax + 9} 200 {az - 61} {ax + 11} 202 {az - 59} minecraft:air");       // ...hollow inside
        await _server.CommandAsync($"summon minecraft:wolf {ax + 10.5} 200 {az - 59.5} {{NoAI:1b}}");           // a wolf inside it: invisible
        await Wait.UntilAsync(() => Math.Abs(client.Position.Z - (az + 0.5)) < 0.1 && client.OnGround, TimeSpan.FromSeconds(30), () => $"at {client.Position}");
        await client.WaitTicksAsync(30);

        var skills = new Skills(client);
        var (ok, summary) = await skills.ExploreAsync("north", 45, null, cts.Token);
        TestContext.Out.WriteLine("HE SAID: " + summary);

        Assert.That(ok, Is.True);
        // The controls: what really was in plain view IS reported, or this test could pass by reporting nothing at all.
        Assert.That(summary, Does.Contain("diamonds"), "the diamond ore in plain view must be reported");
        Assert.That(summary, Does.Contain("cow"), "the cow in plain view must be reported");
        // What no player could see is NOT.
        Assert.That(summary, Does.Not.Contain("gold"), "gold buried inside the platform cannot be seen: " + summary);
        Assert.That(summary, Does.Not.Contain("wolf"), "a wolf shut in a stone box cannot be seen: " + summary);
    }
}
