using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Items;
using SpawnDev.Minecraft.Pathing;

namespace SpawnDev.Minecraft;

// Getting places: plan with Pathfinder, then steer the body there tick by tick.
public sealed partial class MinecraftClient
{
    private Pathfinder? _pathfinder;

    public Pathfinder Pathfinder => _pathfinder ??= new Pathfinder(World);

    /// <summary>Raised with each new plan (for logging/visualising).</summary>
    public event Action<IReadOnlyList<PathStep>>? PathPlanned;

    /// <summary>The block the feet are in.</summary>
    public (int X, int Y, int Z) FeetBlock
    {
        get
        {
            var (x, y, z) = Position;
            // Standing on a slab puts the feet at y.5, still in that block; on a full block at exactly y.0.
            return ((int)Math.Floor(x), (int)Math.Floor(y + 1e-6), (int)Math.Floor(z));
        }
    }

    /// <summary>Names of blocks Vertex is happy to use as scaffolding (pillar, bridge).</summary>
    public HashSet<string> ThrowawayBlocks { get; } =
    [
        "minecraft:dirt", "minecraft:cobblestone", "minecraft:cobbled_deepslate", "minecraft:netherrack",
        "minecraft:stone", "minecraft:andesite", "minecraft:diorite", "minecraft:granite", "minecraft:tuff",
        "minecraft:oak_planks", "minecraft:spruce_planks", "minecraft:birch_planks", "minecraft:sand", "minecraft:gravel",
    ];

