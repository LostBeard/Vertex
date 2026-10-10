using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.World;

namespace SpawnDev.Minecraft.Physics;

/// <summary>What the player is pressing this tick (vanilla Input).</summary>
public sealed class PlayerControls
{
    public bool Forward, Back, Left, Right, Jump, Sneak, Sprint;

    public void Clear() => Forward = Back = Left = Right = Jump = Sneak = Sprint = false;

    /// <summary>player_input flag byte: forward 1, backward 2, left 4, right 8, jump 16, shift 32, sprint 64.</summary>
    public byte Flags => (byte)((Forward ? 1 : 0) | (Back ? 2 : 0) | (Left ? 4 : 0) | (Right ? 8 : 0) | (Jump ? 16 : 0) | (Sneak ? 32 : 0) | (Sprint ? 64 : 0));
}

/// <summary>The simulated player body: position, velocity and the collision/fluid flags vanilla tracks.</summary>
public sealed class PlayerState
{
    public double X, Y, Z;
    public double VelX, VelY, VelZ;
    public float Yaw, Pitch;
    public bool OnGround, HorizontalCollision, VerticalCollision, MinorHorizontalCollision;
    public bool Sprinting, InWater, InLava, OnClimbable;
    public double WaterHeight, LavaHeight;
    /// <summary>The input this tick after LocalPlayer.modifyInput (xxa, zza).</summary>
    public float InputStrafe, InputForward;
    public int NoJumpDelay;
    public double FallDistance;
    /// <summary>The block the player stands on (vanilla mainSupportingBlockPos), when on ground.</summary>
    public (int X, int Y, int Z)? SupportingBlock;
    /// <summary>Cobweb/berry bush/powder snow slow-down for the next move (vanilla stuckSpeedMultiplier).</summary>
    public double StuckX, StuckY, StuckZ;

    public const float Width = 0.6f;
    public const float Height = 1.8f;
    public const float EyeHeight = 1.62f;

    public Aabb Box => Aabb.AtFeet(X, Y, Z, Width, Height);

    public PlayerState Clone() => (PlayerState)MemberwiseClone();
}

/// <summary>
/// Vanilla 1.21.5 player movement, ported from the game's own structure (LivingEntity.aiStep/travel/travelInAir/
/// travelInFluid, Entity.move/collide/collideWithShapes/step-up) and its constants (read from the server
/// bytecode): the client must land exactly where vanilla would, or Paper rejects the move ("moved wrongly").
/// One call to Tick = one client tick (50 ms).
/// </summary>
public sealed class PlayerPhysics
{
    private readonly GameWorld _world;
    private readonly GameData _data;
    private readonly int _cobweb, _sweetBerry, _powderSnow, _scaffolding, _ladder, _vine, _honey, _slime, _bubbleColumn;
    private readonly HashSet<int> _climbable;
    private readonly HashSet<int> _bedLike;

    public PlayerPhysics(GameWorld world)
    {
        _world = world;
        _data = world.Data;
        int Id(string n) => _data.Block(n)?.Id ?? -1;
        _cobweb = Id("cobweb");
        _sweetBerry = Id("sweet_berry_bush");
        _powderSnow = Id("powder_snow");
        _scaffolding = Id("scaffolding");
        _ladder = Id("ladder");
        _vine = Id("vine");
        _honey = Id("honey_block");
        _slime = Id("slime_block");
        _bubbleColumn = Id("bubble_column");
        _climbable = _data.BlockTag("minecraft:climbable").Select(n => _data.Block(n)!.Id).ToHashSet();
        _bedLike = _data.BlockTag("minecraft:beds").Select(n => _data.Block(n)!.Id).ToHashSet();
    }

    // Attributes the server can change (step_height, movement_speed...), refreshed by the owner each tick.
    public double MovementSpeed = 0.1;      // movement_speed without the sprint modifier
    public double JumpStrength = 0.42;
    public double Gravity = 0.08;
    public double StepHeight = 0.6;
    public double SneakingSpeed = 0.3;
    public double WaterMovementEfficiency;
    public bool SlowFalling, DolphinsGrace;
    public int LevitationAmplifier = -1, JumpBoostAmplifier = -1;

    /// <summary>Test-only: ignore block collisions (proves the server catches an illegal move).</summary>
    internal bool NoClip;

    /// <summary>Creative/spectator flight (abilities.flying) and its speed (abilities flying speed, 0.05).</summary>
    public bool Flying;
    public float FlySpeed = 0.05f;

