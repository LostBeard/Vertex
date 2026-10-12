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
/// Hearing must not wait for a whole reply, so a long holder (the language model) calls <see cref="YieldAsync"/>
/// between tokens: when an urgent caller (speech to text, a reflex) is waiting, the holder steps aside for it and
/// then continues.
/// </remarks>
public sealed class GpuLane
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _urgentWaiting;

    /// <summary>Waits for the GPU. Dispose the result to hand it on.</summary>
    /// <param name="urgent">Speech to text and reflexes: a holder that yields lets these in first.</param>
    public async Task<IDisposable> EnterAsync(bool urgent = false, CancellationToken ct = default)
    {
        if (urgent) Interlocked.Increment(ref _urgentWaiting);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (urgent) Interlocked.Decrement(ref _urgentWaiting);
        }
        return new Holder(this);
    }

    /// <summary>
    /// Called by a holder between steps: when an urgent caller is waiting, releases the GPU, lets it run, and takes
    /// the GPU back. Does nothing otherwise.
    /// </summary>
    public async Task YieldAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _urgentWaiting) == 0) return;
        _gate.Release();
        // Give the waiter the first chance at the gate before taking it back.
        while (Volatile.Read(ref _urgentWaiting) > 0 && _gate.CurrentCount > 0)
            await Task.Yield();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="work"/> while holding the GPU.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> work, bool urgent = false, CancellationToken ct = default)
    {
        using (await EnterAsync(urgent, ct).ConfigureAwait(false))
            return await work().ConfigureAwait(false);
    }

    private sealed class Holder(GpuLane lane) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) lane._gate.Release();
        }
    }
}
