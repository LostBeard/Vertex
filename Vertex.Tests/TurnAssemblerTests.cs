using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// When is a turn over: the assembler's decisions, with a scripted end-of-turn model and a controllable clock. No GPU. Each test pins one
/// guard, so a red-check can disable that guard and watch that test, and only that test, fail.
/// </summary>
public class TurnAssemblerTests
{
    private sealed class Rig : IDisposable
    {
        public readonly List<CompletedTurn> Turns = new();
        public readonly List<float[]> Asked = new();
        public Func<float[], Task<float>> Model = _ => Task.FromResult(0.9f);
        /// <summary>A real model call runs to its end however late it is cancelled; the rig honours cancellation unless a test says otherwise.</summary>
        public bool ModelIgnoresCancellation;
        /// <summary>Every pending hold: complete one to let the clock run out.</summary>
        public readonly List<TaskCompletionSource> Holds = new();
        public readonly TurnAssembler Assembler;

        public Rig()
        {
            Assembler = new TurnAssembler(
                async (pcm, ct) => { lock (Asked) Asked.Add(pcm); var p = await Model(pcm); if (!ModelIgnoresCancellation) ct.ThrowIfCancellationRequested(); return p; },
                (span, ct) =>
                {
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ct.Register(() => tcs.TrySetCanceled(ct));
                    lock (Holds) Holds.Add(tcs);
                    return tcs.Task;
                });
            Assembler.Completed += t => { lock (Turns) Turns.Add(t); };
        }

        public static float[] Speech(int samples, float value = 0.1f) => Enumerable.Repeat(value, samples).ToArray();

        public async Task WaitFor(Func<bool> condition, string what, int ms = 5000)
        {
            var until = Environment.TickCount64 + ms;
            while (!condition())
            {
                if (Environment.TickCount64 > until) Assert.Fail("timed out waiting for " + what);
                await Task.Delay(5);
            }
        }

        public int HoldCount { get { lock (Holds) return Holds.Count; } }
        public int TurnCount { get { lock (Turns) return Turns.Count; } }
        public void Dispose() => Assembler.Dispose();
    }

    [Test]
    public async Task ASentenceTheModelCallsFinished_GoesOutAtOnce_WithoutWaiting()
    {
        using var rig = new Rig();
        rig.Assembler.Add(Rig.Speech(16000), 0, 100);
        await rig.WaitFor(() => rig.TurnCount == 1, "the turn");
        Assert.That(rig.Turns[0].Reason, Is.EqualTo("complete"));
        Assert.That(rig.Turns[0].Probability, Is.EqualTo(0.9f));
        Assert.That(rig.Turns[0].Samples, Has.Length.EqualTo(16000));
        Assert.That(rig.HoldCount, Is.Zero, "a finished turn must not wait for anything");
    }

    [Test]
    public async Task ATrailingOffSentence_IsHeld_ThenGoesOutWhenNobodyContinues()
    {
        using var rig = new Rig { Model = _ => Task.FromResult(0.1f) };
        rig.Assembler.Add(Rig.Speech(16000), 0, 100);
        await rig.WaitFor(() => rig.HoldCount == 1, "the hold to start");
        Assert.That(rig.TurnCount, Is.Zero, "an unfinished turn is held, not sent");

        rig.Holds[0].SetResult();
        await rig.WaitFor(() => rig.TurnCount == 1, "the hold to expire");
        Assert.That(rig.Turns[0].Reason, Is.EqualTo("hold expired"));
        Assert.That(rig.Assembler.HasPending, Is.False);
    }

