using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Physics;
using SpawnDev.Minecraft.World;

namespace Vertex.Tests;

/// <summary>
/// Player physics against the game's published movement numbers (minecraft.wiki / mcpk): jump apex 1.2522,
/// walk 4.317 m/s, sprint 5.612 m/s, terminal fall 3.92 blocks/tick. A synthetic world: stone floor whose top is
/// at y=64.
/// </summary>
public class PhysicsTests
{
    private static GameData D => GameData.Vanilla;

    private static GameWorld FlatWorld(Action<GameWorld>? build = null)
    {
        var world = new GameWorld();
        var stone = D.Block("stone")!.DefaultState;
        for (var cx = -2; cx <= 2; cx++)
        for (var cz = -2; cz <= 2; cz++)
        {
            var sections = Enumerable.Range(0, 24).Select(_ => new ChunkSection()).ToArray();
            var column = new ChunkColumn(cx, cz, -64, sections);
            for (var x = 0; x < 16; x++)
            for (var z = 0; z < 16; z++)
                column.Set((cx << 4) + x, 63, (cz << 4) + z, stone);
            world.SetColumn(column);
        }
        build?.Invoke(world);
        return world;
    }

    private static PlayerState StandingAt(double x, double z) => new() { X = x, Y = 64, Z = z, OnGround = true };

    [Test]
    public void Jump_ReachesTheVanillaApex()
    {
        var physics = new PlayerPhysics(FlatWorld());
        var s = StandingAt(0.5, 0.5);
        var c = new PlayerControls { Jump = true };
        var apex = 64.0;
        var landed = false;
        for (var t = 0; t < 30; t++)
        {
            physics.Tick(s, c);
            c.Jump = false;
            apex = Math.Max(apex, s.Y);
            if (t > 2 && s.OnGround) { landed = true; break; }
        }
        Assert.That(apex - 64, Is.EqualTo(1.2522033).Within(1e-6));
        Assert.That(landed);
        Assert.That(s.Y, Is.EqualTo(64.0), "lands exactly on the floor");
    }

    [TestCase(false, 4.317)]
    [TestCase(true, 5.612)]
    public void WalkAndSprint_ReachVanillaSpeeds(bool sprint, double metresPerSecond)
    {
        var physics = new PlayerPhysics(FlatWorld());
        var s = StandingAt(-30.5, 0.5);
        s.Yaw = -90; // facing +X
        var c = new PlayerControls { Forward = true, Sprint = sprint };
        for (var t = 0; t < 40; t++) physics.Tick(s, c);
        var before = s.X;
        physics.Tick(s, c);
        Assert.That((s.X - before) * 20, Is.EqualTo(metresPerSecond).Within(0.001));
        Assert.That(s.Y, Is.EqualTo(64.0));
        Assert.That(s.Z, Is.EqualTo(0.5).Within(1e-9), "straight line");
    }

    [Test]
    public void Fall_FollowsVanillaVelocitiesAndLandsOnTheFloor()
    {
        var physics = new PlayerPhysics(FlatWorld());
        var s = new PlayerState { X = 0.5, Y = 300, Z = 0.5 };
        var c = new PlayerControls();
        physics.Tick(s, c);
        // 0.98f is a float in vanilla: -0.08 * (double)0.98f = -0.07840000152587891.
        Assert.That(s.VelY, Is.EqualTo(-0.08 * 0.98f));
        physics.Tick(s, c);
        Assert.That(s.VelY, Is.EqualTo((-0.08 * 0.98f - 0.08) * 0.98f));
        var maxSpeed = 0.0;
        for (var t = 0; t < 400 && !s.OnGround; t++)
        {
            physics.Tick(s, c);
            maxSpeed = Math.Max(maxSpeed, -s.VelY);
        }
        Assert.That(s.OnGround);
        Assert.That(s.Y, Is.EqualTo(64.0));
        Assert.That(maxSpeed, Is.LessThan(3.92), "never beyond terminal velocity (0.08 * 0.98 / 0.02)");
    }