    /// <summary>Advances the player one tick. Returns false (and does nothing) while the ground is not loaded.</summary>
    public bool Tick(PlayerState s, PlayerControls c)
    {
        var bx = (int)Math.Floor(s.X);
        var bz = (int)Math.Floor(s.Z);
        if (!_world.IsLoaded(bx, bz)) return false; // vanilla freezes a player whose chunk is not there

        UpdateFluidState(s);
        s.OnClimbable = OnClimbable(s);

        // LivingEntity.aiStep
        if (s.NoJumpDelay > 0) s.NoJumpDelay--;
        if (Math.Abs(s.VelX) < 0.003) s.VelX = 0;
        if (Math.Abs(s.VelY) < 0.003) s.VelY = 0;
        if (Math.Abs(s.VelZ) < 0.003) s.VelZ = 0;

        // Sprinting stops without forward input, on a real wall, or in water when not swimming.
        if (s.Sprinting && (!c.Forward || c.Sneak || (s.HorizontalCollision && !s.MinorHorizontalCollision))) s.Sprinting = false;
        if (c.Sprint && c.Forward && !c.Sneak && !s.Sprinting) s.Sprinting = true;

        if (c.Jump && !Flying)
        {
            var fluidHeight = s.InLava ? s.LavaHeight : s.WaterHeight;
            var inWater = s.InWater && fluidHeight > 0;
            const double threshold = 0.4; // getFluidJumpThreshold: eye height >= 0.4
            if (!inWater || s.OnGround && !(fluidHeight > threshold))
            {
                if (!s.InLava || s.OnGround && !(fluidHeight > threshold))
                {
                    if ((s.OnGround || inWater && fluidHeight <= threshold) && s.NoJumpDelay == 0)
                    {
                        JumpFromGround(s);
                        s.NoJumpDelay = 10;
                    }
                }
                else s.VelY += 0.03999999910593033; // jumpInLiquid(LAVA)
            }
            else s.VelY += 0.03999999910593033; // jumpInLiquid(WATER)
        }
        else s.NoJumpDelay = 0;

        // LocalPlayer.modifyInput (1.21.4+): x 0.98, sneaking, then square-movement normalization.
        float strafe = (c.Left ? 1 : 0) - (c.Right ? 1 : 0);
        float forward = (c.Forward ? 1 : 0) - (c.Back ? 1 : 0);
        if (strafe != 0 || forward != 0)
        {
            strafe *= 0.98f;
            forward *= 0.98f;
            if (c.Sneak) { strafe *= (float)SneakingSpeed; forward *= (float)SneakingSpeed; }
            var len = MathF.Sqrt(strafe * strafe + forward * forward);
            if (len > 0)
            {
                float nx = strafe / len, ny = forward / len;
                float ax = Math.Abs(nx), ay = Math.Abs(ny);
                var r = ay > ax ? ax / ay : ay / ax;
                var f = Math.Min(len * MathF.Sqrt(1 + r * r), 1.0f);
                strafe = nx * f;
                forward = ny * f;
            }
        }

        s.InputStrafe = strafe;
        s.InputForward = forward;
        if (Flying)
        {
            // LocalPlayer.aiStep + the flying branch of Player.travel (client-side code: not in the server jar, so
            // not byte-verified; Paper does not movement-check creative/spectator players): sneak/jump move
            // down/up by 3x the fly speed, travel uses the fly speed, vertical velocity keeps 60% per tick.
            var vertical = (c.Jump ? 1 : 0) - (c.Sneak ? 1 : 0);
            if (vertical != 0) s.VelY += vertical * FlySpeed * 3.0f;
            var vyBefore = s.VelY;
            TravelInAir(s, strafe, forward, c);
            s.VelY = vyBefore * 0.6;
            s.FallDistance = 0;
            return true;
        }
        Travel(s, strafe, forward, c);
        return true;
    }

    private void JumpFromGround(PlayerState s)
    {
        var power = (float)(JumpStrength * BlockJumpFactor(s) + (JumpBoostAmplifier >= 0 ? 0.1f * (JumpBoostAmplifier + 1) : 0));
        if (power <= 1.0E-5f) return;
        s.VelY = Math.Max(power, s.VelY);
        if (s.Sprinting)
        {
            var yaw = s.Yaw * Mth.DegToRad;
            s.VelX += -Mth.Sin(yaw) * 0.2;
            s.VelZ += Mth.Cos(yaw) * 0.2;
        }
    }

