namespace Vertex.Reachy;

/// <summary>
/// Notices when clips reach the robot far slower than its Wi-Fi should carry them, and says so (rarely, and in plain words).
/// </summary>
/// <remarks>
/// Live 2026-10-15: after 47 days of uptime the robot's link degraded until the router sent to it at 1 Mbit/s (about 90 KB/s); every sentence waited seconds for its clip and the cause took a day to
/// find. A restart of the robot brought it back to 6.6 MB/s. Nothing in the app said the link was the problem; now it does.
/// </remarks>
public sealed class LinkMonitor
{
    /// <summary>Below this a clip is reported as slow. A healthy link carries several MB/s; the degraded one carried 0.09.</summary>
    public const double SlowKbPerSecond = 300;

    /// <summary>Clips smaller than this say nothing about throughput (handshake and rounding dominate).</summary>
    public const int MinBytes = 40 * 1024;

    /// <summary>How long the robot must still have been receiving when the play command went out for the transfer time to be known (otherwise the clip was simply waiting its turn).</summary>
    public const int MinWaitedMs = 300;

    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);
    private long _lastWarningTick = long.MinValue / 2;

    /// <summary>Kilobytes per second of the last measured clip (0 until one was measured), for the dashboard and tests.</summary>
    public double LastKbPerSecond { get; private set; }

    /// <param name="bytes">Size of the clip as sent.</param>
    /// <param name="transferMs">From the first byte handed to the channel until the robot said it had the clip and started it.</param>
    /// <param name="waitedAfterCommandMs">How long after the play command the robot took to confirm: if it is small the clip had been waiting, and the transfer time is not known.</param>
    /// <param name="nowTick">Environment.TickCount64 (a parameter so tests control time).</param>
    /// <returns>A warning to show, or null.</returns>
    public string? Observe(long bytes, long transferMs, long waitedAfterCommandMs, long nowTick)
    {
        if (bytes < MinBytes || waitedAfterCommandMs < MinWaitedMs || transferMs <= 0) return null;
        var kbPerSecond = bytes / 1024.0 / (transferMs / 1000.0);
        LastKbPerSecond = kbPerSecond;
        if (kbPerSecond >= SlowKbPerSecond) return null;
        if (nowTick - _lastWarningTick < Quiet.TotalMilliseconds) return null;
        _lastWarningTick = nowTick;
        return $"slow link to the robot: a {bytes / 1024} KB clip took {transferMs / 1000.0:0.0} s to arrive ({kbPerSecond:0} KB/s; a healthy link carries several MB/s). " +
               "Its Wi-Fi may have degraded: restarting the robot (the button on its back) fixed this once, and tools/rose_link_test.cs in SpawnDev.Reachy measures the link.";
    }
}
