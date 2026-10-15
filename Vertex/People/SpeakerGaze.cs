using Vertex.Reachy;

namespace Vertex.People;

/// <summary>Where the robot should look to face whoever is talking.</summary>
/// <param name="YawLeft">The turn, in radians, from where the head points now: positive to the robot's left, negative to its right.</param>
/// <param name="FromFace">True when a face in the camera was matched to the voice (the turn centres that face); false when only the voice direction is known.</param>
/// <param name="Name">The matched person's name (or visitor name), when a face was matched.</param>
public sealed record GazeTarget(double YawLeft, bool FromFace, string? Name);

/// <summary>
/// Decides where to look when someone talks, from the two things the robot has: the direction the voice came from (the microphone array, coarse and a little jumpy)
/// and the faces in the camera (precise, but no idea who is talking). A face whose direction matches the voice's is the speaker's; the turn centres THAT face, so the robot lands
/// looking at a person and not at the spot the sound seemed to come from. With no matching face it turns toward the voice.
/// </summary>
/// <remarks>
/// <para>
/// Both angles are measured from where the head points now: the microphone array and the camera are in the head. The voice: 0 = left, 90 = ahead, 180 = right, and ahead-or-behind
/// (a line of four microphones cannot tell them apart), so a voice from behind is read as ahead and no turn is made for it unless a face says otherwise.
/// The camera: a picture's right side is the robot's right.
/// </para>
/// <para>
/// The camera's field of view is the one the daemon reports for the sensor (focal length 2,002 px over 3,840 px: 87.6 degrees across); how well that holds for the 1280 x 720 mode the video
/// arrives in is checked live, not assumed - see <see cref="HorizontalFieldOfViewDegrees"/>.
/// </para>
/// </remarks>
public static class SpeakerGaze
{
    /// <summary>Across the picture, in degrees. From the daemon's camera intrinsics (fx 2001.8 px at 3,840 px wide); unverified for the 720p mode until measured on a person at a known angle.</summary>
    public static double HorizontalFieldOfViewDegrees { get; set; } = 87.0;

    /// <summary>A face counts as the speaker's when its direction is within this many degrees of the voice's (the array is good to about 10 degrees and a head turn bounces the sound).</summary>
    public const double MatchToleranceDegrees = 25;

    /// <summary>Turns smaller than this are not made: facing someone already.</summary>
    public const double DeadZoneDegrees = 8;

    /// <summary>The robot can turn this far in total (body 40 degrees plus head 29), in degrees.</summary>
    public const double MaxTurnDegrees = 69;

    /// <summary>The direction of a face from where the camera points, in degrees: positive to the robot's left.</summary>
    public static double FaceYawLeftDegrees(VisiblePerson face)
    {
        if (face.FrameWidth <= 0) throw new ArgumentException("the face has no picture width", nameof(face));
        var centre = face.Left + face.Width / 2.0;
        return -((centre / face.FrameWidth) - 0.5) * HorizontalFieldOfViewDegrees;
    }

    /// <summary>Where to look for a voice at <paramref name="voice"/> with these <paramref name="faces"/> in view; null when no turn is called for.</summary>
    public static GazeTarget? Choose(VoiceBearing? voice, IReadOnlyList<VisiblePerson> faces)
    {
        if (voice is not { } v) return null;
        var voiceYaw = -v.DegreesFromAhead;       // positive left
        var candidates = faces.Where(f => f.FrameWidth > 0).Select(f => (Face: f, Yaw: FaceYawLeftDegrees(f))).ToList();

        var match = candidates.Where(c => Math.Abs(c.Yaw - voiceYaw) <= MatchToleranceDegrees).OrderBy(c => Math.Abs(c.Yaw - voiceYaw)).Cast<(VisiblePerson Face, double Yaw)?>().FirstOrDefault();
        if (match is { } m)
        {
            if (Math.Abs(m.Yaw) < DeadZoneDegrees) return null;
            return new GazeTarget(Radians(m.Yaw), true, m.Face.Name);
        }
        if (Math.Abs(voiceYaw) < DeadZoneDegrees) return null;
        return new GazeTarget(Radians(voiceYaw), false, null);
    }

    private static double Radians(double degrees) => Math.Clamp(degrees, -MaxTurnDegrees, MaxTurnDegrees) * Math.PI / 180;
}