    private void Travel(PlayerState s, float strafe, float forward, PlayerControls c)
    {
        if (s.InWater || s.InLava) TravelInFluid(s, strafe, forward, c);
        else TravelInAir(s, strafe, forward, c);
    }

    private void TravelInAir(PlayerState s, float strafe, float forward, PlayerControls c)
    {
        var below = BlockBelowThatAffectsMovement(s);
        var friction = s.OnGround ? Friction(below) : 1.0f;
        var inertia = friction * 0.91f;

        // handleRelativeFrictionAndCalculateMovement
        MoveRelative(s, FrictionInfluencedSpeed(s, friction), strafe, forward);
        HandleOnClimbable(s, c);
        Move(s, s.VelX, s.VelY, s.VelZ, c);
        if ((s.HorizontalCollision || c.Jump) && (s.OnClimbable || IsIn(s, _powderSnow))) s.VelY = 0.2;

        var vy = s.VelY;
        if (LevitationAmplifier >= 0) vy += (0.05 * (LevitationAmplifier + 1) - s.VelY) * 0.2;
        else if (_world.IsLoaded(below.X, below.Z)) vy -= EffectiveGravity(s);
        else vy = s.Y > _world.MinY ? -0.1 : 0;
        s.VelX *= inertia;
        s.VelY = vy * 0.98f;
        s.VelZ *= inertia;
    }

    private void TravelInFluid(PlayerState s, float strafe, float forward, PlayerControls c)
    {
        var falling = s.VelY <= 0;
        var y0 = s.Y;
        var gravity = EffectiveGravity(s);
        if (s.InWater)
        {
            var slowDown = s.Sprinting ? 0.9f : 0.8f; // Player.getWaterSlowDown 0.8
            var accel = 0.02f;
            var strider = (float)WaterMovementEfficiency;
            if (!s.OnGround) strider *= 0.5f;
            if (strider > 0)
            {
                slowDown += (0.54600006f - slowDown) * strider;
                accel += ((float)Speed(s) - accel) * strider;
            }
            if (DolphinsGrace) slowDown = 0.96f;
            MoveRelative(s, accel, strafe, forward);
            Move(s, s.VelX, s.VelY, s.VelZ, c);
            if (s.HorizontalCollision && s.OnClimbable) s.VelY = 0.2;
            s.VelX *= slowDown;
            s.VelY *= 0.800000011920929;
            s.VelZ *= slowDown;
            s.VelY = FluidFallingAdjusted(s, gravity, falling, s.VelY);
        }
        else
        {
            MoveRelative(s, 0.02f, strafe, forward);
            Move(s, s.VelX, s.VelY, s.VelZ, c);
            if (s.LavaHeight <= 0.4)
            {
                s.VelX *= 0.5;
                s.VelY *= 0.800000011920929;
                s.VelZ *= 0.5;
                s.VelY = FluidFallingAdjusted(s, gravity, falling, s.VelY);
            }
            else
            {
                s.VelX *= 0.5;
                s.VelY *= 0.5;
                s.VelZ *= 0.5;
            }
            if (gravity != 0) s.VelY += -gravity / 4.0;
        }
        // Climb out onto a ledge.
        if (s.HorizontalCollision && IsFree(s.Box.Move(s.VelX, s.VelY + 0.6 - s.Y + y0, s.VelZ))) s.VelY = 0.3;
    }

    private double FluidFallingAdjusted(PlayerState s, double gravity, bool falling, double vy)
    {
        if (gravity == 0 || s.Sprinting) return vy;
        if (falling && Math.Abs(vy - 0.005) >= 0.003 && Math.Abs(vy - gravity / 16.0) < 0.003) return -0.003;
        return vy - gravity / 16.0;
    }

    private double EffectiveGravity(PlayerState s) => s.VelY <= 0 && SlowFalling ? Math.Min(Gravity, 0.01) : Gravity;

    private double Speed(PlayerState s) => s.Sprinting ? MovementSpeed * 1.3 : MovementSpeed; // sprint: +30% multiply_total

    private float FrictionInfluencedSpeed(PlayerState s, float friction) =>
        s.OnGround ? (float)Speed(s) * (0.21600002f / (friction * friction * friction))
        : Flying ? FlySpeed * (s.Sprinting ? 2 : 1)
        : s.Sprinting ? 0.025999999f : 0.02f;

