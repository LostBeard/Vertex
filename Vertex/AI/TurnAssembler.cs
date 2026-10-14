namespace Vertex.AI;

/// <summary>A turn the speaker has finished: all the audio of it, and why it was handed over.</summary>
/// <param name="Samples">16 kHz mono audio from the first segment's start to the last segment's end (gaps between segments are silence).</param>
/// <param name="Probability">The end-of-turn model's last answer, or null when it could not be asked.</param>
/// <param name="Reason">"complete" (the model said the turn is over), "hold expired" (it said not yet, nobody spoke again), "max length", or "model unavailable".</param>
/// <param name="EndedTick">When the last segment arrived (<see cref="Environment.TickCount64"/>).</param>
public sealed record CompletedTurn(float[] Samples, float? Probability, string Reason, long EndedTick);

/// <summary>
/// Turns the VAD's short pauses into whole turns. The VAD closes a segment after a brief silence (a quarter second); this asks an
/// end-of-turn model whether what has been said so far is finished. Finished: the turn goes out at once, which is faster than waiting half a
/// second for everyone. Not finished ("I'd like to... um"): the audio is held, up to <see cref="MaxHold"/>, for the speaker to carry on; if they do, the
/// next segment is joined to it and the model is asked again, and if they do not, the turn goes out anyway.
/// </summary>
/// <remarks>
/// The model never loses speech: if it cannot be asked (not loaded, GPU error) the turn goes out immediately, exactly like the plain
/// silence rule. Segments are placed by their absolute sample position, so the pause between two segments is real silence and segments whose
/// padding overlaps do not duplicate audio.
/// </remarks>
public sealed class TurnAssembler : IDisposable
{
    private readonly Func<float[], CancellationToken, Task<float>> _predict;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _lock = new();
    private float[] _buffer = new float[16000 * 10];
    private int _length;            // samples held
    private long _origin;           // absolute sample index of _buffer[0]
    private long _endedTick;
    private int _generation;
    private int _emitting;        // turns being handed over (the handler has not returned yet)
    private bool _speaking;       // the VAD has speech open right now
    private TaskCompletionSource _quiet = Done();
    private CancellationTokenSource? _holdCts;   // the running hold, cancelled when speech resumes

    private static TaskCompletionSource Done() { var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); t.SetResult(); return t; }
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>At or above this, the turn is over.</summary>
    public float Threshold { get; set; } = 0.5f;

    /// <summary>How long a turn the model calls unfinished is held for the speaker to continue (counted from when the VAD closed the segment).</summary>
    public TimeSpan MaxHold { get; set; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>A turn this long goes out regardless.</summary>
    public TimeSpan MaxTurn { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A finished turn. Raised on a thread-pool thread.</summary>
    public event Action<CompletedTurn>? Completed;

    public event Action<string>? Log;

    /// <param name="predict">P(turn complete) for the audio so far (16 kHz mono).</param>
    /// <param name="delay">Test seam; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public TurnAssembler(Func<float[], CancellationToken, Task<float>> predict, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _predict = predict;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>True while audio is held or being judged.</summary>
    public bool HasPending { get { lock (_lock) return _length > 0 || _emitting > 0; } }

    /// <summary>
    /// Whether the VAD has speech open right now. A hold counts SILENCE only: while the speaker is talking again the clock is stopped, because the
    /// segment that carries the continuation is not reported until they finish it, which can be many seconds after they started.
    /// </summary>
    public void SetSpeaking(bool speaking)
    {
        lock (_lock)
        {
            if (_speaking == speaking) return;
            _speaking = speaking;
            if (speaking) { _quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _holdCts?.Cancel(); }
            else _quiet.TrySetResult();
        }
    }

    /// <summary>A segment the VAD closed.</summary>
    /// <param name="samples">Its audio.</param>
    /// <param name="startSample">Its absolute start position in the stream (the VAD's own count), to place it relative to the others.</param>
    /// <param name="endedTick">When it arrived.</param>
    public void Add(float[] samples, long startSample, long endedTick)
    {
        float[] snapshot;
        int generation;
        CancellationToken token;
        lock (_lock)
        {
            if (_disposed) return;
            if (_length == 0) { _origin = startSample; }
            long offsetLong = startSample - _origin;
            // A segment that starts before what is held (it cannot, in order) is clamped rather than trusted with a negative index.
            int offset = (int)Math.Max(0, Math.Min(offsetLong, int.MaxValue / 2));
            int needed = offset + samples.Length;
            if (needed > _buffer.Length) Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
            if (offset > _length) Array.Clear(_buffer, _length, offset - _length);   // the pause between two segments is silence
            Array.Copy(samples, 0, _buffer, offset, samples.Length);
            if (needed > _length) _length = needed;
            _endedTick = endedTick;
            _generation++;
            generation = _generation;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            snapshot = new float[_length];
            Array.Copy(_buffer, snapshot, _length);
        }
        _ = Task.Run(() => JudgeAsync(snapshot, generation, token));
    }

    private async Task JudgeAsync(float[] audio, int generation, CancellationToken ct)
    {
        float? p = null;
        try
        {
            p = await _predict(audio, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            // Never lose speech to the helper: without an answer this is the plain silence rule.
            Log?.Invoke("end-of-turn model unavailable (" + e.Message + "); ending the turn on silence");
            Emit(generation, null, "model unavailable");
            return;
        }
        if (ct.IsCancellationRequested) return;      // a newer segment arrived while the model was thinking: its judgement replaces this one

        if (p >= Threshold) { Emit(generation, p, "complete"); return; }
        if (audio.Length >= MaxTurn.TotalSeconds * 16000) { Emit(generation, p, "max length"); return; }

        // Hold for the speaker to carry on, counting only silence: if speech opens again the clock stops, and if it closes without
        // producing a segment (a cough the VAD discarded) the full hold starts over.
        while (true)
        {
            Task quiet;
            lock (_lock) quiet = _speaking ? _quiet.Task : Task.CompletedTask;
            try { await quiet.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            using var hold = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_lock)
            {
                if (_speaking) continue;      // speech opened between the check and here
                _holdCts = hold;
            }
            try { await _delay(MaxHold, hold.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return;
                continue;                      // speech resumed: wait for quiet, then hold again
            }
            finally { lock (_lock) if (ReferenceEquals(_holdCts, hold)) _holdCts = null; }
            Emit(generation, p, "hold expired");
            return;
        }
    }

    private void Emit(int generation, float? probability, string reason)
    {
        CompletedTurn turn;
        lock (_lock)
        {
            if (generation != _generation || _length == 0) return;   // superseded
            var samples = new float[_length];
            Array.Copy(_buffer, samples, _length);
            turn = new CompletedTurn(samples, probability, reason, _endedTick);
            _length = 0;
            _emitting++;
        }
        try { Completed?.Invoke(turn); }
        finally { lock (_lock) _emitting--; }
    }

    /// <summary>Drops what is held (the ears closed while Vertex talks).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _length = 0;
            _generation++;
            _cts?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _length = 0;
            _generation++;
            _cts?.Cancel();
        }
    }
}
