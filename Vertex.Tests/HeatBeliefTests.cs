using Vertex.Brain;

namespace Vertex.Tests;

public class HeatBeliefTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 21, 33, 0, DateTimeKind.Utc);

    [Test]
    public void AHighNumberAlone_IsNotDanger()
    {
        var belief = new HeatBelief();
        for (var s = 0; s < 120; s++) belief.NoteReading(56.6, 43, T0.AddSeconds(s));
        Assert.That(belief.Hurts, Is.False, "two minutes at 56.6 and nothing hurt him");
    }

    [Test]
    public void ABurn_MakesItDanger_AndItStaysWhileItKeepsBurning()
    {
        var belief = new HeatBelief();
        Assert.That(belief.NoteBurn(T0), Is.True, "news the first time");
        Assert.That(belief.NoteBurn(T0.AddSeconds(3)), Is.False, "not the second");
        for (var s = 3; s < 120; s++)
        {
            if (s % 3 == 0) belief.NoteBurn(T0.AddSeconds(s));
            belief.NoteReading(56.6, 43, T0.AddSeconds(s));
        }
        Assert.That(belief.Hurts, Is.True);
    }

    [Test]
    public void AwayFromTheHeat_HeKeepsTheBelief_HoweverLong()
    {
        var belief = new HeatBelief();
        belief.NoteBurn(T0);
        for (var s = 1; s < 3600; s += 10) belief.NoteReading(25, 43, T0.AddSeconds(s));
        Assert.That(belief.Hurts, Is.True, "an hour in the plains says nothing about the savanna");
    }

    [Test]
    public void HighReadingsWithNoBurn_EndTheBelief_AndABurnBringsItBack()
    {
        var belief = new HeatBelief();
        belief.NoteBurn(T0);
        var ended = false;
        for (var s = 1; s <= 19; s++) ended |= belief.NoteReading(56.6, 43, T0.AddSeconds(s));
        Assert.That(ended || !belief.Hurts, Is.False, "not after under 20 seconds");
        Assert.That(belief.NoteReading(56.6, 43, T0.AddSeconds(25)), Is.True, "20 seconds hot and unharmed");
        Assert.That(belief.Hurts, Is.False);
        Assert.That(belief.NoteBurn(T0.AddSeconds(100)), Is.True, "news again");
        Assert.That(belief.Hurts, Is.True);
    }
}
