using Vertex.Host;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Vertex was cut off AubsCraft at 15:59:04 and never came back: nothing rejoined him, so any server restart or network drop left him out until someone pressed Join.
/// He now goes back by himself when the server goes away, unless the reason is the server's answer (a ban, a kick, no whitelist, a sign-in problem), and never after a Leave or Stop.
/// </summary>
public class RejoinPolicyTests
{
    [TestCase("connection error: An existing connection was forcibly closed by the remote host.", true)]
    [TestCase("connection closed", true)]
    [TestCase("Server closed", true)]
    [TestCase("Timed out", true)]
    [TestCase("Internal Exception: java.io.IOException: Connection reset by peer", true)]
    [TestCase(null, true)]
    [TestCase("", true)]
    [TestCase("You are banned from this server.", false)]
    [TestCase("You are not white-listed on this server!", false)]
    [TestCase("this server is in online mode: sign in with a Microsoft account to join it", false)]
    [TestCase("Kicked by an operator", false)]
    [TestCase("Outdated client! Please use 1.21.5", false)]
    [TestCase("This server requires Fabric to join", false)]
    [TestCase("disconnected by client", false)]
    public void WhichDisconnectsAreWorthGoingBackAfter(string? reason, bool expected)
        => Assert.That(VertexHost.ShouldRejoin(reason), Is.EqualTo(expected), reason);
}

/// <summary>The real thing: a real server goes away under a running Vertex, comes back, and he is in it again; a Leave in between keeps him out. Real model on the real GPU.</summary>
[Category("GPU")]
[NonParallelizable]
public class HostRejoinTests
{
    private const int Port = 25980;
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, string> Peaceful = new() { ["difficulty"] = "peaceful" };

    [Test]
    public async Task WhenTheServerGoesAway_VertexRejoinsByItself_WhenItComesBack_AndALeaveKeepsHimOut()
    {
        var server = await PaperServer.StartAsync("rejoin", Port, TimeSpan.FromMinutes(4), Peaceful);
        var folder = Path.Combine(Path.GetTempPath(), "vertex-rejoin-" + Guid.NewGuid().ToString("N"));
        await using var host = new VertexHost(folder) { RejoinDelay = _ => TimeSpan.FromSeconds(3), UnloadAfterStop = Timeout.InfiniteTimeSpan };
        try
        {
            host.Settings.Server = "127.0.0.1";
            host.Settings.Port = Port;
            host.Settings.Account = "";            // offline
            host.Settings.OfflineName = "VertexRejoin";
            host.Settings.Voice = false;
            host.Settings.Minecraft = true;
            host.Settings.Reachy = false;
            await host.StartAsync().WaitAsync(Slow);
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);
            Assert.That(host.InGame, Is.True, host.Status);

            // The server goes away under him.
            await server.DisposeAsync();
            await Wait.UntilAsync(() => !host.InGame, TimeSpan.FromSeconds(60), () => host.Status);
            await Wait.UntilAsync(() => host.Status.Contains("rejoining"), TimeSpan.FromSeconds(30), () => host.Status);
            Assert.That(host.State, Is.EqualTo(HostState.Playing), "Vertex stays on while the game is away");

            // It comes back: he is in it again, by himself.
            server = await PaperServer.StartAsync("rejoin", Port, TimeSpan.FromMinutes(4), Peaceful);
            // The log line, not InGame: InGame is true as soon as his brain exists, which is before the connection has finished.
            await Wait.UntilAsync(() => host.Lines.Any(l => l.Text.StartsWith("rejoined")), TimeSpan.FromMinutes(3), () => host.Status + " | " + string.Join(" | ", host.Lines.TakeLast(4).Select(l => l.Text)));
            Assert.That(host.InGame, Is.True, "he is in the game again");
            Assert.That(host.Client?.State, Is.EqualTo(SpawnDev.Minecraft.ConnectionState.Play), "really connected and playing, not just set up");
            Assert.That(host.Status, Does.Not.Contain("rejoining"), "no longer waiting to rejoin");

            // Gone again, and this time somebody presses Leave while he is waiting: he must stay out.
            await server.DisposeAsync();
            await Wait.UntilAsync(() => !host.InGame && host.Status.Contains("rejoining"), TimeSpan.FromSeconds(60), () => host.Status);
            await host.LeaveGameAsync();
            server = await PaperServer.StartAsync("rejoin", Port, TimeSpan.FromMinutes(4), Peaceful);
            await Task.Delay(TimeSpan.FromSeconds(30));
            Assert.That(host.InGame, Is.False, "a Leave means stay out, even when the server is back");
            Assert.That(host.Status, Does.Not.Contain("rejoining"));
            await host.StopAsync();
        }
        finally
        {
            try { await server.DisposeAsync(); } catch { }
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
