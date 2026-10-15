using System.Runtime.CompilerServices;

namespace Vertex.AI;

/// <summary>
/// One GPU, one model at a time. Every model call (language model, speech to text, voice, sentence encoder, reflex
/// network) enters here first.
/// </summary>
/// <remarks>
/// Running two models at once on one CUDA device is not merely slow. A model that captures a CUDA graph (Kokoro does,
/// to replay its decoder) records in Global capture mode, and while it records, ANY allocation or free from another
/// thread invalidates the capture and poisons the context: an uncatchable access violation (SpawnDev.ILGPU.ML
/// CudaGraphCapture notes). Before this lane, Whisper transcribed on the ears' thread while the language model
/// decoded on the brain's (seen together in one stack, 2026-10-12).
///
/// Hearing must not wait for a whole reply, so a long holder (the language model) calls <see cref="Hold.YieldAsync"/>
/// between tokens: when an urgent caller (speech to text, a reflex) is waiting, the holder steps aside for it and
/// then continues.
///
/// ⚠️ The yield belongs to the HOLD, not to the lane (2026-10-15, live on the robot). It used to be a lane-wide call that released the
/// gate and waited to take it back with the turn's token: a turn cancelled while it waited (someone spoke over the reply, or the 60 s
/// limit) threw out of the wait WITHOUT the gate, and its <c>using</c> then released a gate it did not hold - the semaphore overflowed
/// ("Adding the specified count to the semaphore would cause it to exceed its maximum count"), and for that moment two models owned the
/// GPU. A hold now knows whether it holds the gate, takes it back whatever happens, and releases it exactly once.
/// </remarks>
public sealed class GpuLane
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _urgentWaiting;
    private volatile Hold? _holder;
    private readonly object _usageLock = new();
    private readonly Dictionary<string, (long Ms, int Count)> _usage = new();

    /// <summary>
    /// What each caller has HELD the GPU for since <paramref name="since"/> was last reset (the time a holder stepped aside for someone else is not counted): the
    /// answer to "where did the time of this reply go". Names are the calling methods. Calls (not milliseconds) are counted once, when they start.
    /// </summary>
    public string DescribeUsage(ref long since)
    {
        Dictionary<string, (long Ms, int Count)> copy;
        lock (_usageLock) { copy = new(_usage); _usage.Clear(); }
        var now = Environment.TickCount64;
        var window = Math.Max(1, now - since);
        since = now;
        var parts = copy.OrderByDescending(kv => kv.Value.Ms).Select(kv => $"{kv.Key} {kv.Value.Ms / 1000.0:0.0} s ({kv.Value.Count}x)");
        var busy = copy.Values.Sum(v => v.Ms);
        return $"GPU lane over {window / 1000.0:0.0} s: busy {Math.Min(100, busy * 100 / window)}%: " + (copy.Count == 0 ? "idle" : string.Join(", ", parts));
    }

    private void Account(string who, long ms, bool newCall)
    {
        lock (_usageLock)
        {
            _usage.TryGetValue(who, out var u);
            _usage[who] = (u.Ms + ms, u.Count + (newCall ? 1 : 0));
        }
    }

    /// <summary>Who holds the lane right now (null when nobody does): for a caller to say who it waited for.</summary>
    public string? CurrentHolder => _holder is { } h && h.IsHeld ? h.Who : null;

    /// <summary>Told when a caller has waited a long time for the lane, and by whom it is held (a model call that never ends shows here instead of as silence).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>How long a caller waits before the lane says who is holding it, and every so often after that.</summary>
    public TimeSpan StallReportAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Waits for the GPU. Dispose the result to hand it on.</summary>
    /// <param name="urgent">Speech to text and reflexes: a holder that yields lets these in first.</param>
    /// <param name="who">Who is asking, for the stall report (the calling method's name by default).</param>
    public async Task<Hold> EnterAsync(bool urgent = false, CancellationToken ct = default, [CallerMemberName] string who = "")
    {
        if (urgent) Interlocked.Increment(ref _urgentWaiting);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!await _gate.WaitAsync(StallReportAfter, ct).ConfigureAwait(false))
            {
                var holder = _holder;
                Log?.Invoke($"GPU lane: {who}{(urgent ? " (urgent)" : "")} has waited {clock.Elapsed.TotalSeconds:0} s; held by {holder?.Who ?? "?"} for {holder?.Age.TotalSeconds ?? 0:0} s");
            }
        }
        finally
        {
            if (urgent) Interlocked.Decrement(ref _urgentWaiting);
        }
        var hold = new Hold(this, who);
        _holder = hold;
        Account(who, 0, newCall: true);
        return hold;
    }

    /// <summary>Runs <paramref name="work"/> while holding the GPU.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> work, bool urgent = false, CancellationToken ct = default, [CallerMemberName] string who = "")
    {
        using (await EnterAsync(urgent, ct, who).ConfigureAwait(false))
            return await work().ConfigureAwait(false);
    }

    /// <summary>The GPU, held. Dispose it to hand the GPU on; a long holder calls <see cref="YieldAsync"/> between its steps.</summary>
    public sealed class Hold : IDisposable
    {
        private const int Held = 1, Yielded = 0, Done = 2;
        private readonly GpuLane _lane;
        private int _state = Held;
        private readonly long _since = Environment.TickCount64;
        private long _heldFrom = Environment.TickCount64;       // when this hold last took the GPU (at the start, and after each yield)

        internal Hold(GpuLane lane, string who) { _lane = lane; Who = who; }

        /// <summary>Who took the GPU.</summary>
        public string Who { get; }

        internal bool IsHeld => Volatile.Read(ref _state) != Done;

        /// <summary>How long it has been held.</summary>
        public TimeSpan Age => TimeSpan.FromMilliseconds(Environment.TickCount64 - _since);

        /// <summary>
        /// Called by the holder between steps: when an urgent caller is waiting, hands the GPU over, lets it run, and takes the GPU back. Does nothing otherwise. Taking it back
        /// is never abandoned: if <paramref name="ct"/> is cancelled meanwhile, the hold waits for its turn anyway and THEN throws, so the holder always owns the GPU again
        /// when control returns to it (or to its <c>using</c>).
        /// </summary>
        public async Task YieldAsync(CancellationToken ct = default)
        {
            if (Volatile.Read(ref _lane._urgentWaiting) == 0) return;
            if (Interlocked.CompareExchange(ref _state, Yielded, Held) != Held) return;
            _lane.Account(Who, Environment.TickCount64 - _heldFrom, newCall: false);
            _lane._gate.Release();
            // Give the waiter the first chance at the gate before taking it back.
            while (Volatile.Read(ref _lane._urgentWaiting) > 0 && _lane._gate.CurrentCount > 0)
                await Task.Yield();
            await _lane._gate.WaitAsync().ConfigureAwait(false);
            _lane._holder = this;
            _heldFrom = Environment.TickCount64;
            // Disposed while it waited (it cannot be, from its own thread, but a cancellation path may): hand the gate straight on.
            if (Interlocked.CompareExchange(ref _state, Held, Yielded) != Yielded) _lane._gate.Release();
            ct.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            // Held: release it. Yielded: the GPU is not ours, nothing to release (a yield that finishes later sees Done and hands it back). Done: already released.
            if (Interlocked.Exchange(ref _state, Done) == Held)
            {
                _lane.Account(Who, Environment.TickCount64 - _heldFrom, newCall: false);
                _lane._gate.Release();
            }
        }
    }
}
