namespace Vertex.People;

/// <summary>
/// Keeping the person you are talking to in the middle of the picture as they move: a small correction toward the attended face every time the camera looks.
/// </summary>
/// <remarks>
/// <para>
/// WHO is followed: the person who last talked to the robot, if they are in view; otherwise the only face in view. With several people and nobody
/// who has spoken, nobody is followed - a robot that swings between faces is worse than one that holds still.
/// </para>
/// <para>
/// HOW: a proportional step (<see cref="Gain"/> of the offset, at most <see cref="MaxStepDegrees"/>) with a dead zone, because the picture is two looks a second
/// and the head takes most of a second to move: asking for the whole offset would overshoot on the next look. The caller must not use a picture taken while the head
/// was moving (the face is not where it was seen), see <see cref="SettleMilliseconds"/>.
/// </para>
/// </remarks>
public static class FaceFollow
{
    /// <summary>Offsets under this many degrees are not corrected: the person is already in the middle.</summary>
    public const double DeadZoneDegrees = 6;

    /// <summary>The fraction of the offset corrected per look.</summary>
    public const double Gain = 0.8;

    /// <summary>The most one correction turns, in degrees.</summary>
    public const double MaxStepDegrees = 25;

    /// <summary>After any move, pictures for this long are ignored: they were taken while the head was still travelling.</summary>
    public const int SettleMilliseconds = 1200;

    /// <summary>The correction for this look, or null when none is called for.</summary>
    /// <param name="faces">The faces in THIS look (not remembered ones).</param>
    /// <param name="attended">The name of the person who last talked to the robot, or null.</param>
    public static GazeTarget? Choose(IReadOnlyList<VisiblePerson> faces, string? attended)
    {
        var usable = faces.Where(f => f.FrameWidth > 0).ToList();
        VisiblePerson? target = null;
        if (attended != null) target = usable.FirstOrDefault(f => string.Equals(f.Name, attended, StringComparison.OrdinalIgnoreCase));
        if (target == null && usable.Count == 1) target = usable[0];
        if (target == null) return null;

        var offset = SpeakerGaze.FaceYawLeftDegrees(target);
        if (Math.Abs(offset) < DeadZoneDegrees) return null;
        var step = Math.Clamp(offset * Gain, -MaxStepDegrees, MaxStepDegrees);
        return new GazeTarget(step * Math.PI / 180, true, target.Name);
    }
}
