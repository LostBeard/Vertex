using System.Diagnostics;

namespace SpawnDev.Minecraft.Net;

/// <summary>
/// What a connection has done lately, kept so that the way it ENDED can be explained. Vertex was cut off an AubsCraft server with only "An existing connection was forcibly closed by the
/// remote host" (2026-11-01, 15:59:04, about 7 minutes in, no restart and no kick on the server) and nothing recorded about the minute before. This keeps the last packet in and out, their
/// ids and ages, and how quickly keep-alives were answered: a proxy drops a client that stays silent for its read timeout without saying why, so "the last thing we sent was 41 s ago"
/// and "a keep-alive reply took 30 s" are exactly the facts that tell a slow client from a server that closed on its own. Cheap enough to be always on (a few interlocked writes per packet).
/// </summary>
public sealed class ConnectionTrace
{
    private const int Ring = 8;
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _lastIn, _lastOut, _lastKeepAliveIn;
    private int _lastInId = -1, _lastOutId = -1;
    private long _packetsIn, _packetsOut, _bytesIn, _keepAlives, _slowestReplyTicks, _lastReplyTicks;
    private readonly int[] _recentOut = new int[Ring];
    private long _outIndex;

    /// <summary>A packet arrived from the server (state-relative id, body length).</summary>
    public void Received(int id, int length)
    {
        Volatile.Write(ref _lastIn, Stopwatch.GetTimestamp());
        Volatile.Write(ref _lastInId, id);
        Interlocked.Increment(ref _packetsIn);
        Interlocked.Add(ref _bytesIn, length);
    }

    /// <summary>A packet was handed to the socket.</summary>
    public void Sent(int id)
    {
        Volatile.Write(ref _lastOut, Stopwatch.GetTimestamp());
        Volatile.Write(ref _lastOutId, id);
        Interlocked.Increment(ref _packetsOut);
        _recentOut[(int)(Interlocked.Increment(ref _outIndex) % Ring)] = id;
    }

    /// <summary>A keep-alive arrived and was answered <paramref name="arrivedToAnswered"/> later.</summary>
    public void KeepAlive(TimeSpan arrivedToAnswered)
    {
        Volatile.Write(ref _lastKeepAliveIn, Stopwatch.GetTimestamp() - (long)(arrivedToAnswered.TotalSeconds * Stopwatch.Frequency));
        Interlocked.Increment(ref _keepAlives);
        var ticks = (long)(arrivedToAnswered.TotalSeconds * Stopwatch.Frequency);
        Volatile.Write(ref _lastReplyTicks, ticks);
        long seen;
        while (ticks > (seen = Volatile.Read(ref _slowestReplyTicks)) && Interlocked.CompareExchange(ref _slowestReplyTicks, ticks, seen) != seen) { }
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
    private double Ago(long at) => at == 0 ? double.NaN : Seconds(Stopwatch.GetTimestamp() - at);
    private static string Age(double s) => double.IsNaN(s) ? "never" : $"{s:0.0} s ago";

    /// <summary>One line: how long it lived, the last packet each way and when, the last packets sent, and the keep-alive timing.</summary>
    public string Describe()
    {
        var lived = Seconds(Stopwatch.GetTimestamp() - _start);
        var recent = new List<string>();
        var n = Volatile.Read(ref _outIndex);
        for (var i = Math.Min(Ring, n); i >= 1; i--) recent.Add(_recentOut[(int)((n - i + 1) % Ring)].ToString("X2"));
        var ka = Volatile.Read(ref _keepAlives);
        return $"lived {lived:0} s; from the server: {_packetsIn:N0} packets, {_bytesIn / 1024:N0} KB, last id 0x{Math.Max(_lastInId, 0):X2} {Age(Ago(Volatile.Read(ref _lastIn)))}; " +
               $"to the server: {_packetsOut:N0} packets, last id 0x{Math.Max(_lastOutId, 0):X2} {Age(Ago(Volatile.Read(ref _lastOut)))}, recent ids [{string.Join(' ', recent)}]; " +
               (ka == 0 ? "no keep-alive was answered"
                        : $"{ka} keep-alives answered, slowest reply {Seconds(Volatile.Read(ref _slowestReplyTicks)) * 1000:0} ms, last reply {Seconds(Volatile.Read(ref _lastReplyTicks)) * 1000:0} ms, last one arrived {Age(Ago(Volatile.Read(ref _lastKeepAliveIn)))}");
    }
}