    /// <summary>
    /// Walks (digs, pillars, swims, climbs) to a goal. Replans when stuck, corrected by the server, or when a plan
    /// only got part of the way. Returns true when the goal is reached.
    /// </summary>
    public async Task<bool> GoToAsync(Goal goal, PathOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PathOptions();
        options.DigTicks ??= BestDigTicks;
        try
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var feet = FeetBlock;
                if (goal.IsReached(feet.X, feet.Y, feet.Z)) return true;
                var (steps, complete) = Pathfinder.FindPath(feet, goal, options);
                PathPlanned?.Invoke(steps);
                if (steps.Count == 0)
                {
                    Log($"no path from {feet} ({(complete ? "already there" : "nothing reachable closer")})");
                    return goal.IsReached(feet.X, feet.Y, feet.Z);
                }
                var followed = await FollowAsync(steps, cancellationToken);
                feet = FeetBlock;
                if (goal.IsReached(feet.X, feet.Y, feet.Z)) return true;
                if (!followed) Log("path interrupted, replanning");
                else if (!complete) Log("partial path done, replanning");
            }
            var end = FeetBlock;
            return goal.IsReached(end.X, end.Y, end.Z);
        }
        finally
        {
            Controls.Clear();
        }
    }

    private int? BestDigTicks(int state)
    {
        if (World.Data.Hardness[state] < 0) return null;
        // The best tool we carry (Vertex swaps to it before digging).
        var best = TicksToBreak(state);
        foreach (var slot in Inventory.Find(s => s.Type?.ToolRules.Length > 0))
        {
            var t = TicksToBreakWith(Inventory[slot], state);
            if (t < best) best = t;
        }
        return best == int.MaxValue ? null : best;
    }

    private int TicksToBreakWith(ItemStack tool, int state)
    {
        var hardness = World.Data.Hardness[state];
        if (hardness < 0) return int.MaxValue;
        if (hardness == 0) return 0;
        var speed = ToolSpeed(tool, state);
        var correct = (World.Data.StateFlags[state] & GameData.FlagNeedsTool) == 0 || IsCorrectTool(tool, state);
        var p = speed / hardness / (correct ? 30 : 100);
        return p >= 1 ? 0 : (int)Math.Ceiling(1 / p);
    }

    /// <summary>Holds the tool that breaks this block fastest (and drops it, when possible).</summary>
    public async Task EquipBestToolAsync(int state, CancellationToken cancellationToken = default)
    {
        var bestSlot = -1;
        var best = TicksToBreakWith(ItemStack.Empty, state);
        var harvest = CanHarvestWith(ItemStack.Empty, state);
        foreach (var slot in Inventory.Find(s => s.Type?.ToolRules.Length > 0))
        {
            var stack = Inventory[slot];
            var t = TicksToBreakWith(stack, state);
            var h = CanHarvestWith(stack, state);
            if ((h && !harvest) || (h == harvest && t < best)) { best = t; bestSlot = slot; harvest = h; }
        }
        if (bestSlot >= 0) await EquipAsync(s => ReferenceEquals(s, Inventory[bestSlot]) || s == Inventory[bestSlot], cancellationToken);
    }

    private bool CanHarvestWith(ItemStack tool, int state) =>
        (World.Data.StateFlags[state] & GameData.FlagNeedsTool) == 0 || IsCorrectTool(tool, state);

    private async Task<bool> FollowAsync(List<PathStep> steps, CancellationToken ct)
    {
        var startTeleports = TeleportCount;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var last = i == steps.Count - 1;

            foreach (var d in step.Dig)
            {
                var st = World.GetState(d.X, d.Y, d.Z);
                if (st <= 0 || !World.Data.HasCollision(st)) continue;
                Controls.Clear();
                await EquipBestToolAsync(st, ct);
                if (!await DigAsync(d.X, d.Y, d.Z, ct)) return false;
            }

            if (step.Kind == MoveKind.Pillar)
            {
                if (!await PillarAsync(step, ct)) return false;
                continue;
            }

            if (!await MoveToStepAsync(step, last, ct)) return false;
            if (TeleportCount != startTeleports) return false; // the server disagreed with us: replan
        }
        Controls.Clear();
        return true;
    }

    /// <summary>Steers onto one step's block. False when it stops making progress.</summary>
    private async Task<bool> MoveToStepAsync(PathStep step, bool last, CancellationToken ct)
    {
        double tx = step.X + 0.5, tz = step.Z + 0.5;
        var bestDist = double.MaxValue;
        var sinceProgress = 0;
        for (var tick = 0; tick < 200; tick++)
        {
            var body = BodySnapshot();
            double dx = tx - body.X, dz = tz - body.Z;
            var dist = Math.Sqrt(dx * dx + dz * dz);
            var feet = FeetBlock;
            var levelOk = feet.Y == step.Y || (body.InWater && Math.Abs(feet.Y - step.Y) <= 1);
            // Close enough: tight on the final block, looser mid-path so we flow into the next step.
            if (levelOk && (body.OnGround || body.InWater || body.OnClimbable) && dist < (last ? 0.2 : 0.45))
            {
                if (last) Controls.Clear();
                return true;
            }

            if (dist < bestDist - 0.01) { bestDist = dist; sinceProgress = 0; }
            else if (++sinceProgress > 40 && !(step.Kind is MoveKind.Swim or MoveKind.Climb)) { Controls.Clear(); return false; }

            var yaw = (float)(Math.Atan2(-dx, dz) * 180 / Math.PI);
            Look(yaw, 0);
            // Coasting distance on the ground after letting go: v * f / (1 - f), f = 0.546 on normal blocks.
            var speed = Math.Sqrt(body.VelX * body.VelX + body.VelZ * body.VelZ);
            Controls.Forward = !(last && dist < speed * 1.2 + 0.02);
            Controls.Sprint = false;
            Controls.Jump = step.Kind switch
            {
                MoveKind.JumpUp => dist < 1.3 || body.HorizontalCollision,
                MoveKind.Swim or MoveKind.Climb => feet.Y < step.Y || body.InWater && feet.Y <= step.Y && body.WaterHeight > 1.0,
                _ => body.InWater && body.WaterHeight > 1.2, // keep the head up while crossing water
            };
            if (step.Kind == MoveKind.Climb && dist < 0.3) Controls.Forward = false;
            await WaitTicksAsync(1, ct);
        }
        Controls.Clear();
        return false;
    }

    /// <summary>Jump and place a block under our feet (towering up one block).</summary>
    private async Task<bool> PillarAsync(PathStep step, CancellationToken ct)
    {
        if (!await EquipAsync(s => ThrowawayBlocks.Contains(s.Name), ct)) return false;
        var (bx, by, bz) = step.Place!.Value;
        Controls.Clear();
        Look(Rotation.Yaw, 90);
        Controls.Jump = true;
        // Place as soon as the feet clear the block's top (y + 1), against the block below it.
        var placed = false;
        for (var t = 0; t < 30 && !placed; t++)
        {
            await WaitTicksAsync(1, ct);
            if (Position.Y >= by + 1.0)
            {
                Controls.Jump = false;
                await UseOnBlockAsync(bx, by - 1, bz, BlockFace.Up, Hand.Main, null, ct);
                placed = await WaitUntilAsync(() => World.Data.HasCollision(Math.Max(0, World.GetState(bx, by, bz))), 10, ct);
            }
        }
        Controls.Clear();
        return placed && await WaitUntilAsync(() => OnGround && FeetBlock.Y == step.Y, 20, ct);
    }
}
