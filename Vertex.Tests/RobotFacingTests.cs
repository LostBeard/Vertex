using SpawnDev.Reachy;

namespace Vertex.Tests;

/// <summary>
/// The robot turns to face a speaker and STAYS facing them: every later gesture is made around that heading (otherwise the first nod after turning would swing the robot back to the front).
/// A recording stand-in for the daemon, so no robot is needed.
/// </summary>
public class RobotFacingTests
{
    private sealed class RecordingMotion : IReachyMotion
    {
        public readonly List<(double? Body, XyzRpyPose? Head)> Calls = new();
        public Task<MoveHandle?> GotoAsync(double? bodyYaw = null, XyzRpyPose? headPose = null, (double Left, double Right)? antennas = null,
            double duration = 1.0, Interpolation interpolation = Interpolation.MinJerk, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((bodyYaw, headPose));
            return Task.FromResult<MoveHandle?>(null);
        }
        public (double? Body, XyzRpyPose? Head) Last { get { lock (Calls) return Calls[^1]; } }
    }

    private static readonly GestureStyle Style = GestureStyle.Default;

    [Test]
    public async Task TheHeadFacesTheNewDirection_BecauseItsYawIsAWorldAngle_AndTheTorsoFollows()
    {
        // Measured on the robot 2026-10-15: the daemon's head yaw is world-referenced. Turning only the body (head yaw 0) leaves the camera facing forward.
        var motion = new RecordingMotion();
        await using var body = new ReachyBody(motion);
        await body.TurnTowardAsync(0.6, Style);
        Assert.That(body.FacingYaw, Is.EqualTo(0.6).Within(1e-9), "the camera heading");
        Assert.That(body.FacingBodyYaw, Is.EqualTo(0.6).Within(1e-9));
        Assert.That(motion.Last.Body, Is.EqualTo(0.6).Within(1e-9));
        Assert.That(motion.Last.Head!.Yaw, Is.EqualTo(0.6).Within(1e-9), "the head is commanded to the WORLD angle, not to a remainder");
    }

    [Test]
    public async Task ALargerTurnKeepsTheTorsoWithinItsRange_WhileTheHeadGoesAllTheWay_AndIsLimited()
    {
        var motion = new RecordingMotion();
        await using var body = new ReachyBody(motion);
        await body.TurnTowardAsync(0.6, Style);
        await body.TurnTowardAsync(0.5, Style);          // 1.1 in total: torso at its limit, head at 1.1
        Assert.That(body.FacingBodyYaw, Is.EqualTo(ReachyBody.BodyYawMax).Within(1e-9));
        Assert.That(body.FacingYaw, Is.EqualTo(1.1).Within(1e-9));
        Assert.That(motion.Last.Head!.Yaw, Is.EqualTo(1.1).Within(1e-9));
        Assert.That(Math.Abs(motion.Last.Head!.Yaw - motion.Last.Body!.Value), Is.LessThan(65 * Math.PI / 180), "the daemon needs the body within about 65 degrees of the head");
        await body.TurnTowardAsync(5, Style);            // far too much: limited to the head's range
        Assert.That(body.FacingYaw, Is.EqualTo(ReachyBody.FacingMax).Within(1e-9));
        await body.TurnTowardAsync(-10, Style);
        Assert.That(body.FacingYaw, Is.EqualTo(-ReachyBody.FacingMax).Within(1e-9));
    }

    [Test]
    public async Task AGestureAfterTheTurnIsMadeAroundTheNewHeading()
    {
        var motion = new RecordingMotion();
        await using var body = new ReachyBody(motion);
        await body.TurnTowardAsync(1.1, Style);          // torso 0.70, head 1.1 (world)
        motion.Calls.Clear();
        await body.PerformAsync(Gesture.Shake, Style);   // the head shakes: its yaw swings around the heading, not around 0
        var shakeYaws = motion.Calls.Where(c => c.Head != null).Select(c => c.Head!.Yaw).ToList();
        Assert.That(shakeYaws.Count, Is.GreaterThan(2));
        Assert.That(shakeYaws.Min(), Is.LessThan(1.1), "swings below the heading");
        Assert.That(shakeYaws.Max(), Is.GreaterThan(1.1), "and above it");
        Assert.That(shakeYaws.All(y => y > 0.5), Is.True, "never back toward the front");
        var rest = motion.Last;
        Assert.That(rest.Body, Is.EqualTo(ReachyBody.BodyYawMax).Within(1e-9), "it ends back at the heading, not at the front");
        Assert.That(rest.Head!.Yaw, Is.EqualTo(1.1).Within(1e-9));
    }

    [Test]
    public async Task ATurningGestureIsRelativeToTheHeading_NotToTheFront()
    {
        var motion = new RecordingMotion();
        await using var body = new ReachyBody(motion);
        await body.TurnTowardAsync(0.3, Style);
        motion.Calls.Clear();
        await body.PerformAsync(Gesture.TurnBody, Style);
        var turned = motion.Calls.First(c => c.Body != null && Math.Abs(c.Body.Value - 0.3) > 0.05).Body!.Value;
        Assert.That(turned, Is.GreaterThan(0.3), "the body-turn gesture goes further from where the robot already faces");
    }

    [Test]
    public async Task RecentringTurnsBackToTheFront()
    {
        var motion = new RecordingMotion();
        await using var body = new ReachyBody(motion);
        await body.TurnTowardAsync(1.1, Style);
        await body.RecentreAsync(Style);
        Assert.That(body.FacingYaw, Is.EqualTo(0).Within(1e-9));
        Assert.That(motion.Last.Body, Is.EqualTo(0).Within(1e-9));
        Assert.That(motion.Last.Head!.Yaw, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public async Task ATurnTooSmallToSeeIsNotMade()
    {
        var motion = new RecordingMotion();
        await using var body = new ReachyBody(motion);
        await body.TurnTowardAsync(0.01, Style);
        Assert.That(motion.Calls, Is.Empty);
    }
}
