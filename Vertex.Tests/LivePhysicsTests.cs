using System.Globalization;
using System.Text.RegularExpressions;
using SpawnDev.Minecraft;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// M3 movement against a REAL Paper server: the server re-runs every move through its own collision and rejects
/// anything it disagrees with ("moved wrongly" / "moved too quickly", then a correction teleport). Each test drives
/// the body with controls only (no teleports of our own), then checks the server's stored position matches ours
/// and that it never complained.
/// </summary>
[NonParallelizable]
public partial class LivePhysicsTests
{
    private const int Port = 25926;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer() => _server = await PaperServer.StartAsync("physics", Port, TimeSpan.FromMinutes(4),
        new Dictionary<string, string> { ["difficulty"] = "easy", ["spawn-monsters"] = "false", ["spawn-animals"] = "false" });

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    private async Task<(MinecraftClient Client, int X, int Z)> JoinOnArenaAsync(string name, Func<int, int, string[]> build)
    {
        var client = new MinecraftClient(GameProfile.Offline(name));
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var (px, _, pz) = client.Position;
        int ax = (int)Math.Floor(px), az = (int)Math.Floor(pz);
        // An arena in the sky over spawn (always loaded, nothing natural in the way).
        await _server.CommandAsync($"fill {ax - 4} 200 {az - 6} {ax + 50} 215 {az + 6} minecraft:air");
        await _server.CommandAsync($"fill {ax - 4} 199 {az - 6} {ax + 50} 199 {az + 6} minecraft:stone");
        foreach (var cmd in build(ax, az)) await _server.CommandAsync(cmd);
        await _server.CommandAsync($"tp {name} {ax + 0.5} 200 {az + 0.5} -90 0"); // facing +X
        await Wait.UntilAsync(() => Math.Abs(client.Position.Y - 200) < 1e-9 && client.OnGround, Timeout,
            () => $"at {client.Position} onGround={client.OnGround}");
        _teleportsAtStart[name] = client.TeleportCount;
        return (client, ax, az);
    }

    [GeneratedRegex(@"\[(-?[\d.E-]+)d, (-?[\d.E-]+)d, (-?[\d.E-]+)d\]")]
    private static partial Regex PosRegex();

