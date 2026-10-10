namespace Vertex.Brain;

/// <summary>How a background task ended.</summary>
public sealed record TaskOutcome(string Task, bool Success, string Summary);

/// <summary>
/// Runs one long activity at a time (follow, collect, go to...). Starting a new one cancels the current one,
/// like a player switching what they are doing. The brain hears about every ending.
/// </summary>
public sealed class TaskRunner
{
    private CancellationTokenSource? _cts;
    private Func<CancellationToken, Task<(bool Success, string Summary)>>? _work;
    private readonly object _lock = new();

    /// <summary>What Vertex is doing now, in a few words (for the prompt), or null when idle.</summary>
    public string? Current { get; private set; }

    public event Action<TaskOutcome>? Finished;

    public bool IsBusy => Current != null;

    /// <summary>Starts a task (cancelling the current one). The task returns its summary for the brain.</summary>
    public void Start(string description, Func<CancellationToken, Task<(bool Success, string Summary)>> work) => Run(description, work, null);

    /// <summary>
    /// Does something urgent now (get away from a creeper), then goes back to what it was doing (following
    /// someone, guarding...). The urgent job's ending is reported like any other; the resumed one quietly restarts.
    /// </summary>
    public void Interrupt(string description, Func<CancellationToken, Task<(bool Success, string Summary)>> work)
    {
        (string, Func<CancellationToken, Task<(bool, string)>>)? resume;
        lock (_lock) resume = Current != null && _work != null ? (Current, _work) : null;
        Run(description, work, resume);
    }

    private void Run(string description, Func<CancellationToken, Task<(bool Success, string Summary)>> work,
        (string Description, Func<CancellationToken, Task<(bool, string)>> Work)? resume)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = cts = new CancellationTokenSource();
            _work = work;
            Current = description;
        }
        _ = Task.Run(async () =>
        {
            TaskOutcome outcome;
            try
            {
                var (ok, summary) = await work(cts.Token);
                outcome = new TaskOutcome(description, ok, summary);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return; // replaced or stopped: whoever cancelled already knows
            }
            catch (Exception e)
            {
                outcome = new TaskOutcome(description, false, "failed: " + e.Message);
            }
            lock (_lock)
            {
                if (_cts != cts) return;
                Current = null;
                _work = null;
            }
            Finished?.Invoke(outcome);
            if (resume is { } r)
                lock (_lock)
                    if (_cts == cts) Run(r.Description, r.Work, null); // nothing new was started meanwhile
        });
    }

    /// <summary>Stops whatever is running.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            _work = null;
            Current = null;
        }
    }
}