    [Test]
    public async Task ContinuingDuringTheHold_JoinsTheSegments_AndAsksAgain()
    {
        var answers = new Queue<float>(new[] { 0.1f, 0.8f });
        using var rig = new Rig { Model = _ => Task.FromResult(answers.Dequeue()) };
        rig.Assembler.Add(Rig.Speech(16000, 0.1f), 0, 100);
        await rig.WaitFor(() => rig.HoldCount == 1, "the first hold");

        // The speaker carries on 0.5 s after the first segment ended: 8000 samples of silence between them.
        rig.Assembler.Add(Rig.Speech(16000, 0.3f), 16000 + 8000, 200);
        await rig.WaitFor(() => rig.TurnCount == 1, "the joined turn");

        Assert.That(rig.Turns, Has.Count.EqualTo(1), "one turn, not two");
        var turn = rig.Turns[0];
        Assert.That(turn.Reason, Is.EqualTo("complete"));
        Assert.That(turn.Samples, Has.Length.EqualTo(16000 + 8000 + 16000));
        Assert.That(turn.Samples[0], Is.EqualTo(0.1f));
        Assert.That(turn.Samples[16000 + 100], Is.Zero, "the pause between the two is real silence");
        Assert.That(turn.Samples[^1], Is.EqualTo(0.3f));
        Assert.That(turn.EndedTick, Is.EqualTo(200), "ended when the LAST segment arrived");
        Assert.That(rig.Asked[1], Has.Length.EqualTo(40000), "the second question covers everything said so far");
        Assert.That(rig.Holds[0].Task.IsCanceled, "the first hold is cancelled, it must not send the half turn");
    }

    [Test]
    public async Task AModelThatCannotBeAsked_NeverLosesTheSpeech()
    {
        using var rig = new Rig { Model = _ => throw new InvalidOperationException("no GPU") };
        rig.Assembler.Add(Rig.Speech(8000), 0, 100);
        await rig.WaitFor(() => rig.TurnCount == 1, "the turn");
        Assert.That(rig.Turns[0].Reason, Is.EqualTo("model unavailable"));
        Assert.That(rig.Turns[0].Probability, Is.Null);
        Assert.That(rig.HoldCount, Is.Zero, "no answer means the plain silence rule: out now");
    }

    [Test]
    public async Task AJudgementSlowerThanTheNextSegment_IsDiscarded()
    {
        // The first answer ("complete") arrives only after the speaker has started again: it must not send half a turn.
        var firstAnswer = new TaskCompletionSource<float>();
        var calls = 0;
        using var rig = new Rig { ModelIgnoresCancellation = true };
        rig.Model = _ => ++calls == 1 ? firstAnswer.Task : Task.FromResult(0.1f);
        rig.Assembler.Add(Rig.Speech(16000), 0, 100);
        await rig.WaitFor(() => calls == 1, "the first question");
        rig.Assembler.Add(Rig.Speech(16000), 20000, 200);
        await rig.WaitFor(() => calls == 2, "the second question");
        firstAnswer.SetResult(0.95f);
        await Task.Delay(100);
        Assert.That(rig.TurnCount, Is.Zero, "the stale 'complete' was acted on");
        await rig.WaitFor(() => rig.HoldCount >= 1, "the hold for the joined turn");
    }

    [Test]
    public async Task OverlappingSegmentPadding_IsNotDuplicated()
    {
        // The VAD pads each segment by 150 ms either side; with a short silence the pads of two neighbours overlap.
        using var rig = new Rig { Model = _ => Task.FromResult(0.1f) };
        rig.Assembler.Add(Rig.Speech(16000, 0.1f), 0, 100);
        await rig.WaitFor(() => rig.HoldCount == 1, "the first hold");
        rig.Assembler.Add(Rig.Speech(16000, 0.2f), 16000 - 1000, 200);   // starts 1000 samples BEFORE the first ended
        await rig.WaitFor(() => rig.HoldCount == 2, "the second hold");
        rig.Holds[1].SetResult();
        await rig.WaitFor(() => rig.TurnCount == 1, "the turn");
        Assert.That(rig.Turns[0].Samples, Has.Length.EqualTo(16000 - 1000 + 16000), "audio counted twice");
    }

