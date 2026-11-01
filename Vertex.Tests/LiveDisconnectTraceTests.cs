using SpawnDev.Minecraft;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Vertex was cut off AubsCraft with only "An existing connection was forcibly closed by the remote host" and nothing about the minute before (2026-11-01 15:59:04). Now every connection
/// that ends says how it looked: the last packet each way and its age, and how fast keep-alives were answered. A real Paper server, a real keep-alive, a real shutdown.
/// </summary>
[NonParallelizable]
public class LiveDisconnectTraceTests
{
    private const int Port = 25978;

    [Test]
    public async Task AConnectionThatEnds_SaysHowItLookedJustBefore_WithRealKeepAliveTiming()
    {
        var server = await PaperServer.StartAsync("trace", Port, TimeSpan.FromMinutes(4));
        var log = new List<string>();
        await using var client = new MinecraftClient(GameProfile.Offline("VertexTrace"), new ClientOptions { Log = m => { lock (log) log.Add(m); } });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);

        // Paper sends the first keep-alive after about 15 s of play: wait for it to have been answered.
        await Task.Delay(TimeSpan.FromSeconds(20));
        await server.DisposeAsync();     // the server goes away under the client
        await Wait.UntilAsync(() => { lock (log) return log.Any(l => l.StartsWith("disconnected detail:")); }, TimeSpan.FromSeconds(60), () => string.Join(" | ", log.TakeLast(5)));

        string detail; lock (log) detail = log.First(l => l.StartsWith("disconnected detail:"));
        TestContext.Out.WriteLine(detail);
        Assert.That(detail, Does.Contain("from the server:").And.Contain("to the server:"), "both directions are described");
        Assert.That(detail, Does.Match(@"from the server: [1-9][\d,]* packets"), "it saw real traffic");
        Assert.That(detail, Does.Match(@"to the server: [1-9][\d,]* packets"));
        Assert.That(detail, Does.Match(@"\d+ keep-alives answered, slowest reply \d+ ms"), "the keep-alive timing is there (the server sent at least one in 20 s of play)");
        Assert.That(detail, Does.Contain("recent ids ["));
        lock (log) Assert.That(log.Any(l => l.StartsWith("disconnected: ")), "the reason line is still there too");
    }
}