    /// <summary>Entity.moveRelative + getInputVector (Mth table trig, as vanilla).</summary>
    private static void MoveRelative(PlayerState s, float speed, float strafe, float forward)
    {
        double ix = strafe, iz = forward;
        var len2 = ix * ix + iz * iz;
        if (len2 < 1.0E-7) return;
        if (len2 > 1.0)
        {
            var len = Math.Sqrt(len2);
            ix /= len;
            iz /= len;
        }
        ix *= speed;
        iz *= speed;
        var sin = Mth.Sin(s.Yaw * Mth.DegToRad);
        var cos = Mth.Cos(s.Yaw * Mth.DegToRad);
        s.VelX += ix * cos - iz * sin;
        s.VelZ += iz * cos + ix * sin;
    }

    private void HandleOnClimbable(PlayerState s, PlayerControls c)
    {
        if (!s.OnClimbable) return;
        s.FallDistance = 0;
        s.VelX = Math.Clamp(s.VelX, -0.15000000596046448, 0.15000000596046448);
        s.VelZ = Math.Clamp(s.VelZ, -0.15000000596046448, 0.15000000596046448);
        s.VelY = Math.Max(s.VelY, -0.15000000596046448);
        if (s.VelY < 0 && !IsIn(s, _scaffolding) && c.Sneak) s.VelY = 0;
    }

    // ---------------------------------------------------------------- Entity.move

    private void Move(PlayerState s, double dx, double dy, double dz, PlayerControls c)
    {
        if (s.StuckX != 0 || s.StuckY != 0 || s.StuckZ != 0)
        {
            dx *= s.StuckX; dy *= s.StuckY; dz *= s.StuckZ;
            s.StuckX = s.StuckY = s.StuckZ = 0;
            s.VelX = s.VelY = s.VelZ = 0;
        }
        if (c.Sneak) (dx, dz) = BackOffFromEdge(s, dx, dy, dz);

        var (cx, cy, cz) = NoClip ? (dx, dy, dz) : Collide(s, dx, dy, dz);
        var moved2 = cx * cx + cy * cy + cz * cz;
        if (moved2 > 1.0E-7 || dx * dx + dy * dy + dz * dz - moved2 < 1.0E-7)
        {
            s.X += cx;
            s.Y += cy;
            s.Z += cz;
        }

        var xCol = !Mth.Equal(dx, cx);
        var zCol = !Mth.Equal(dz, cz);
        s.HorizontalCollision = xCol || zCol;
        s.VerticalCollision = dy != cy;
        var below = s.VerticalCollision && dy < 0;
        s.OnGround = below;
        s.SupportingBlock = below ? FindSupportingBlock(s) : null;
        s.MinorHorizontalCollision = s.HorizontalCollision && IsMinorCollision(s, cx, cz);

        if (s.HorizontalCollision)
        {
            if (xCol) s.VelX = 0;
            if (zCol) s.VelZ = 0;
        }
        if (dy != cy)
        {
            // Block.updateEntityMovementAfterFallOn: slime bounces, beds bounce a little, else stop.
            var on = s.SupportingBlock is { } sb ? _data.StateBlock[Math.Max(0, _world.GetState(sb.X, sb.Y, sb.Z))] : -1;
            if (on == _slime && !c.Sneak && s.VelY < 0) s.VelY = -s.VelY;
            else if (_bedLike.Contains(on) && !c.Sneak && s.VelY < 0) s.VelY = -s.VelY * 0.66f;
            else s.VelY = 0;
        }

        if (s.OnGround) s.FallDistance = 0;
        else if (cy < 0) s.FallDistance -= cy;

        CheckInsideBlocks(s);
        var speedFactor = BlockSpeedFactor(s);
        s.VelX *= speedFactor;
        s.VelZ *= speedFactor;
    }

