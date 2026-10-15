using Vertex.Reachy;

namespace Vertex.Tests;

/// <summary>Where a voice is, from the microphone array's angle (0 = the robot's left, pi/2 = ahead, pi = its right).</summary>
public class VoiceDirectionTests
{
    private static double Rad(double degreesFromLeft) => degreesFromLeft * Math.PI / 180;

    [TestCase(0, "left", "9")]
    [TestCase(45, "left", "10:30")]
    [TestCase(30, "left", "10")]
    [TestCase(90, "ahead", "12")]
    [TestCase(100, "ahead", "12:30")]
    [TestCase(80, "ahead", "11:30")]
    [TestCase(135, "right", "1:30")]
    [TestCase(180, "right", "3")]
    public void AnAngleIsReadAsSideAndClockPosition(double degrees, string side, string clock)
    {
        var bearing = new VoiceBearing(Rad(degrees), 1);
        Assert.That(bearing.Side, Is.EqualTo(side));
        Assert.That(bearing.Clock, Is.EqualTo(clock));
    }

    [Test]
    public void TheMedianOfTheRecentSpeechReadingsIgnoresAReflection()
    {
        // The measured shape of a person at 10:30 (45 degrees on this scale) turning their head: most readings near 40, a few bounced off the room.
        var voice = new VoiceDirection();
        double[] readings = [38, 40, 88, 41, 37, 85, 43, 39, 44];
        for (var i = 0; i < readings.Length; i++) voice.Add(Rad(readings[i]), speech: true, tick: i * 200);
        var bearing = voice.Current(tick: readings.Length * 200)!.Value;
        Assert.That(bearing.Radians * 180 / Math.PI, Is.EqualTo(41).Within(3));
        Assert.That(bearing.Side, Is.EqualTo("left"));
    }

    [Test]
    public void ReadingsWithoutSpeechAreIgnored_BecauseTheChipRepeatsItsLastAngle()
    {
        var voice = new VoiceDirection();
        for (var i = 0; i < 5; i++) voice.Add(Rad(30), speech: false, tick: i * 200);
        Assert.That(voice.Current(1000), Is.Null);
        voice.Add(Rad(150), speech: true, tick: 1200);
        Assert.That(voice.Current(1200)!.Value.Side, Is.EqualTo("right"));
    }

    [Test]
    public void TheBearingIsRememberedForAWhile_ThenForgotten()
    {
        var voice = new VoiceDirection();
        voice.Add(Rad(20), true, 0);
        Assert.That(voice.Speaking(100), Is.True);
        Assert.That(voice.Speaking(3000), Is.False, "speaking now means within the window");
        Assert.That(voice.Current(3000), Is.Not.Null, "still remembered after it stopped");
        Assert.That(voice.Current(6000), Is.Null, "forgotten after the hold");
    }

    [Test]
    public void ANewSpeakerReplacesTheOldBearingOnceTheWindowHasPassed()
    {
        var voice = new VoiceDirection();
        for (var i = 0; i < 6; i++) voice.Add(Rad(20), true, i * 200);
        for (var i = 0; i < 6; i++) voice.Add(Rad(160), true, 4000 + i * 200);
        Assert.That(voice.Current(5200)!.Value.Side, Is.EqualTo("right"));
    }
}
