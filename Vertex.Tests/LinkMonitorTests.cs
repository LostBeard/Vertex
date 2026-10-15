using Vertex.Reachy;

namespace Vertex.Tests;

/// <summary>The warning that would have saved a day on 2026-10-15, when the robot's Wi-Fi had degraded to about 90 KB/s and nothing said so.</summary>
public class LinkMonitorTests
{
    [Test]
    public void ASlowClipIsReported_AHealthyOneIsNot()
    {
        var monitor = new LinkMonitor();
        var warning = monitor.Observe(bytes: 390 * 1024, transferMs: 5900, waitedAfterCommandMs: 5900, nowTick: 1_000_000);
        Assert.That(warning, Does.Contain("slow link to the robot").And.Contain("66 KB/s").And.Contain("restarting the robot"));

        var healthy = new LinkMonitor();
        Assert.That(healthy.Observe(390 * 1024, transferMs: 300, waitedAfterCommandMs: 300, nowTick: 1_000_000), Is.Null, "1.3 MB/s is a healthy link");
        Assert.That(healthy.LastKbPerSecond, Is.GreaterThan(1000));
        Assert.That(healthy.Observe(390 * 1024, transferMs: 60, waitedAfterCommandMs: 40, nowTick: 1_000_000), Is.Null, "a transfer that finished before the play command is not measured at all");
    }

    [Test]
    public void ItSaysNothingWhenTheTransferTimeIsNotKnown_OrTheClipIsTiny()
    {
        var monitor = new LinkMonitor();
        // The robot confirmed within a few ms of the command: the clip had already arrived and simply waited its turn, so the elapsed time says nothing about the link.
        Assert.That(monitor.Observe(390 * 1024, transferMs: 9000, waitedAfterCommandMs: 20, nowTick: 1_000_000), Is.Null);
        Assert.That(monitor.Observe(8 * 1024, transferMs: 3000, waitedAfterCommandMs: 3000, nowTick: 1_000_000), Is.Null, "a few KB tells nothing");
    }

    [Test]
    public void ItRepeatsItselfOnlyEveryTwoMinutes()
    {
        var monitor = new LinkMonitor();
        Assert.That(monitor.Observe(200 * 1024, 4000, 4000, 1_000_000), Is.Not.Null);
        Assert.That(monitor.Observe(200 * 1024, 4000, 4000, 1_000_000 + 60_000), Is.Null, "one minute later: quiet");
        Assert.That(monitor.Observe(200 * 1024, 4000, 4000, 1_000_000 + 121_000), Is.Not.Null, "after two minutes: again");
    }
}