    /// <summary>Entity.collide: collision with block shapes, then the 1.21 step-up (candidate heights).</summary>
    private (double, double, double) Collide(PlayerState s, double dx, double dy, double dz)
    {
        var box = s.Box;
        var colliders = new List<Aabb>();
        var collided = (dx * dx + dy * dy + dz * dz) == 0 ? (dx, dy, dz) : CollideWithShapes(dx, dy, dz, box, CollectColliders(box.ExpandTowards(dx, dy, dz), colliders));
        var (rx, ry, rz) = collided;
        var xCol = dx != rx;
        var yCol = dy != ry;
        var zCol = dz != rz;
        var onGroundish = s.OnGround || yCol && dy < 0;
        var step = (float)StepHeight;
        if (step > 0 && onGroundish && (xCol || zCol))
        {
            var box2 = yCol ? box.Move(0, ry, 0) : box;
            var stretched = box2.ExpandTowards(dx, step, dz);
            if (!yCol) stretched = stretched.ExpandTowards(0, -9.999999747378752E-6, 0);
            var shapes = CollectColliders(stretched, []);
            foreach (var h in CandidateStepHeights(box2, shapes, step, (float)ry))
            {
                var (sx, sy, sz) = CollideWithShapes(dx, h, dz, box2, shapes);
                if (sx * sx + sz * sz > rx * rx + rz * rz)
                {
                    var d = box.MinY - box2.MinY;
                    return (sx, sy - d, sz);
                }
            }
        }
        return collided;
    }

    private static float[] CandidateStepHeights(Aabb box, List<Aabb> shapes, float stepHeight, float currentY)
    {
        var set = new HashSet<float>();
        foreach (var shape in shapes)
        {
            // VoxelShape.getCoords(Y) of a single box: its min and max Y.
            foreach (var y in new[] { shape.MinY, shape.MaxY })
            {
                var h = (float)(y - box.MinY);
                if (h < 0 || h == currentY) continue;
                if (h > stepHeight) break;
                set.Add(h);
            }
        }
        var heights = set.ToArray();
        Array.Sort(heights);
        return heights;
    }

    /// <summary>Entity.collideWithShapes: Y first, then the larger horizontal axis first (axisStepOrder).</summary>
    private static (double, double, double) CollideWithShapes(double dx, double dy, double dz, Aabb box, List<Aabb> shapes)
    {
        if (shapes.Count == 0) return (dx, dy, dz);
        double rx = 0, ry = 0, rz = 0;
        if (dy != 0) ry = CollideAxis(1, box, shapes, dy);
        if (Math.Abs(dx) < Math.Abs(dz))
        {
            if (dz != 0) rz = CollideAxis(2, box.Move(rx, ry, 0), shapes, dz);
            if (dx != 0) rx = CollideAxis(0, box.Move(0, ry, rz), shapes, dx);
        }
        else
        {
            if (dx != 0) rx = CollideAxis(0, box.Move(0, ry, 0), shapes, dx);
            if (dz != 0) rz = CollideAxis(2, box.Move(rx, ry, 0), shapes, dz);
        }
        return (rx, ry, rz);
    }

    /// <summary>Shapes.collide along one axis: the furthest the box can move before touching any shape.</summary>
    private static double CollideAxis(int axis, Aabb box, List<Aabb> shapes, double d)
    {
        const double eps = 1.0E-7;
        foreach (var b in shapes)
        {
            if (Math.Abs(d) < eps) return 0;
            switch (axis)
            {
                case 0:
                    if (box.MaxY - eps <= b.MinY || box.MinY + eps >= b.MaxY || box.MaxZ - eps <= b.MinZ || box.MinZ + eps >= b.MaxZ) continue;
                    if (d > 0 && b.MinX >= box.MaxX - eps) d = Math.Min(d, b.MinX - box.MaxX);
                    else if (d < 0 && b.MaxX <= box.MinX + eps) d = Math.Max(d, b.MaxX - box.MinX);
                    break;
                case 1:
                    if (box.MaxX - eps <= b.MinX || box.MinX + eps >= b.MaxX || box.MaxZ - eps <= b.MinZ || box.MinZ + eps >= b.MaxZ) continue;
                    if (d > 0 && b.MinY >= box.MaxY - eps) d = Math.Min(d, b.MinY - box.MaxY);
                    else if (d < 0 && b.MaxY <= box.MinY + eps) d = Math.Max(d, b.MaxY - box.MinY);
                    break;
                default:
                    if (box.MaxX - eps <= b.MinX || box.MinX + eps >= b.MaxX || box.MaxY - eps <= b.MinY || box.MinY + eps >= b.MaxY) continue;
                    if (d > 0 && b.MinZ >= box.MaxZ - eps) d = Math.Min(d, b.MinZ - box.MaxZ);
                    else if (d < 0 && b.MaxZ <= box.MinZ + eps) d = Math.Max(d, b.MaxZ - box.MinZ);
                    break;
            }
        }
        return Math.Abs(d) < eps ? 0 : d;
    }

