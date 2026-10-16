using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>Where to point a bow: the arrow's flight (3 blocks per tick, 1% drag, 0.05 gravity) searched for the pitch that arrives at the target's height.</summary>
public class BallisticsTests
{
    [TestCase(5, 0)]
    [TestCase(12, 0)]
    [TestCase(20, 0)]
    [TestCase(30, 0)]
    [TestCase(40, 0)]
    [TestCase(12, 4)]
    [TestCase(12, -4)]
    [TestCase(25, 6)]
    [TestCase(25, -8)]
    public void TheAimedArrowArrivesAtTheTargetsHeight(double distance, double height)
    {
        var aim = Ballistics.Aim(distance, height);
        Assert.That(aim, Is.Not.Null, $"{distance} blocks at height {height} is within range of a full draw");
        var flight = Ballistics.Fly(aim!.Value.Pitch, distance, height)!.Value;
        Assert.That(Math.Abs(flight.HeightError), Is.LessThan(0.1), $"pitch {aim.Value.Pitch:0.00}: the arrow is {flight.HeightError:0.00} blocks off at the target");
        Assert.That(flight.Ticks, Is.EqualTo(aim.Value.Ticks));
    }

    [Test]
    public void FartherMeansHigherAndLongerFlight_AndTheFlatShotIsPreferredToTheLob()
    {
        var near = Ballistics.Aim(10, 0)!.Value;
        var far = Ballistics.Aim(40, 0)!.Value;
        Assert.That(far.Pitch, Is.GreaterThan(near.Pitch), "more distance needs more elevation");
        Assert.That(far.Ticks, Is.GreaterThan(near.Ticks));
        Assert.That(near.Pitch, Is.LessThan(5), "a flat shot at ten blocks, not a high lob");
        Assert.That(near.Pitch, Is.GreaterThan(0), "but above level: the arrow drops on the way");
    }

    [Test]
    public void ATargetBeyondTheBowsRangeHasNoAim()
    {
        Assert.That(Ballistics.Aim(150, 0), Is.Null);
        Assert.That(Ballistics.Aim(300, 0), Is.Null);
        Assert.That(Ballistics.Aim(20, 80), Is.Null, "nor can it hit something 80 blocks up");
    }

    [Test]
    public void AimingAtATargetGivesYawAndPitchInMinecraftsConvention()
    {
        // South (+z) is yaw 0; west (-x) is yaw 90; looking up is negative pitch; an arrow aimed level at distance is aimed a little up.
        var south = Ballistics.AimAt(0, 1.62, 0, 0, 1.62, 15)!.Value;       // at eye height
        Assert.That(south.Yaw, Is.EqualTo(0).Within(1e-6));
        Assert.That(south.Pitch, Is.LessThan(0), "up is negative");
        var west = Ballistics.AimAt(0, 1.62, 0, -15, 1, 0)!.Value;
        Assert.That(west.Yaw, Is.EqualTo(90).Within(1e-6));
        var north = Ballistics.AimAt(0, 1.62, 0, 0, 1, -15)!.Value;
        Assert.That(Math.Abs(north.Yaw), Is.EqualTo(180).Within(1e-6));
        var east = Ballistics.AimAt(0, 1.62, 0, 15, 1, 0)!.Value;
        Assert.That(east.Yaw, Is.EqualTo(-90).Within(1e-6));
    }

    [Test]
    public void AMovingTargetIsLed_ByWhereItWillBeWhenTheArrowArrives()
    {
        var still = Ballistics.AimAt(0, 1.62, 0, 0, 1, 20)!.Value;
        // Walking east (+x) at 0.2 blocks per tick across the line of fire: aim must swing east (negative yaw) of straight south.
        var crossing = Ballistics.AimAt(0, 1.62, 0, 0, 1, 20, velX: 0.2)!.Value;
        Assert.That(crossing.Yaw, Is.LessThan(still.Yaw - 1), "the shot leads the target");
        // The lead is about what the target walks in the flight time: tan(angle) ~ 0.2 * ticks / distance.
        var expected = Math.Atan(0.2 * crossing.Ticks / 20.0) * 180 / Math.PI;
        Assert.That(-(crossing.Yaw - still.Yaw), Is.EqualTo(expected).Within(2.0), "it is led by about the distance walked during the flight");
        // Walking straight toward us needs a lower aim than the target would at its starting distance.
        var coming = Ballistics.AimAt(0, 1.62, 0, 0, 1, 25, velZ: -0.2)!.Value;
        var staying = Ballistics.AimAt(0, 1.62, 0, 0, 1, 25)!.Value;
        Assert.That(coming.Pitch, Is.GreaterThan(staying.Pitch), "closer when it arrives: less elevation (pitch is negative upward)");
    }
}
