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
    private Task? _task;
    private readonly object _lock = new();

    /// <summary>What Vertex is doing now, in a few words (for the prompt), or null when idle.</summary>
    public string? Current { get; private set; }

    public event Action<TaskOutcome>? Finished;

    public bool IsBusy => Current != null;

    /// <summary>Starts a task (cancelling the current one). The task returns its summary for the brain.</summary>
    public void Start(string description, Func<CancellationToken, Task<(bool Success, string Summary)>> work)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = cts = new CancellationTokenSource();
            Current = description;
        }
        _task = Task.Run(async () =>
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
            }
            Finished?.Invoke(outcome);
        });
    }

    /// <summary>Stops whatever is running.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            Current = null;
        }
    }
}
