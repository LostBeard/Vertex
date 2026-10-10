namespace SpawnDev.Minecraft.Physics;

/// <summary>An axis-aligned bounding box in world coordinates (vanilla AABB).</summary>
public readonly record struct Aabb(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    public Aabb Move(double dx, double dy, double dz) => new(MinX + dx, MinY + dy, MinZ + dz, MaxX + dx, MaxY + dy, MaxZ + dz);

    /// <summary>Grows the box in the direction of a movement (vanilla expandTowards).</summary>
    public Aabb ExpandTowards(double dx, double dy, double dz) => new(
        dx < 0 ? MinX + dx : MinX, dy < 0 ? MinY + dy : MinY, dz < 0 ? MinZ + dz : MinZ,
        dx > 0 ? MaxX + dx : MaxX, dy > 0 ? MaxY + dy : MaxY, dz > 0 ? MaxZ + dz : MaxZ);

    public Aabb Inflate(double x, double y, double z) => new(MinX - x, MinY - y, MinZ - z, MaxX + x, MaxY + y, MaxZ + z);

    public bool Intersects(in Aabb o) => MinX < o.MaxX && MaxX > o.MinX && MinY < o.MaxY && MaxY > o.MinY && MinZ < o.MaxZ && MaxZ > o.MinZ;

    /// <summary>A box of a given width/height standing at feet position x,y,z.</summary>
    public static Aabb AtFeet(double x, double y, double z, double width, double height)
    {
        var h = width / 2;
        return new Aabb(x - h, y, z - h, x + h, y + height, z + h);
    }
}
