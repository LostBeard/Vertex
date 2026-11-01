namespace Vertex.Brain;

/// <summary>What Vertex may do on its own when nobody needs it.</summary>
public enum IdleAction
{
    /// <summary>Nothing: wait.</summary>
    Nothing,
    /// <summary>Work on the next long-running goal.</summary>
    WorkOnGoal,
    /// <summary>Be curious: feel out the unknown blocks nearby.</summary>
    StudyBlocks,
    /// <summary>Nothing to do and nobody needing it for a good while: pick a small project of its own (self-directed play).</summary>
    ChooseProject,
}

/// <summary>
/// The decision the idle check makes, as a function of what it can see (so it can be tested without a game or a language model). "Stay here" is the person's word and outranks every
/// reason to act: a Vertex told to wait does not wander off to work on a goal or to study blocks until it is given something to do.
/// </summary>
public static class IdlePolicy
{
    /// <param name="selfDirected">Self-directed play is on (off by default: it is the person's choice to let Vertex play on its own).</param>
    /// <param name="idleBeforeProject">How long nothing may have needed Vertex before it chooses a project (longer than for a goal it already has).</param>
    /// <param name="fit">Healthy, fed, daytime and the project cooldown over: the conditions the brain checks that are not about time idle.</param>
    public static IdleAction Decide(bool staying, bool busy, bool goalQueued, bool spawned, TimeSpan idleFor, TimeSpan idleBeforeActing, bool hasGoal, bool studyWhenIdle, bool hasUnstudiedBlocks,
        bool selfDirected = false, TimeSpan idleBeforeProject = default, bool fit = false)
    {
        if (staying || busy || goalQueued || !spawned || idleFor < idleBeforeActing) return IdleAction.Nothing;
        if (hasGoal) return IdleAction.WorkOnGoal;
        if (studyWhenIdle && hasUnstudiedBlocks) return IdleAction.StudyBlocks;
        return selfDirected && fit && idleFor >= idleBeforeProject ? IdleAction.ChooseProject : IdleAction.Nothing;
    }
}
