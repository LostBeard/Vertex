using Vertex.People;

namespace Vertex.Tests;

/// <summary>Keeping the attended face in the middle of the picture: who is followed and how much each look corrects.</summary>
public class FaceFollowTests
{
    private const int W = 1280;
    private static VisiblePerson Face(string? name, double centreFraction, float width = 150) =>
        new(name, false, (float)(centreFraction * W - width / 2), width, 0, W);

    [Test]
    public void ThePersonWhoLastSpokeIsFollowed_NotTheOtherFace()
    {
        var step = FaceFollow.Choose([Face("Nikki", 0.25), Face("Todd", 0.8)], attended: "Todd")!;
        Assert.That(step.Name, Is.EqualTo("Todd"));
        Assert.That(step.YawLeft, Is.LessThan(0), "Todd is right of centre: turn right");
    }

    [Test]
    public void WithNobodyAttended_OnlyALoneFaceIsFollowed()
    {
        Assert.That(FaceFollow.Choose([Face("Todd", 0.8)], attended: null), Is.Not.Null);
        Assert.That(FaceFollow.Choose([Face("Todd", 0.8), Face("Nikki", 0.2)], attended: null), Is.Null, "two faces and nobody talking: hold still");
        Assert.That(FaceFollow.Choose([Face("Nikki", 0.2), Face("Aubri", 0.3)], attended: "Todd"), Is.Null, "the person talked to is not in view: do not chase someone else");
    }

    [Test]
    public void ACentredFaceIsLeftAlone()
    {
        Assert.That(FaceFollow.Choose([Face("Todd", 0.5)], "Todd"), Is.Null);
        Assert.That(FaceFollow.Choose([Face("Todd", 0.5 + 4 / SpeakerGaze.HorizontalFieldOfViewDegrees)], "Todd"), Is.Null, "4 degrees off is inside the dead zone");
    }

    [Test]
    public void TheCorrectionIsAFractionOfTheOffset_AndLimited()
    {
        var offset = 30.0;       // degrees to the robot's left
        var step = FaceFollow.Choose([Face("Todd", 0.5 - offset / SpeakerGaze.HorizontalFieldOfViewDegrees)], "Todd")!;
        Assert.That(step.YawLeft * 180 / Math.PI, Is.EqualTo(offset * FaceFollow.Gain).Within(0.01), "not the whole offset: the next look would overshoot");
        var far = FaceFollow.Choose([Face("Todd", 0.02)], "Todd")!;      // far to the left of the picture
        Assert.That(far.YawLeft * 180 / Math.PI, Is.EqualTo(FaceFollow.MaxStepDegrees).Within(0.01));
    }

    [Test]
    public void TheNameMatchIsNotCaseSensitive() => Assert.That(FaceFollow.Choose([Face("todd", 0.8)], "Todd"), Is.Not.Null);
}
