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
    /// Blocks navigation never digs through: what players build with and nature rarely places (planks, glass,
    /// doors, beds, chests, bricks, stairs, wool...). Walking somewhere must never break someone's house; collecting
    /// a block on purpose (DigAsync) is not affected. Replace to change the rule.
    /// </summary>
    public Func<BlockType, bool> IsBuiltBlock { get; set; } = DefaultIsBuiltBlock;

    private static readonly string[] BuiltNameParts =
    [
        "planks", "glass", "_wool", "_carpet", "bricks", "door", "_bed", "chest", "barrel", "crafting_table", "furnace", "smoker",
        "torch", "lantern", "_sign", "concrete", "glazed_terracotta", "_stairs", "_slab", "fence", "_wall", "bookshelf",
        "shulker_box", "banner", "rail", "redstone", "repeater", "comparator", "lever", "_button", "pressure_plate", "piston",
        "hopper", "dropper", "dispenser", "observer", "anvil", "enchanting_table", "brewing_stand", "cauldron", "jukebox",
        "note_block", "beacon", "lectern", "loom", "stonecutter", "grindstone", "cartography_table", "fletching_table",
        "smithing_table", "composter", "flower_pot", "candle", "bell", "scaffolding", "target", "respawn_anchor", "lodestone",
        "daylight_detector", "tnt", "sea_lantern", "farmland", "stripped_", "_wood", "polished_", "smooth_", "cut_", "chiseled_",
        "waxed_", "quartz_block", "quartz_pillar", "trapdoor", "iron_bars", "chain", "campfire", "crafter", "spawner", "obsidian",
        "decorated_pot", "_head", "skull", "end_rod", "froglight", "hay_block", "_pane",
    ];

    private static readonly HashSet<string> BuiltBlocks =
    [
        "minecraft:iron_block", "minecraft:gold_block", "minecraft:diamond_block", "minecraft:emerald_block", "minecraft:lapis_block",
        "minecraft:netherite_block", "minecraft:redstone_block", "minecraft:coal_block", "minecraft:copper_block",
    ];

    /// <summary>The default IsBuiltBlock. Ores are never "built" (redstone_ore is not a redstone part).</summary>
    public static bool DefaultIsBuiltBlock(BlockType block) =>
        !block.Name.EndsWith("_ore") && (BuiltBlocks.Contains(block.Name) || BuiltNameParts.Any(block.Name.Contains));

    /// <summary>
    /// Walks (digs, pillars, swims, climbs) to a goal. Replans when stuck, corrected by the server, or when a plan
    /// only got part of the way. Returns true when the goal is reached.
    /// </summary>
    public async Task<bool> GoToAsync(Goal goal, PathOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PathOptions();
        options.DigTicks ??= BestDigTicks;
        // When stuck, get less fussy like a player would: first accept a fall that hurts (but leaves at least 4
        // health), then dig. The caller's options are the starting point, never made stricter.
        var escalation = 0;
        var current = options;
        try
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var feet = FeetBlock;
                if (goal.IsReached(feet.X, feet.Y, feet.Z)) return true;
                var (steps, complete) = Pathfinder.FindPath(feet, goal, current);
                if (steps.Count == 0 && escalation < 2)
                {
                    escalation++;
                    current = new PathOptions
                    {
                        CanDig = current.CanDig || escalation >= 2,
                        CanPlace = current.CanPlace,
                        MaxDrop = Math.Max(current.MaxDrop, 3 + Math.Max(0, (int)Health - 4)),
                        MaxNodes = current.MaxNodes,
                        DigTicks = current.DigTicks,
                    };
                    Log($"no path with the current rules: escalating to max drop {current.MaxDrop}{(current.CanDig ? ", digging" : "")}");
                    (steps, complete) = Pathfinder.FindPath(feet, goal, current);
                }
                PathPlanned?.Invoke(steps);
                Log($"path attempt {attempt}: from {feet} (at {Position.X:0.##} {Position.Y:0.##} {Position.Z:0.##}), {steps.Count} steps, {(complete ? "complete" : "partial")}"
                    + (steps.Count > 0 ? $", first {steps[0].Kind}({steps[0].X},{steps[0].Y},{steps[0].Z}) last {steps[^1].Kind}({steps[^1].X},{steps[^1].Y},{steps[^1].Z})" : ""));
                if (steps.Count == 0)
                {
                    Log($"no path from {feet} ({(complete ? "already there" : "nothing reachable closer")})");
                    return goal.IsReached(feet.X, feet.Y, feet.Z);
                }
                var followed = await FollowAsync(steps, cancellationToken);
                feet = FeetBlock;
                if (goal.IsReached(feet.X, feet.Y, feet.Z)) return true;
                if (!followed) Log($"path interrupted at {FeetBlock} (at {Position.X:0.##} {Position.Y:0.##} {Position.Z:0.##}, onGround {OnGround}), replanning");
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
        // Never through someone's build, nor through modded blocks we know nothing about.
        if (state == World.Data.UnknownState || IsBuiltBlock(World.Data.BlockOf(state))) return null;
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
        var opened = new List<(int X, int Y, int Z)>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var last = i == steps.Count - 1;

            // A closed door or gate on the step: open it (and close it again once we are through).
            if (Pathfinder.IsDoorway(World.GetState(step.X, step.Y, step.Z)))
            {
                if (!await OpenDoorwayAsync(step.X, step.Y, step.Z, opened, ct)) return false;
            }
            else if (opened.Count > 0 && !last)
            {
                // Out of the doorway and one step on: shut what we opened behind us.
                foreach (var d in opened.Where(d => BodyClearOf(d.X, d.Z) && (d.X, d.Y, d.Z) != (step.X, step.Y, step.Z)).ToList())
                {
                    Controls.Clear();
                    await SetDoorwayOpenAsync(d.X, d.Y, d.Z, false, ct);
                    opened.Remove(d);
                }
            }

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
        // Through a door on the very last steps: close it behind us when we are clear of it.
        foreach (var d in opened.Where(d => BodyClearOf(d.X, d.Z) && DistanceToBlock(d.X, d.Y, d.Z) <= BlockReach - 0.5))
            await SetDoorwayOpenAsync(d.X, d.Y, d.Z, false, ct);
        return true;
    }

    /// <summary>Our body (0.6 wide) is entirely outside block column x,z (safe to shut a door there).</summary>
    private bool BodyClearOf(int x, int z)
    {
        var (px, _, pz) = Position;
        return Math.Abs(px - (x + 0.5)) >= 0.8 || Math.Abs(pz - (z + 0.5)) >= 0.8;
    }

    private async Task<bool> OpenDoorwayAsync(int x, int y, int z, List<(int X, int Y, int Z)> opened, CancellationToken ct)
    {
        if (IsDoorwayOpen(x, y, z)) return true;
        Controls.Clear();
        if (!await SetDoorwayOpenAsync(x, y, z, true, ct)) return false;
        opened.Add((x, y, z));
        return true;
    }

    /// <summary>Right-clicks a door or gate until it is open (or closed). False if the server would not change it.</summary>
    public async Task<bool> SetDoorwayOpenAsync(int x, int y, int z, bool open, CancellationToken ct = default)
    {
        if (IsDoorwayOpen(x, y, z) == open) return true;
        await UseOnBlockAsync(x, y, z, FaceToward(x, y, z), Hand.Main, null, ct);
        return await WaitUntilAsync(() => IsDoorwayOpen(x, y, z) == open, 20, ct);
    }

    private bool IsDoorwayOpen(int x, int y, int z) => World.GetState(x, y, z) is var s && s > 0 && World.Data.GetProperty(s, "open") == "true";

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
