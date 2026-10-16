namespace Vertex.Brain;

/// <summary>
/// Where to point a bow. A fully drawn arrow leaves at 3 blocks per tick; each tick it moves, loses 1% of its speed, then drops by 0.05 (vanilla AbstractArrow). The aim is the pitch whose flight
/// is at the target's height when it has covered the target's distance, found by search; a target that is moving is led by where it will be when the arrow arrives.
/// </summary>
public static class Ballistics
{
    /// <summary>Speed of a fully drawn bow's arrow, blocks per tick.</summary>
    public const double FullDrawSpeed = 3.0;

    private const double Gravity = 0.05, Drag = 0.99;

    /// <summary>
    /// Flies an arrow shot with the given pitch (degrees, positive = up) and returns how far above (+) or below (-) a target at horizontal distance <paramref name="distance"/> it passes, and
    /// the ticks it took; null when it hits the ground first or never gets that far (it is out of range).
    /// </summary>
    public static (double HeightError, int Ticks)? Fly(double pitchDegrees, double distance, double targetHeight, double speed = FullDrawSpeed)
    {
        var rad = pitchDegrees * Math.PI / 180;
        double vx = Math.Cos(rad) * speed, vy = Math.Sin(rad) * speed, x = 0, y = 0;
        for (var tick = 1; tick <= 600; tick++)
        {
            var (px, py) = (x, y);
            x += vx;
            y += vy;
            vx *= Drag;
            vy = vy * Drag - Gravity;
            if (x >= distance)
            {
                var f = x - px < 1e-9 ? 1 : (distance - px) / (x - px);
                return (py + (y - py) * f - targetHeight, tick);
            }
            if (vx < 0.01) return null;
        }
        return null;
    }

    /// <summary>
    /// The pitch (degrees, positive = up) to hit a target at horizontal distance <paramref name="distance"/> and height <paramref name="targetHeight"/> relative to the arrow's start, or null
    /// when it is out of range. Of the two pitches that reach it (a flat shot and a lob) the flat one: it arrives sooner, so the target has less time to move.
    /// </summary>
    public static (double Pitch, int Ticks)? Aim(double distance, double targetHeight, double speed = FullDrawSpeed)
    {
        (double Pitch, double Error, int Ticks)? best = null;
        for (var pitch = -30.0; pitch <= 60.0; pitch += 0.25)
        {
            if (Fly(pitch, distance, targetHeight, speed) is not { } hit) continue;
            if (best == null || Math.Abs(hit.HeightError) < Math.Abs(best.Value.Error) - 1e-9) best = (pitch, hit.HeightError, hit.Ticks);
        }
        if (best == null || Math.Abs(best.Value.Error) > 0.35) return null;       // within a third of a block, or it is out of range
        // Refine around the best step.
        double lo = best.Value.Pitch - 0.25, hi = best.Value.Pitch + 0.25;
        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2;
            if (Fly(mid, distance, targetHeight, speed) is not { } m) { lo = mid; continue; }
            // Pitching up raises the arrow at the target: a positive error means too high.
            if (m.HeightError > 0) hi = mid; else lo = mid;
        }
        var pitchOut = (lo + hi) / 2;
        var final = Fly(pitchOut, distance, targetHeight, speed);
        return final == null ? null : (pitchOut, final.Value.Ticks);
    }

    /// <summary>
    /// Yaw and pitch (vanilla: yaw 0 looks south, 90 west; pitch negative = up) from the shooter's eye to a target, leading a moving target by its velocity (blocks per tick). Null when out of range.
    /// </summary>
    public static (double Yaw, double Pitch, int Ticks)? AimAt(double eyeX, double eyeY, double eyeZ, double targetX, double targetY, double targetZ, double velX = 0, double velY = 0, double velZ = 0)
    {
        double tx = targetX, ty = targetY, tz = targetZ;
        (double Pitch, int Ticks)? aim = null;
        for (var iteration = 0; iteration < 4; iteration++)
        {
            double dx = tx - eyeX, dz = tz - eyeZ, horizontal = Math.Sqrt(dx * dx + dz * dz);
            aim = Aim(horizontal, ty - eyeY);
            if (aim == null) return null;
            // Where the target will be when the arrow gets there (vertical motion is ignored: mobs mostly walk).
            tx = targetX + velX * aim.Value.Ticks;
            tz = targetZ + velZ * aim.Value.Ticks;
        }
        var yaw = Math.Atan2(-(tx - eyeX), tz - eyeZ) * 180 / Math.PI;
        return (yaw, -aim!.Value.Pitch, aim.Value.Ticks);
    }
}