    /// <summary>The block collision boxes (world coordinates) of every block touching the area.</summary>
    private List<Aabb> CollectColliders(Aabb area, List<Aabb> into)
    {
        int x0 = (int)Math.Floor(area.MinX - 1.0E-7) - 1, x1 = (int)Math.Floor(area.MaxX + 1.0E-7) + 1;
        int y0 = (int)Math.Floor(area.MinY - 1.0E-7) - 1, y1 = (int)Math.Floor(area.MaxY + 1.0E-7) + 1;
        int z0 = (int)Math.Floor(area.MinZ - 1.0E-7) - 1, z1 = (int)Math.Floor(area.MaxZ + 1.0E-7) + 1;
        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
        {
            if (!_world.IsLoaded(x, z))
            {
                // Unloaded chunks are solid walls (vanilla stops entities at unloaded borders).
                into.Add(new Aabb(x, y0, z, x + 1, y1 + 1, z + 1));
                continue;
            }
            for (var y = y0; y <= y1; y++)
            {
                var state = _world.GetState(x, y, z);
                if (state <= 0) continue;
                foreach (var b in _data.CollisionBoxes(state))
                {
                    var wb = new Aabb(x + b.MinX, y + b.MinY, z + b.MinZ, x + b.MaxX, y + b.MaxY, z + b.MaxZ);
                    if (wb.MaxX >= area.MinX && wb.MinX <= area.MaxX && wb.MaxY >= area.MinY && wb.MinY <= area.MaxY && wb.MaxZ >= area.MinZ && wb.MinZ <= area.MaxZ)
                        into.Add(wb);
                }
            }
        }
        return into;
    }

    public bool IsFree(Aabb box)
    {
        foreach (var b in CollectColliders(box, [])) if (b.Intersects(box)) return false;
        return !HasFluid(box);
    }