    [Test]
    public void Walking_StepsOntoASlabButNotOntoABlock()
    {
        var slab = D.WithProperty(D.Block("stone_slab")!.DefaultState, "type", "bottom");
        var stone = D.Block("stone")!.DefaultState;
        var world = FlatWorld(w =>
        {
            for (var x = 3; x < 12; x++) for (var z = -3; z <= 3; z++) w.SetState(x, 64, z, slab); // a 0.5 step up from x=3
            for (var x = 3; x < 12; x++) for (var z = -3; z <= 3; z++) w.SetState(x, 64, z + 10, stone); // a 1.0 step from x=3, further along z
        });
        var physics = new PlayerPhysics(world);

        var s = StandingAt(0.5, 0.5);
        s.Yaw = -90;
        var c = new PlayerControls { Forward = true };
        for (var t = 0; t < 30; t++) physics.Tick(s, c);
        Assert.That(s.Y, Is.EqualTo(64.5), "stepped up onto the slab");
        Assert.That(s.X, Is.GreaterThan(3.5));

        var w2 = StandingAt(0.5, 10.5);
        w2.Yaw = -90;
        for (var t = 0; t < 30; t++) physics.Tick(w2, c);
        Assert.That(w2.Y, Is.EqualTo(64.0), "cannot step 1 block");
        Assert.That(w2.X, Is.EqualTo(3 - 0.3).Within(1e-7), "stopped against the wall");
        Assert.That(w2.HorizontalCollision);

        // Jumping gets over it.
        c.Jump = true;
        for (var t = 0; t < 30; t++) physics.Tick(w2, c);
        c.Jump = false;
        for (var t = 0; t < 20; t++) physics.Tick(w2, c);
        Assert.That(w2.Y, Is.EqualTo(65.0));
        Assert.That(w2.X, Is.GreaterThan(3.3));
    }

    [Test]
    public void Fence_CannotBeJumpedOver()
    {
        var fence = D.Block("oak_fence")!.DefaultState;
        var world = FlatWorld(w => { for (var z = -3; z <= 3; z++) w.SetState(3, 64, z, fence); });
        var physics = new PlayerPhysics(world);
        var s = StandingAt(0.5, 0.5);
        s.Yaw = -90;
        var c = new PlayerControls { Forward = true, Jump = true };
        for (var t = 0; t < 60; t++) physics.Tick(s, c);
        Assert.That(s.X, Is.LessThan(3.5), "a 1.5-tall fence stops a 1.25 jump");
    }

    [Test]
    public void Ice_KeepsYouSliding()
    {
        var ice = D.Block("ice")!.DefaultState;
        var world = FlatWorld(w => { for (var x = -40; x < 40; x++) for (var z = -2; z <= 2; z++) w.SetState(x, 63, z, ice); });
        double Slide(GameWorld w)
        {
            var physics = new PlayerPhysics(w);
            var s = StandingAt(-30.5, 0.5);
            s.Yaw = -90;
            var c = new PlayerControls { Forward = true };
            for (var t = 0; t < 60; t++) physics.Tick(s, c);
            c.Forward = false;
            var start = s.X;
            for (var t = 0; t < 60; t++) physics.Tick(s, c);
            return s.X - start;
        }
        var onIce = Slide(world);
        var onStone = Slide(FlatWorld());
        // Coasting = v * f / (1 - f) with f = friction * 0.91: ~1.7 blocks on ice (0.98), ~0.26 on stone (0.6).
        Assert.That(onIce, Is.EqualTo(1.70).Within(0.05));
        Assert.That(onStone, Is.EqualTo(0.26).Within(0.02));
    }

    [Test]
    public void Water_SinksSlowlyAndSwimsUp()
    {
        var water = D.Block("water")!.DefaultState;
        var world = FlatWorld(w => { for (var x = -5; x <= 5; x++) for (var z = -5; z <= 5; z++) for (var y = 64; y < 70; y++) w.SetState(x, y, z, water); });
        var physics = new PlayerPhysics(world);
        var s = new PlayerState { X = 0.5, Y = 67, Z = 0.5 };
        var c = new PlayerControls();
        for (var t = 0; t < 10; t++) physics.Tick(s, c);
        Assert.That(s.InWater);
        Assert.That(s.VelY, Is.GreaterThan(-0.1), "water slows falling");
        c.Jump = true;
        var y0 = s.Y;
        for (var t = 0; t < 20; t++) physics.Tick(s, c);
        Assert.That(s.Y, Is.GreaterThan(y0), "holding jump swims up");
    }

    [Test]
    public void Mth_SinTableMatchesVanilla()
    {
        // Mth.sin quantizes to 65536 steps: sin(90 deg) is exactly 1, sin(1 rad) differs from Math.Sin.
        Assert.That(Mth.Sin(90 * Mth.DegToRad), Is.EqualTo(1f));
        Assert.That(Mth.Cos(0), Is.EqualTo(1f));
        Assert.That(Math.Abs(Mth.Sin(1f) - (float)Math.Sin(1)), Is.LessThan(1e-4f));
    }
}
