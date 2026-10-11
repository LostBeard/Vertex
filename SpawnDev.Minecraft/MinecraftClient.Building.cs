using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Items;
using SpawnDev.Minecraft.Pathing;

namespace SpawnDev.Minecraft;

/// <summary>One block of a build plan: what goes where. Facing: look that way first (doors, stairs face the player).</summary>
public sealed record Placement(int X, int Y, int Z, Func<ItemStack, bool> Item, string Description, BlockFace? Facing = null);

// Building from a plan, block by block, with real placement clicks.
public sealed partial class MinecraftClient
{
    /// <summary>
    /// Places a plan's blocks in order (each needs something already placed or standing to click against), walking
    /// within reach when needed and never into its own body. Cells already holding something solid are left alone
    /// (they are part of the build already, or a tree we build around). Stops when a material runs out.
    /// </summary>
    public async Task<(int Placed, int Failed, string? Missing)> BuildAsync(IReadOnlyList<Placement> plan, CancellationToken cancellationToken = default)
    {
        var placed = 0;
        var failed = 0;
        foreach (var p in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var st = World.GetState(p.X, p.Y, p.Z);
            if (st < 0) { failed++; continue; }
            if (!World.Data.IsAir(st) && (World.Data.StateFlags[st] & GameData.FlagReplaceable) == 0) continue;
            if (!Inventory.Find(p.Item).Any()) return (placed, failed, p.Description);

            if (DistanceToBlock(p.X, p.Y, p.Z) > BlockReach - 0.3 || BodyOverlaps(p.X, p.Y, p.Z))
            {
                Goal goal = BodyOverlaps(p.X, p.Y, p.Z) ? Goal.AwayFrom(p.X + 0.5, p.Z + 0.5, 1.5) : new GoalReachBlock(p.X, p.Y, p.Z, BlockReach - 0.8);
                await GoToAsync(goal, new PathOptions { MaxNodes = 10_000 }, cancellationToken);
                if (BodyOverlaps(p.X, p.Y, p.Z)) { failed++; Log($"build: could not step out of {p.X} {p.Y} {p.Z}"); continue; }
            }
            if (!await EquipAsync(p.Item, cancellationToken)) return (placed, failed, p.Description);
            if (p.Facing is { } facing)
            {
                // The server takes a placed door's facing from our rotation as it last heard it: turn, let it hear.
                Look(facing switch { BlockFace.North => 180, BlockFace.South => 0, BlockFace.West => 90, _ => -90 }, 30);
                await WaitTicksAsync(2, cancellationToken);
            }
            if (await PlaceBlockAsync(p.X, p.Y, p.Z, cancellationToken)) placed++;
            else
            {
                failed++;
                Log($"build: could not place {p.Description} at {p.X} {p.Y} {p.Z} (there: {World.GetBlock(p.X, p.Y, p.Z)}, from {Position.X:0.##} {Position.Y:0.##} {Position.Z:0.##}, holding {Inventory.MainHand})");
            }
        }
        return (placed, failed, null);
    }

    /// <summary>Whether our body (0.6 wide, 1.8 tall) overlaps block x,y,z (we cannot place a block there).</summary>
    public bool BodyOverlaps(int x, int y, int z)
    {
        var (px, py, pz) = Position;
        return px + 0.3 > x && px - 0.3 < x + 1 && pz + 0.3 > z && pz - 0.3 < z + 1 && py + 1.8 > y && py < y + 1;
    }
}
