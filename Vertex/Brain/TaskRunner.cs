namespace Vertex.Brain;

/// <summary>
/// How a background task ended. For: the player it was done for (who asked), if anyone. Goal: the long-running goal
/// it was a step of, if any. Interruption: an urgent job that cut in (a reflex), after which the interrupted one resumes.
/// </summary>
public sealed record TaskOutcome(string Task, bool Success, string Summary, string? For = null, bool Interruption = false, Objective? Goal = null);

/// <summary>
/// Runs one long activity at a time (follow, collect, go to...). Starting a new one cancels the current one,
/// like a player switching what they are doing. The brain hears about every ending.
/// </summary>
public sealed class TaskRunner
{
    private CancellationTokenSource? _cts;
    private Func<CancellationToken, Task<(bool Success, string Summary)>>? _work;
    private string? _for;
    private Objective? _goal;
    private readonly object _lock = new();

    /// <summary>What Vertex is doing now, in a few words (for the prompt), or null when idle.</summary>
    public string? Current { get; private set; }

    public event Action<TaskOutcome>? Finished;

    public bool IsBusy => Current != null;

    /// <summary>Who a task started now is for (the brain: whoever is talking, or the goal being worked on).</summary>
    public Func<string?>? Requester { get; set; }

    /// <summary>
    /// The goal a task started now is a step of (the brain: the one it is working on). Captured when the task
    /// starts and carried to its outcome: a step can end before the brain has finished deciding on it.
    /// </summary>
    public Func<Objective?>? GoalOf { get; set; }

    /// <summary>Starts a task (cancelling the current one). The task returns its summary for the brain.</summary>
    public void Start(string description, Func<CancellationToken, Task<(bool Success, string Summary)>> work) =>
        Run(description, work, null, Requester?.Invoke(), GoalOf?.Invoke());

    /// <summary>
    /// Does something urgent now (get away from a creeper), then goes back to what it was doing (following
    /// someone, guarding...). The urgent job's ending is reported like any other; the resumed one quietly restarts.
    /// </summary>
    public void Interrupt(string description, Func<CancellationToken, Task<(bool Success, string Summary)>> work)
    {
        (string, Func<CancellationToken, Task<(bool, string)>>, string?, Objective?)? resume;
        lock (_lock) resume = Current != null && _work != null ? (Current, _work, _for, _goal) : null;
        Run(description, work, resume, null, null, interruption: true); // a reflex, done for nobody in particular
    }

    private void Run(string description, Func<CancellationToken, Task<(bool Success, string Summary)>> work,
        (string Description, Func<CancellationToken, Task<(bool, string)>> Work, string? For, Objective? Goal)? resume,
        string? forPlayer, Objective? goal, bool interruption = false)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = cts = new CancellationTokenSource();
            _work = work;
            _for = forPlayer;
            _goal = goal;
            Current = description;
        }
        _ = Task.Run(async () =>
        {
            TaskOutcome outcome;
            try
            {
                var (ok, summary) = await work(cts.Token);
                outcome = new TaskOutcome(description, ok, summary, forPlayer, interruption, goal);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return; // replaced or stopped: whoever cancelled already knows
            }
            catch (Exception e)
            {
                outcome = new TaskOutcome(description, false, "failed: " + e.Message, forPlayer, interruption, goal);
            }
            lock (_lock)
            {
                if (_cts != cts) return;
                Current = null;
                _work = null;
                _for = null;
                _goal = null;
            }
            Finished?.Invoke(outcome);
            if (resume is { } r)
                lock (_lock)
                    if (_cts == cts) Run(r.Description, r.Work, null, r.For, r.Goal); // nothing new was started meanwhile
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
            _for = null;
            _goal = null;
            Current = null;
        }
    }
}
