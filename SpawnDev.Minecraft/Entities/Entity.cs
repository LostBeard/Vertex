using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Items;

namespace SpawnDev.Minecraft.Entities;

/// <summary>An entity the server is showing us: a mob, another player, a dropped item, an arrow...</summary>
public sealed class Entity
{
    public Entity(int id, Guid uuid, EntityTypeInfo type)
    {
        Id = id;
        Uuid = uuid;
        Type = type;
    }

    public int Id { get; }
    public Guid Uuid { get; }
    public EntityTypeInfo Type { get; }
    public string Name => Type.Name;

    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Z { get; internal set; }
    public float Yaw { get; internal set; }
    public float Pitch { get; internal set; }
    public float HeadYaw { get; internal set; }
    public double VelocityX { get; internal set; }
    public double VelocityY { get; internal set; }
    public double VelocityZ { get; internal set; }
    public bool OnGround { get; internal set; }
    /// <summary>Spawn data (e.g. the shooter of a projectile, the facing of an item frame).</summary>
    public int Data { get; internal set; }

    /// <summary>Raw metadata values by index, as decoded from set_entity_data.</summary>
    public Dictionary<int, object?> Metadata { get; } = [];

    /// <summary>For a dropped item (minecraft:item) the stack it carries (metadata index 8).</summary>
    public ItemStack? Item { get; internal set; }

    /// <summary>For living entities, health from metadata index 9 when the server sends it.</summary>
    public float? Health { get; internal set; }

    /// <summary>The equipment slots the server has shown (0 main hand, 1 off hand, 2 feet .. 5 head, 6 body).</summary>
    public Dictionary<int, ItemStack> Equipment { get; } = [];

    /// <summary>For other players: their name from the player list, when known.</summary>
    public string? PlayerName { get; internal set; }

    public bool IsPlayer => Type.Name == "minecraft:player";
    public bool IsItem => Type.Name == "minecraft:item";

    public double DistanceTo(double x, double y, double z)
    {
        double dx = X - x, dy = Y - y, dz = Z - z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public override string ToString() => $"{PlayerName ?? Name} #{Id} at {X:0.#} {Y:0.#} {Z:0.#}";
}

/// <summary>net.minecraft.world.entity.Pose, in declaration order (the ordinal is what the protocol sends).</summary>
public enum EntityPose
{
    Standing, FallFlying, Sleeping, Swimming, SpinAttack, Crouching, LongJumping, Dying, Croaking, UsingTongue,
    Sitting, Roaring, Sniffing, Emerging, Digging, Sliding, Shooting, Inhaling,
}
