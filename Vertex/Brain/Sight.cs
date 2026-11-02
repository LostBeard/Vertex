using SpawnDev.Minecraft.World;

namespace Vertex.Brain;

/// <summary>
/// Could a player standing here SEE that? The server sends a client every block of every loaded chunk, buried ore and all, and every animal it tracks, walls or no walls. Reading that
/// straight out and telling friends "I found gold" is an X-ray (live on AubsCraft, 2026-11-01: "found gold, 7 wolfs" after a walk across the surface). Something counts as seen only if a
/// straight line from the eyes reaches it without passing through a solid block. Glass, leaves and ice let the line through; anything not a full cube (fences, plants, water) does too.
/// </summary>
public static class Sight
{
    private const double Step = 0.2;

    /// <summary>True if the line from the eyes to this block reaches one of its faces through open space.</summary>
    public static bool CanSeeBlock(GameWorld world, (double X, double Y, double Z) eye, int x, int y, int z)
    {
        // Seven sample points: just outside each face. A block is seen if any face is in the open and has a clear line.
        const double o = 0.5 + 0.02;
        ReadOnlySpan<(double, double, double)> faces = [(o, 0, 0), (-o, 0, 0), (0, o, 0), (0, -o, 0), (0, 0, o), (0, 0, -o)];
        foreach (var (fx, fy, fz) in faces)
            if (Clear(world, eye, x + 0.5 + fx, y + 0.5 + fy, z + 0.5 + fz)) return true;
        return false;
    }

    /// <summary>True if the line from the eyes reaches the body of a creature standing at (x, y, z) with the given height.</summary>
    public static bool CanSeeBody(GameWorld world, (double X, double Y, double Z) eye, double x, double y, double z, double height = 1.0)
        => Clear(world, eye, x, y + height * 0.5, z) || Clear(world, eye, x, y + height * 0.9, z) || Clear(world, eye, x, y + height * 0.15, z);

    /// <summary>True if no sight-blocking block lies on the straight line from `eye` to the point (the point's own cell included).</summary>
    public static bool Clear(GameWorld world, (double X, double Y, double Z) eye, double tx, double ty, double tz)
    {
        double dx = tx - eye.X, dy = ty - eye.Y, dz = tz - eye.Z;
        var len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var steps = Math.Max(1, (int)Math.Ceiling(len / Step));
        int lx = int.MinValue, ly = 0, lz = 0;
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            int cx = (int)Math.Floor(eye.X + dx * t), cy = (int)Math.Floor(eye.Y + dy * t), cz = (int)Math.Floor(eye.Z + dz * t);
            if (cx == lx && cy == ly && cz == lz) continue;
            (lx, ly, lz) = (cx, cy, cz);
            if (Blocks(world, cx, cy, cz)) return false;
        }
        return true;
    }

    /// <summary>Does this cell stop the eye? Unloaded ground does: we cannot say what is in it, so nothing behind it was seen.</summary>
    private static bool Blocks(GameWorld world, int x, int y, int z)
    {
        var state = world.GetState(x, y, z);
        if (state < 0) return true;
        if (state == 0) return false;
        var data = world.Data;
        var boxes = data.CollisionBoxes(state);
        if (boxes.Length != 1) return false;
        var b = boxes[0];
        if (b.MinX > 0.001 || b.MinY > 0.001 || b.MinZ > 0.001 || b.MaxX < 0.999 || b.MaxY < 0.999 || b.MaxZ < 0.999) return false; // not a full cube
        return !SeeThrough(data.BlockOf(state).Name);
    }

    private static bool SeeThrough(string name)
        => name.Contains("glass") || name.Contains("leaves") || name is "minecraft:ice" or "minecraft:barrier" or "minecraft:slime_block" or "minecraft:honey_block"
            or "minecraft:spawner" or "minecraft:iron_bars";
}
