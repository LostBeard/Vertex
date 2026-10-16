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
}

/// <summary>
/// The decision the idle check makes, as a function of what it can see (so it can be tested without a game or a language model). "Stay here" is the person's word and outranks every
/// reason to act: a Vertex told to wait does not wander off to work on a goal or to study blocks until it is given something to do.
/// </summary>
public static class IdlePolicy
{
    public static IdleAction Decide(bool staying, bool busy, bool goalQueued, bool spawned, TimeSpan idleFor, TimeSpan idleBeforeActing, bool hasGoal, bool studyWhenIdle, bool hasUnstudiedBlocks)
    {
        if (staying || busy || goalQueued || !spawned || idleFor < idleBeforeActing) return IdleAction.Nothing;
        if (hasGoal) return IdleAction.WorkOnGoal;
        return studyWhenIdle && hasUnstudiedBlocks ? IdleAction.StudyBlocks : IdleAction.Nothing;
    }
}
