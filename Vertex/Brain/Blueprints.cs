using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Items;

namespace Vertex.Brain;

/// <summary>Things Vertex knows how to build, as ordered block plans (each block has something to click against).</summary>
public static class Blueprints
{
    /// <summary>Blocks a house can be made of, best looking first (any planks count as one kind).</summary>
    public static readonly string[] HouseMaterials =
    [
        "planks", "minecraft:cobblestone", "minecraft:stone_bricks", "minecraft:bricks", "minecraft:stone", "minecraft:cobbled_deepslate",
        "minecraft:sandstone", "minecraft:mud_bricks", "minecraft:deepslate_bricks", "minecraft:andesite", "minecraft:dirt",
    ];

    public static Func<ItemStack, bool> MaterialMatch(string material) =>
        material == "planks" ? s => s.Name.EndsWith("_planks") : s => s.Name == material;

    /// <summary>Wall and roof blocks for a house of an outer size (5 or 7), with or without glass in the 3 windows.</summary>
    public static int HouseBlocks(int size, bool glass) => 4 * (size - 1) * 3 - 2 - (glass ? 3 : 0) + size * size;

    public static bool IsGlass(ItemStack s) => s.Name is "minecraft:glass" || s.Name.EndsWith("_stained_glass");
    public static bool IsDoor(ItemStack s) => s.Name.EndsWith("_door") && s.Name != "minecraft:iron_door";

    /// <summary>
    /// A small house, built from inside: standing at (cx, y, cz) every block is within reach. Walls three high
    /// (ring at distance size/2), a doorway on `doorSide` with a door if we carry one, glass windows on the other
    /// three sides if we carry glass, a flat roof (edges first: each roof block leans on the wall or the one
    /// before it), and a torch inside so no monsters spawn in it.
    /// </summary>
    public static List<Placement> House(int cx, int y, int cz, int size, BlockFace doorSide, Func<ItemStack, bool> wall, bool glass, bool torch, bool door)
    {
        var r = size / 2;
        var (ddx, ddz) = Dir(doorSide);
        var doorway = (X: cx + ddx * r, Z: cz + ddz * r);
        var windows = new[] { BlockFace.North, BlockFace.South, BlockFace.East, BlockFace.West }.Where(f => f != doorSide)
            .Select(f => { var (dx, dz) = Dir(f); return (X: cx + dx * r, Z: cz + dz * r); }).ToHashSet();

        var plan = new List<Placement>();
        // Walls, layer by layer, around the ring.
        for (var dy = 0; dy < 3; dy++)
            foreach (var (x, z) in Ring(cx, cz, r))
            {
                if ((x, z) == doorway && dy < 2) continue;
                if (glass && dy == 1 && windows.Contains((x, z))) { plan.Add(new Placement(x, y + dy, z, IsGlass, "glass")); continue; }
                plan.Add(new Placement(x, y + dy, z, wall, "wall blocks"));
            }
        // Roof: from the edge in, ring by ring.
        for (var ring = r; ring >= 0; ring--)
            foreach (var (x, z) in ring == 0 ? new[] { (cx, cz) } : Ring(cx, cz, ring))
                plan.Add(new Placement(x, y + 3, z, wall, "roof blocks"));
        // A torch on the floor in an inside corner, then the door (placed from inside, facing out).
        if (torch) plan.Add(new Placement(cx - (r - 1), y, cz - (r - 1), s => s.Name == "minecraft:torch", "a torch"));
        if (door) plan.Add(new Placement(doorway.X, y, doorway.Z, IsDoor, "a door", doorSide));
        return plan;
    }

    public static (int Dx, int Dz) Dir(BlockFace f) => f switch
    {
        BlockFace.North => (0, -1), BlockFace.South => (0, 1), BlockFace.West => (-1, 0), _ => (1, 0),
    };

    /// <summary>The square ring of cells at Chebyshev distance r, walking round it.</summary>
    private static IEnumerable<(int X, int Z)> Ring(int cx, int cz, int r)
    {
        for (var x = -r; x < r; x++) yield return (cx + x, cz - r);
        for (var z = -r; z < r; z++) yield return (cx + r, cz + z);
        for (var x = r; x > -r; x--) yield return (cx + x, cz + r);
        for (var z = r; z > -r; z--) yield return (cx - r, cz + z);
    }
}
