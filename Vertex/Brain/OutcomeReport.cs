namespace Vertex.Brain;

/// <summary>
/// What the language model is told when a job Vertex was doing ends. It used to say "you finished {task}" whether the job worked or not, with the real result tacked on after it, and an
/// 8B model reads the first words and ignores the rest: on 2026-11-01 a planting job that failed ("I don't have any carrot to plant") was reported to Aubs's family as "I finished
/// planting a field of carrots" while he stood still. A failure now says it did NOT work and that nothing was done, and every report ends by telling him to say only what the result says.
/// </summary>
public static class OutcomeReport
{
    private const string Honesty = " Say only what this says: never claim you did something it does not say, and do not make up details.";

    /// <summary>The text for the model (the caller wraps it in the "[Game update...]" frame and adds the "tell your friends" instruction).</summary>
    /// <param name="goalText">The long-term goal this job was a step of, if any.</param>
    public static string ForModel(TaskOutcome outcome, string? goalText)
    {
        var step = goalText != null ? $", a step toward your goal \"{goalText}\"" : "";
        if (outcome.Success)
            return $"you finished {outcome.Task}{step}. Result: {outcome.Summary}.{Honesty}"
                + (goalText != null ? " Is the goal reached now? If yes, call finish_goal." : "");
        return $"you tried {outcome.Task}{step}, and it did NOT work: nothing was done. What happened: {outcome.Summary}.{Honesty}"
            + (goalText != null ? " If you cannot do this with what you have, call finish_goal (the goal is no longer wanted); otherwise say what you will try next." : "");
    }
}