    private async Task<(double X, double Y, double Z)> ServerPositionAsync(string name)
    {
        var line = _server.WaitForLineAsync(l => l.Contains($"{name} has the following entity data"), Timeout);
        await _server.CommandAsync($"data get entity {name} Pos");
        var text = await line;
        var m = PosRegex().Match(text);
        Assert.That(m.Success, "unexpected position line: " + text);
        double P(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        return (P(1), P(2), P(3));
    }

    private readonly Dictionary<string, int> _teleportsAtStart = [];

    /// <summary>
    /// Paper rejects a move two ways: a big error (over 0.25 blocks) logs "moved wrongly"/"moved too quickly" and
    /// teleports back; a small one into a block is accepted and then silently corrected by a teleport. Both show up
    /// as a correction teleport on the client, so none may happen while we move by ourselves.
    /// </summary>
    private void AssertServerNeverObjected(MinecraftClient client, string name, int expectedTeleports = 0)
    {
        var complaints = _server.OutputTail(400).Where(l => l.Contains(name) && (l.Contains("moved wrongly") || l.Contains("moved too quickly"))).ToList();
        Assert.That(complaints, Is.Empty, "the server rejected our movement");
        Assert.That(client.TeleportCount - _teleportsAtStart[name], Is.EqualTo(expectedTeleports), "the server corrected our position");
    }

    private static async Task RunTicksAsync(MinecraftClient client, int ticks)
    {
        var count = 0;
        var done = new TaskCompletionSource();
        void OnMoved() { if (++count >= ticks) done.TrySetResult(); }
        client.Moved += OnMoved;
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(ticks / 20.0 + 10)); }
        finally { client.Moved -= OnMoved; }
    }

    private async Task AssertSameAsServerAsync(MinecraftClient client, string name)
    {
        await RunTicksAsync(client, 5); // standing still: let the last move packets arrive
        var server = await ServerPositionAsync(name);
        var (x, y, z) = client.Position;
        Assert.That(server.X, Is.EqualTo(x).Within(1e-6), "x");
        Assert.That(server.Y, Is.EqualTo(y).Within(1e-6), "y");
        Assert.That(server.Z, Is.EqualTo(z).Within(1e-6), "z");
    }

    [Test]
    public async Task WalkJumpStepAndSprint_TheServerAgrees()
    {
        const string name = "VertexWalk";
        var (client, ax, az) = await JoinOnArenaAsync(name, (x, z) =>
        [
            $"fill {x + 6} 200 {z - 2} {x + 9} 200 {z + 2} minecraft:stone_slab[type=bottom]", // half step
            $"fill {x + 10} 200 {z - 2} {x + 25} 200 {z + 2} minecraft:stone",                 // full step
            $"fill {x + 10} 201 {z - 2} {x + 25} 201 {z + 2} minecraft:stone_slab[type=bottom]",
        ]);
        await using var _ = client;

        // Walk onto the slab, then the block needs a jump.
        client.Controls.Forward = true;
        await RunTicksAsync(client, 40);
        Assert.That(client.Position.Y, Is.EqualTo(200.5), "stepped up the slab and stopped at the block");
        client.Controls.Jump = true;
        await RunTicksAsync(client, 25);
        client.Controls.Jump = false;
        await RunTicksAsync(client, 25);
        Assert.That(client.Position.Y, Is.EqualTo(201.5), "jumped onto the block, then stepped up its slab");

        // Sprint-jump off the far edge and land on the floor.
        client.Controls.Sprint = true;
        client.Controls.Jump = true;
        await RunTicksAsync(client, 40);
        client.Controls.Clear();
        await RunTicksAsync(client, 30);
        Assert.That(client.Position.Y, Is.EqualTo(200.0));
        Assert.That(client.Position.X, Is.GreaterThan(ax + 26), "went off the far end of the platform");

        await AssertSameAsServerAsync(client, name);
        AssertServerNeverObjected(client, name);
    }

    /// <summary>
    /// The red-check for every test here: a client that walks through a wall MUST be caught by the server, or
    /// "the server never objected" proves nothing. (It has to be horizontal: Paper 1.21.5's handleMovePlayer zeroes
    /// the vertical error with `if (dy > -0.5 || dy &lt; 0.5) dy = 0`, always true, so it cannot see a wrong Y. An
    /// illegal step-up was tried first and the server accepted it. Y is covered by comparing positions instead.)
    /// </summary>
    [Test]
    public async Task WalkingThroughAWall_IsCaughtByTheServer()
    {
        const string name = "VertexCheat";
        var (client, ax, az) = await JoinOnArenaAsync(name, (x, z) => [$"fill {x + 3} 200 {z - 2} {x + 3} 202 {z + 2} minecraft:stone"]);
        await using var _ = client;
        client.PhysicsTweak = p => p.NoClip = true;
        client.Controls.Forward = true;
        await RunTicksAsync(client, 30);
        client.Controls.Clear();
        client.PhysicsTweak = null;
        await RunTicksAsync(client, 10);
        Assert.That(client.TeleportCount - _teleportsAtStart[name], Is.GreaterThan(0), "the server accepted walking through stone");
        // ...and its correction teleport put us back on our side of the wall.
        Assert.That(client.Position.X, Is.LessThan(ax + 3));
    }

    [Test]
    public async Task Fall_TakesVanillaFallDamage()
    {
        const string name = "VertexFall";
        var (client, ax, az) = await JoinOnArenaAsync(name, (_, _) => []);
        await using var _ = client;
        await _server.CommandAsync($"gamemode survival {name}");
        await Wait.UntilAsync(() => client.Health == 20, Timeout, () => $"health {client.Health}");

        // From 10 blocks up: fall distance 10, damage 10 - 3 (safe fall distance) = 7.
        await _server.CommandAsync($"tp {name} {ax + 0.5} 210 {az + 0.5}");
        await Wait.UntilAsync(() => client.Position.Y > 205, Timeout, () => $"{client.Position}");
        await Wait.UntilAsync(() => client.OnGround && Math.Abs(client.Position.Y - 200) < 1e-9, Timeout, () => $"{client.Position}");
        await Wait.UntilAsync(() => client.Health < 20, Timeout, () => $"health {client.Health}");
        Assert.That(client.Health, Is.EqualTo(13f), "10 blocks = 7 damage");

        await AssertSameAsServerAsync(client, name);
        AssertServerNeverObjected(client, name, expectedTeleports: 1); // our own /tp up to 210
    }
}