    private bool HasFluid(Aabb box)
    {
        for (var x = (int)Math.Floor(box.MinX); x <= (int)Math.Floor(box.MaxX); x++)
        for (var y = (int)Math.Floor(box.MinY); y <= (int)Math.Floor(box.MaxY); y++)
        for (var z = (int)Math.Floor(box.MinZ); z <= (int)Math.Floor(box.MaxZ); z++)
        {
            var st = _world.GetState(x, y, z);
            if (st > 0 && (_data.StateFlags[st] & (GameData.FlagWater | GameData.FlagLava)) != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Player.maybeBackOffFromEdge: a sneaking player on the ground never moves where it could fall further than
    /// its step height; the move shrinks in 0.05 steps per axis until it would not.
    /// </summary>
    private (double, double) BackOffFromEdge(PlayerState s, double dx, double dy, double dz)
    {
        var f = StepHeight;
        if (dy > 0 || !(s.OnGround || s.FallDistance < f && !CanFallAtLeast(s, 0, 0, f - s.FallDistance))) return (dx, dz);
        var sx = Math.Sign(dx) * 0.05;
        var sz = Math.Sign(dz) * 0.05;
        while (dx != 0 && CanFallAtLeast(s, dx, 0, f))
        {
            if (Math.Abs(dx) <= 0.05) { dx = 0; break; }
            dx -= sx;
        }
        while (dz != 0 && CanFallAtLeast(s, 0, dz, f))
        {
            if (Math.Abs(dz) <= 0.05) { dz = 0; break; }
            dz -= sz;
        }
        while (dx != 0 && dz != 0 && CanFallAtLeast(s, dx, dz, f))
        {
            dx = Math.Abs(dx) <= 0.05 ? 0 : dx - sx;
            dz = Math.Abs(dz) <= 0.05 ? 0 : dz - sz;
        }
        return (dx, dz);
    }

    /// <summary>Entity.canFallAtLeast: no block collision in the slab of height f just below the moved box.</summary>
    private bool CanFallAtLeast(PlayerState s, double dx, double dz, double f)
    {
        var b = s.Box;
        var area = new Aabb(b.MinX + 1.0E-7 + dx, b.MinY - f - 1.0E-7, b.MinZ + 1.0E-7 + dz, b.MaxX - 1.0E-7 + dx, b.MinY, b.MaxZ - 1.0E-7 + dz);
        foreach (var c in CollectColliders(area, [])) if (c.Intersects(area)) return false;
        return true;
    }

    /// <summary>
    /// LocalPlayer.isHorizontalCollisionMinor: the angle between the input direction and the actual (collided)
    /// movement is under 8 degrees (0.13962634 rad), i.e. the wall only grazed us.
    /// </summary>
    private static bool IsMinorCollision(PlayerState s, double mx, double mz)
    {
        var yaw = s.Yaw * Mth.DegToRad;
        double sin = Mth.Sin(yaw), cos = Mth.Cos(yaw);
        var ix = s.InputStrafe * cos - s.InputForward * sin;
        var iz = s.InputForward * cos + s.InputStrafe * sin;
        var inputLen2 = ix * ix + iz * iz;
        var moveLen2 = mx * mx + mz * mz;
        if (inputLen2 < 1.0E-5f || moveLen2 < 1.0E-5f) return false;
        var angle = Math.Acos((ix * mx + iz * mz) / Math.Sqrt(inputLen2 * moveLen2));
        return angle < 0.13962634f;
    }

    private (int X, int Y, int Z)? FindSupportingBlock(PlayerState s)
    {
        var box = s.Box;
        var under = new Aabb(box.MinX, box.MinY - 1.0E-6, box.MinZ, box.MaxX, box.MinY, box.MaxZ);
        (int, int, int)? best = null;
        var bestD = double.MaxValue;
        for (var x = (int)Math.Floor(under.MinX); x <= (int)Math.Floor(under.MaxX); x++)
        for (var y = (int)Math.Floor(under.MinY); y <= (int)Math.Floor(under.MaxY); y++)
        for (var z = (int)Math.Floor(under.MinZ); z <= (int)Math.Floor(under.MaxZ); z++)
        {
            var st = _world.GetState(x, y, z);
            if (st <= 0) continue;
            var hit = false;
            foreach (var b in _data.CollisionBoxes(st))
                if (new Aabb(x + b.MinX, y + b.MinY, z + b.MinZ, x + b.MaxX, y + b.MaxY, z + b.MaxZ).Intersects(under)) { hit = true; break; }
            if (!hit) continue;
            double ex = x + 0.5 - s.X, ey = y + 0.5 - s.Y, ez = z + 0.5 - s.Z;
            var d = ex * ex + ey * ey + ez * ez;
            if (d < bestD) { bestD = d; best = (x, y, z); }
        }
        return best;
    }

    /// <summary>getBlockPosBelowThatAffectsMyMovement = getOnPos(0.500001).</summary>
    private (int X, int Y, int Z) BlockBelowThatAffectsMovement(PlayerState s)
    {
        var y = (int)Math.Floor(s.Y - 0.5000010132789612);
        if (s.SupportingBlock is { } sb) return (sb.X, y, sb.Z);
        return ((int)Math.Floor(s.X), y, (int)Math.Floor(s.Z));
    }

    private float Friction((int X, int Y, int Z) p)
    {
        var st = _world.GetState(p.X, p.Y, p.Z);
        return st < 0 ? 0.6f : _data.Friction[st];
    }

    private float BlockSpeedFactor(PlayerState s)
    {
        var st = _world.GetState((int)Math.Floor(s.X), (int)Math.Floor(s.Y), (int)Math.Floor(s.Z));
        if (st < 0) return 1;
        var block = _data.StateBlock[st];
        if (block == _data.Block("water")!.Id || block == _bubbleColumn) return 1;
        var f = _data.SpeedFactor[st];
        if (f != 1.0f) return f;
        var b = BlockBelowThatAffectsMovement(s);
        var below = _world.GetState(b.X, b.Y, b.Z);
        return below < 0 ? 1 : _data.SpeedFactor[below];
    }

    private float BlockJumpFactor(PlayerState s)
    {
        var st = _world.GetState((int)Math.Floor(s.X), (int)Math.Floor(s.Y), (int)Math.Floor(s.Z));
        var f = st < 0 ? 1 : _data.JumpFactor[st];
        if (f != 1.0f) return f;
        var b = BlockBelowThatAffectsMovement(s);
        var below = _world.GetState(b.X, b.Y, b.Z);
        return below < 0 ? 1 : _data.JumpFactor[below];
    }

    private bool OnClimbable(PlayerState s)
    {
        var st = _world.GetState((int)Math.Floor(s.X), (int)Math.Floor(s.Y), (int)Math.Floor(s.Z));
        if (st <= 0) return false;
        var block = _data.StateBlock[st];
        if (_climbable.Contains(block)) return true;
        // An open trapdoor directly above a ladder facing the same way climbs like the ladder.
        if (_data.BlockOf(st).Name.EndsWith("_trapdoor") && _data.GetProperty(st, "open") == "true")
        {
            var below = _world.GetState((int)Math.Floor(s.X), (int)Math.Floor(s.Y) - 1, (int)Math.Floor(s.Z));
            return below > 0 && _data.StateBlock[below] == _ladder && _data.GetProperty(below, "facing") == _data.GetProperty(st, "facing");
        }
        return false;
    }

    private bool IsIn(PlayerState s, int blockId)
    {
        var box = s.Box.Inflate(-1.0E-7, -1.0E-7, -1.0E-7);
        for (var x = (int)Math.Floor(box.MinX); x <= (int)Math.Floor(box.MaxX); x++)
        for (var y = (int)Math.Floor(box.MinY); y <= (int)Math.Floor(box.MaxY); y++)
        for (var z = (int)Math.Floor(box.MinZ); z <= (int)Math.Floor(box.MaxZ); z++)
        {
            var st = _world.GetState(x, y, z);
            if (st > 0 && _data.StateBlock[st] == blockId) return true;
        }
        return false;
    }

    /// <summary>Entity.checkInsideBlocks: cobwebs, berry bushes and powder snow slow the next move.</summary>
    private void CheckInsideBlocks(PlayerState s)
    {
        var box = s.Box.Inflate(-1.0E-7, -1.0E-7, -1.0E-7);
        for (var x = (int)Math.Floor(box.MinX); x <= (int)Math.Floor(box.MaxX); x++)
        for (var y = (int)Math.Floor(box.MinY); y <= (int)Math.Floor(box.MaxY); y++)
        for (var z = (int)Math.Floor(box.MinZ); z <= (int)Math.Floor(box.MaxZ); z++)
        {
            var st = _world.GetState(x, y, z);
            if (st <= 0) continue;
            var block = _data.StateBlock[st];
            if (block == _cobweb) (s.StuckX, s.StuckY, s.StuckZ) = (0.25, 0.05000000074505806, 0.25);
            else if (block == _sweetBerry) (s.StuckX, s.StuckY, s.StuckZ) = (0.800000011920929, 0.75, 0.800000011920929);
            else if (block == _powderSnow) (s.StuckX, s.StuckY, s.StuckZ) = (0.8999999761581421, 1.5, 0.8999999761581421);
        }
    }

    /// <summary>Entity.updateFluidHeightAndDoFluidPushing for water and lava (heights; flow pushing omitted).</summary>
    private void UpdateFluidState(PlayerState s)
    {
        var box = s.Box.Inflate(-0.001, -0.001, -0.001);
        double water = 0, lava = 0;
        for (var x = (int)Math.Floor(box.MinX); x < (int)Math.Ceiling(box.MaxX); x++)
        for (var y = (int)Math.Floor(box.MinY); y < (int)Math.Ceiling(box.MaxY); y++)
        for (var z = (int)Math.Floor(box.MinZ); z < (int)Math.Ceiling(box.MaxZ); z++)
        {
            var st = _world.GetState(x, y, z);
            if (st <= 0) continue;
            var flags = _data.StateFlags[st];
            if ((flags & (GameData.FlagWater | GameData.FlagLava)) == 0) continue;
            var height = y + FluidHeight(x, y, z, st);
            if (height < box.MinY) continue;
            var h = height - box.MinY;
            if ((flags & GameData.FlagWater) != 0) water = Math.Max(water, h);
            else lava = Math.Max(lava, h);
        }
        s.WaterHeight = water;
        s.LavaHeight = lava;
        s.InWater = water > 0;
        s.InLava = lava > 0;
        if (s.InWater) s.FallDistance = 0;
    }

    /// <summary>FluidState.getHeight: a full block when the same fluid is above, else amount/9.</summary>
    private float FluidHeight(int x, int y, int z, int state)
    {
        var above = _world.GetState(x, y + 1, z);
        var mask = _data.StateFlags[state] & (GameData.FlagWater | GameData.FlagLava);
        if (above > 0 && (_data.StateFlags[above] & mask) != 0) return 1f;
        return _data.FluidAmount[state] / 9f;
    }
}
