using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Pathing;

namespace Vertex.Tests;

/// <summary>
/// "Within reach" has to mean one thing. The walk to a crafting table ends where the table's nearest point is within reach of his eyes (as the server measures it); the check that followed measured to
/// the table's centre, up to 0.87 further. From a spot the walk called reached, the table was "out of reach", and with 3 planks he could not make another: "I need a crafting table (and could not
/// make or reach one)", 12 jobs in 19 hours (2026-11-06).
/// </summary>
public class ReachTests
{
    [Test]
    public void ABlockTheWalkCallsReached_IsWithinReach_ThoughItsCentreIsFurtherThanTheReach()
    {
        // Feet at 0 0 0; the table three across, two along and three up.
        Assert.That(new GoalReachBlock(3, 3, 2, 3.5).IsReached(0, 0, 0), Is.True, "the walk stops here");
        var eye = (0.5, 1.62, 0.5);
        var toCentre = Math.Sqrt(3.0 * 3.0 + (3.5 - 1.62) * (3.5 - 1.62) + 2.0 * 2.0);
        Assert.That(toCentre, Is.GreaterThan(4.0), "the setup: measured to its centre it is further than the 4.0 the old check allowed");
        Assert.That(MinecraftClient.WithinReach(eye, 3, 3, 2, 4.0), Is.True, "and it is in reach");
        Assert.That(MinecraftClient.WithinReach(eye, 3, 3, 2, 3.5), Is.True, "by the walk's own measure too");
    }

    [Test]
    public void ABlockTooFarAway_IsNot()
    {
        var eye = (0.5, 1.62, 0.5);
        Assert.That(MinecraftClient.WithinReach(eye, 6, 0, 0, 4.5), Is.False);
        Assert.That(MinecraftClient.WithinReach(eye, 5, 0, 0, 4.5), Is.False, "4.5 across and 0.62 down is 4.54");
        Assert.That(MinecraftClient.WithinReach(eye, 4, 0, 0, 4.5), Is.True);
    }
}
