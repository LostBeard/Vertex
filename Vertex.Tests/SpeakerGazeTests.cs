using Vertex.People;
using Vertex.Reachy;

namespace Vertex.Tests;

/// <summary>Where to look for whoever is talking, from the voice's direction and the faces in the camera (angles from where the head points now; positive = the robot's left).</summary>
public class SpeakerGazeTests
{
    private const int W = 1280;
    private static VoiceBearing Voice(double degreesFromLeft) => new(degreesFromLeft * Math.PI / 180, 5);
    private static VisiblePerson Face(string? name, double centreFraction, float width = 150) =>
        new(name, false, (float)(centreFraction * W - width / 2), width, 0, W);
    private static double Deg(GazeTarget t) => t.YawLeft * 180 / Math.PI;

    [Test]
    public void AFaceInTheLeftOfThePicture_IsTheRobotsLeft()
    {
        // A quarter of the way across: 25% of the field of view to the left of centre.
        Assert.That(SpeakerGaze.FaceYawLeftDegrees(Face("a", 0.25)), Is.EqualTo(0.25 * SpeakerGaze.HorizontalFieldOfViewDegrees).Within(0.01));
        Assert.That(SpeakerGaze.FaceYawLeftDegrees(Face("a", 0.75)), Is.EqualTo(-0.25 * SpeakerGaze.HorizontalFieldOfViewDegrees).Within(0.01));
        Assert.That(SpeakerGaze.FaceYawLeftDegrees(Face("a", 0.5)), Is.EqualTo(0).Within(0.01));
    }

    [Test]
    public void NoVoice_NoTurn() => Assert.That(SpeakerGaze.Choose(null, [Face("Nikki", 0.2)]), Is.Null);

    [Test]
    public void AVoiceFromTheLeftWithNoFaceInView_TurnsTowardTheVoice()
    {
        var t = SpeakerGaze.Choose(Voice(45), [])!;     // 45 on the array's scale = 45 degrees left of ahead
        Assert.That(t.FromFace, Is.False);
        Assert.That(Deg(t), Is.EqualTo(45).Within(0.01));
        Assert.That(SpeakerGaze.Choose(Voice(135), [])!.YawLeft, Is.LessThan(0), "a voice from the right turns right");
    }

    [Test]
    public void AVoiceFromAheadIsAlreadyFaced()
    {
        Assert.That(SpeakerGaze.Choose(Voice(90), []), Is.Null);
        Assert.That(SpeakerGaze.Choose(Voice(97), []), Is.Null, "inside the dead zone");
    }

    [Test]
    public void TheFaceThatMatchesTheVoiceIsCentred_NotTheSpotTheSoundSeemedToComeFrom()
    {
        // Two people: one 40 degrees to the robot's left, one 35 to its right. A voice that the array puts 52 degrees left is the left-hand person's (the array is off by 12).
        var left = Face("Nikki", 0.5 - 40 / SpeakerGaze.HorizontalFieldOfViewDegrees);
        var right = Face("Todd", 0.5 + 35 / SpeakerGaze.HorizontalFieldOfViewDegrees);
        var t = SpeakerGaze.Choose(Voice(90 - 52), [left, right])!;
        Assert.That(t.FromFace, Is.True);
        Assert.That(t.Name, Is.EqualTo("Nikki"));
        Assert.That(Deg(t), Is.EqualTo(40).Within(0.5), "centres her face (40), not the voice (52)");
        // And the other way round.
        var u = SpeakerGaze.Choose(Voice(90 + 30), [left, right])!;
        Assert.That(u.Name, Is.EqualTo("Todd"));
        Assert.That(Deg(u), Is.EqualTo(-35).Within(0.5));
    }

    [Test]
    public void AFaceTheVoiceDoesNotMatch_IsIgnored_AndTheVoiceIsFollowed()
    {
        // The only person in view is 35 degrees to the right; the voice is 50 to the left (someone out of frame): do not turn to the wrong person.
        var right = Face("Todd", 0.5 + 35 / SpeakerGaze.HorizontalFieldOfViewDegrees);
        var t = SpeakerGaze.Choose(Voice(90 - 50), [right])!;
        Assert.That(t.FromFace, Is.False);
        Assert.That(Deg(t), Is.EqualTo(50).Within(0.01));
    }

    [Test]
    public void AlreadyLookingAtTheSpeaker_NoTurn()
    {
        Assert.That(SpeakerGaze.Choose(Voice(90 - 10), [Face("Nikki", 0.5)]), Is.Null);
    }

    [Test]
    public void TheTurnIsLimitedToWhatTheRobotCanDo()
    {
        Assert.That(Deg(SpeakerGaze.Choose(Voice(0), [])!), Is.EqualTo(SpeakerGaze.MaxTurnDegrees).Within(0.01));
        Assert.That(Deg(SpeakerGaze.Choose(Voice(180), [])!), Is.EqualTo(-SpeakerGaze.MaxTurnDegrees).Within(0.01));
    }
}
