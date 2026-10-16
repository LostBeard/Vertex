using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>"Stay here" is the person's word: a Vertex told to wait does not wander off to work on a goal or study blocks, until it is given something to do.</summary>
public class IdlePolicyTests
{
    private static readonly TimeSpan After = TimeSpan.FromSeconds(60);

    private static IdleAction Decide(bool staying = false, bool busy = false, bool goalQueued = false, bool spawned = true, int idleSeconds = 120, bool hasGoal = false, bool study = true, bool unstudied = false) =>
        IdlePolicy.Decide(staying, busy, goalQueued, spawned, TimeSpan.FromSeconds(idleSeconds), After, hasGoal, study, unstudied);

    [Test]
    public void WithAGoalAndNothingElseGoingOnItWorksOnTheGoal_OrStudiesBlocksWhenThereIsNoGoal()
    {
        Assert.That(Decide(hasGoal: true), Is.EqualTo(IdleAction.WorkOnGoal));
        Assert.That(Decide(unstudied: true), Is.EqualTo(IdleAction.StudyBlocks));
        Assert.That(Decide(), Is.EqualTo(IdleAction.Nothing), "no goal and nothing to study");
        Assert.That(Decide(unstudied: true, study: false), Is.EqualTo(IdleAction.Nothing), "studying switched off");
        Assert.That(Decide(hasGoal: true, unstudied: true), Is.EqualTo(IdleAction.WorkOnGoal), "a goal comes before curiosity");
    }

    [Test]
    public void StayingOutranksEverything()
    {
        Assert.That(Decide(staying: true, hasGoal: true), Is.EqualTo(IdleAction.Nothing), "told to wait: no goal");
        Assert.That(Decide(staying: true, unstudied: true), Is.EqualTo(IdleAction.Nothing), "told to wait: no studying either");
        Assert.That(Decide(staying: true, hasGoal: true, idleSeconds: 100000), Is.EqualTo(IdleAction.Nothing), "however long it has been");
    }

    [Test]
    public void ItWaitsForTheQuietToLastAndForEveryOtherReasonNotToAct()
    {
        Assert.That(Decide(hasGoal: true, idleSeconds: 59), Is.EqualTo(IdleAction.Nothing), "someone needed it a moment ago");
        Assert.That(Decide(hasGoal: true, idleSeconds: 60), Is.EqualTo(IdleAction.WorkOnGoal));
        Assert.That(Decide(hasGoal: true, busy: true), Is.EqualTo(IdleAction.Nothing), "already on a job");
        Assert.That(Decide(hasGoal: true, goalQueued: true), Is.EqualTo(IdleAction.Nothing), "a goal step is already being decided");
        Assert.That(Decide(hasGoal: true, spawned: false), Is.EqualTo(IdleAction.Nothing), "not in the world yet");
    }

    [Test]
    public void AnyJobStartedEndsTheStay_ButAReflexThatInterruptsDoesNot()
    {
        var tasks = new TaskRunner();
        var started = new List<string>();
        tasks.Starting += started.Add;
        tasks.Start("walking to Aubri", _ => Task.FromResult((true, "arrived")));
        Assert.That(started, Is.EqualTo(new[] { "walking to Aubri" }), "the brain clears Staying on this");
        tasks.Interrupt("escaping a creeper", _ => Task.FromResult((true, "safe")));
        Assert.That(started, Has.Count.EqualTo(1), "a reflex is not something the person asked for");
        tasks.Stop();
    }
}
