using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// The GPU lane under the situations the robot produced: a reply interrupted while it had stepped aside for speech, and a lot of callers at once.
/// Live 2026-10-15 the first of these overflowed the lane's semaphore ("Adding the specified count to the semaphore would cause it to exceed its maximum count"),
/// broke a turn, and for a moment let two models own the GPU.
/// </summary>
public class GpuLaneTests
{
    /// <summary>
    /// Red-checked against the old lane (a lane-wide YieldAsync whose wait was cancelled by the turn's token): it threw SemaphoreFullException from the urgent holder's Dispose.
    /// </summary>
    [Test]
    public async Task AReplyInterruptedWhileSteppedAside_DoesNotReleaseAGateItDoesNotHold()
    {
        var lane = new GpuLane();
        using var cts = new CancellationTokenSource();
        var llmHolding = new TaskCompletionSource();
        var urgentHolding = new TaskCompletionSource();
        var urgentMayFinish = new TaskCompletionSource();
        var llmLeft = new TaskCompletionSource<bool>();
        var llm = Task.Run(async () =>
        {
            try
            {
                using (var hold = await lane.EnterAsync())
                {
                    llmHolding.SetResult();
                    await Task.Delay(50);
                    await hold.YieldAsync(cts.Token);        // an urgent caller is waiting: step aside
                }
                llmLeft.SetResult(false);
            }
            catch (OperationCanceledException) { llmLeft.SetResult(true); }
        });
        await llmHolding.Task;
        var urgent = Task.Run(async () =>
        {
            using (await lane.EnterAsync(urgent: true))
            {
                urgentHolding.SetResult();
                await urgentMayFinish.Task;
            }
        });
        await urgentHolding.Task;
        cts.Cancel();                                        // the turn is interrupted while the reply waits to get the GPU back
        await Task.Delay(50);
        Assert.That(llmLeft.Task.IsCompleted, Is.False, "the reply must not leave while the urgent caller still has the GPU: it takes it back first");
        urgentMayFinish.SetResult();
        await urgent;                                        // must not throw
        Assert.That(await llmLeft.Task, Is.True, "the reply was told it was cancelled, after it had the GPU again");
        await llm;

        // The lane is whole: the next three callers still take turns, never together.
        var inside = 0; var most = 0;
        async Task Work() { using (await lane.EnterAsync()) { var n = Interlocked.Increment(ref inside); lock (lane) most = Math.Max(most, n); await Task.Delay(10); Interlocked.Decrement(ref inside); } }
        await Task.WhenAll(Work(), Work(), Work());
        Assert.That(most, Is.EqualTo(1));
    }

    [Test]
    public async Task AStormOfCallersAndYieldingHolders_NeverPutsTwoModelsOnTheGpu_AndNeverOverflows()
    {
        var lane = new GpuLane();
        var inside = 0; var most = 0; var errors = new List<Exception>();
        void Enter() { var n = Interlocked.Increment(ref inside); lock (errors) most = Math.Max(most, n); }
        async Task LongHolder(int seed)
        {
            var rnd = new Random(seed);
            using var cts = new CancellationTokenSource();
            if (rnd.Next(3) == 0) cts.CancelAfter(rnd.Next(5, 40));       // interrupted partway, as speech does
            try
            {
                using var hold = await lane.EnterAsync();
                Enter();
                try
                {
                    for (var i = 0; i < 10; i++)
                    {
                        await Task.Delay(rnd.Next(1, 4));
                        Interlocked.Decrement(ref inside);
                        try { await hold.YieldAsync(cts.Token); }
                        finally { Enter(); }
                    }
                }
                finally { Interlocked.Decrement(ref inside); }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { lock (errors) errors.Add(e); }
        }
        async Task Urgent(int seed)
        {
            try
            {
                using (await lane.EnterAsync(urgent: true)) { Enter(); await Task.Delay(new Random(seed).Next(1, 5)); Interlocked.Decrement(ref inside); }
            }
            catch (Exception e) { lock (errors) errors.Add(e); }
        }
        async Task Plain(int seed)
        {
            try
            {
                using (await lane.EnterAsync()) { Enter(); await Task.Delay(new Random(seed).Next(1, 5)); Interlocked.Decrement(ref inside); }
            }
            catch (Exception e) { lock (errors) errors.Add(e); }
        }
        var tasks = new List<Task>();
        for (var i = 0; i < 60; i++)
        {
            var seed = i;
            tasks.Add(Task.Run(() => LongHolder(seed)));
            tasks.Add(Task.Run(() => Urgent(seed)));
            tasks.Add(Task.Run(() => Plain(seed)));
            await Task.Delay(2);
        }
        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join(" | ", errors.Select(e => e.GetType().Name + ": " + e.Message)));
        Assert.That(most, Is.EqualTo(1), "two holders on the GPU at once");
    }

    [Test]
    public async Task ACallerWaitingLongForTheLane_IsToldWhoHasIt()
    {
        var lane = new GpuLane { StallReportAfter = TimeSpan.FromMilliseconds(60) };
        var reports = new List<string>();
        lane.Log = m => { lock (reports) reports.Add(m); };
        Task waiter;
        using (await lane.EnterAsync(who: "the language model"))
        {
            waiter = Task.Run(async () => { using (await lane.EnterAsync(urgent: true, who: "speech to text")) { } });
            await Task.Delay(250);
            lock (reports) Assert.That(reports.Any(r => r.Contains("speech to text") && r.Contains("held by the language model")), Is.True, string.Join(" | ", reports));
        }
        await waiter;     // it gets the lane once the holder lets go
    }
}