    [Test]
    public async Task AVeryLongTurn_GoesOutEvenIfTheModelKeepsSayingNotYet()
    {
        using var rig = new Rig { Model = _ => Task.FromResult(0.1f) };
        rig.Assembler.MaxTurn = TimeSpan.FromSeconds(5);
        rig.Assembler.Add(Rig.Speech(16000 * 6), 0, 100);
        await rig.WaitFor(() => rig.TurnCount == 1, "the turn");
        Assert.That(rig.Turns[0].Reason, Is.EqualTo("max length"));
        Assert.That(rig.HoldCount, Is.Zero);
    }

    [Test]
    public async Task ClosingTheEarsWhileJudging_ForgetsTheTurn()
    {
        var answer = new TaskCompletionSource<float>();
        using var rig = new Rig { Model = _ => answer.Task };
        rig.Assembler.Add(Rig.Speech(16000), 0, 100);
        await rig.WaitFor(() => rig.Asked.Count == 1, "the question");
        rig.Assembler.Clear();
        answer.SetResult(0.95f);
        await Task.Delay(100);
        Assert.That(rig.TurnCount, Is.Zero, "a turn heard while Vertex was talking came out afterwards");
        Assert.That(rig.Assembler.HasPending, Is.False);
    }

    [Test]
    public async Task ATurnThatEnded_StartsTheNextOneFromNothing()
    {
        using var rig = new Rig();
        rig.Assembler.Add(Rig.Speech(16000, 0.1f), 0, 100);
        await rig.WaitFor(() => rig.TurnCount == 1, "the first turn");
        rig.Assembler.Add(Rig.Speech(8000, 0.4f), 500_000, 300);
        await rig.WaitFor(() => rig.TurnCount == 2, "the second turn");
        Assert.That(rig.Turns[1].Samples, Has.Length.EqualTo(8000), "the second turn carried the first one's audio");
        Assert.That(rig.Turns[1].Samples[0], Is.EqualTo(0.4f));
    }

    [Test]
    public async Task TheHoldStopsWhileTheSpeakerIsTalkingAgain_AndStartsOverIfThatWasNoSegment()
    {
        // The continuation's segment is only reported when the speaker FINISHES it, seconds later: a clock that kept running would expire mid-sentence.
        using var rig = new Rig { Model = _ => Task.FromResult(0.1f) };
        rig.Assembler.Add(Rig.Speech(16000), 0, 100);
        await rig.WaitFor(() => rig.HoldCount == 1, "the first hold");

        rig.Assembler.SetSpeaking(true);
        await rig.WaitFor(() => rig.Holds[0].Task.IsCanceled, "the hold to be stopped by speech");
        Assert.That(rig.TurnCount, Is.Zero);
        await Task.Delay(100);
        Assert.That(rig.HoldCount, Is.EqualTo(1), "no new hold may start while the speaker is talking");

        rig.Assembler.SetSpeaking(false);                      // a cough the VAD discarded: no segment follows
        await rig.WaitFor(() => rig.HoldCount == 2, "a fresh hold once it is quiet");
        rig.Holds[1].SetResult();
        await rig.WaitFor(() => rig.TurnCount == 1, "the turn");
        Assert.That(rig.Turns[0].Reason, Is.EqualTo("hold expired"));
    }

    [Test]
    public async Task ASpeakerAlreadyTalkingWhenTheModelAnswers_GetsNoHoldYet()
    {
        var answer = new TaskCompletionSource<float>();
        using var rig = new Rig { Model = _ => answer.Task };
        rig.Assembler.Add(Rig.Speech(16000), 0, 100);
        await rig.WaitFor(() => rig.Asked.Count == 1, "the question");
        rig.Assembler.SetSpeaking(true);                       // they started again while the model was thinking
        answer.SetResult(0.1f);
        await Task.Delay(150);
        Assert.That(rig.HoldCount, Is.Zero, "a hold started under a talking speaker");

        rig.Assembler.Add(Rig.Speech(16000), 30000, 200);       // the continuation arrives
        rig.Assembler.SetSpeaking(false);
        await rig.WaitFor(() => rig.HoldCount >= 1, "the hold for the joined turn");
        Assert.That(rig.TurnCount, Is.Zero);
    }
}
