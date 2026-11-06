using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Items;
using SpawnDev.Minecraft.Pathing;

namespace SpawnDev.Minecraft;

// Getting places: plan with Pathfinder, then steer the body there tick by tick.
public sealed partial class MinecraftClient
{
    private Pathfinder? _pathfinder;

    public Pathfinder Pathfinder => _pathfinder ??= new Pathfinder(World) { MayNotDigAt = PartOfABuild, RefusedDigAt = DigRefused, RefusedMove = MoveFailed };

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

    private readonly Dictionary<((int X, int Y, int Z) From, (int X, int Y, int Z) To), DateTime> _failedMoves = [];

    /// <summary>Did going from this block to that one get him nowhere, in the last 5 minutes?</summary>
    public bool MoveFailed(int fx, int fy, int fz, int tx, int ty, int tz)
    {
        lock (_failedMoves) return _failedMoves.TryGetValue(((fx, fy, fz), (tx, ty, tz)), out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(5);
    }

    private readonly HashSet<(int X, int Y, int Z)> _placedByMe = [];

    /// <summary>He put a block here himself: nobody else's work.</summary>
    internal void NotePlacedByMe(int x, int y, int z) { lock (_placedByMe) _placedByMe.Add((x, y, z)); }

    /// <summary>How far from something built a block still counts as part of the building.</summary>
    public const int BuildReach = 3;

    /// <summary>
    /// Is this block part of something someone built? A log or cobblestone says nothing by itself: a house's frame is the same oak log as a tree. What says so is what is round it: anything built within <see cref="BuildReach"/> blocks that he did not place
    /// himself. Live he took a village house apart for its logs and stone, and it would have been the same with a friend's (TJ, 2026-11-04). A tree beside someone's torch is left too; there are other trees.
    /// </summary>
    public bool PartOfABuild(int x, int y, int z)
    {
        for (var dx = -BuildReach; dx <= BuildReach; dx++)
        for (var dy = -BuildReach; dy <= BuildReach; dy++)
        for (var dz = -BuildReach; dz <= BuildReach; dz++)
            if (IsBuiltHere(x + dx, y + dy, z + dz)) return true;
        return false;
    }

    private bool IsBuiltHere(int x, int y, int z)
    {
        var s = World.GetState(x, y, z);
        if (s <= 0 || !IsBuiltBlock(World.Data.BlockOf(s))) return false;
        lock (_placedByMe) return !_placedByMe.Contains((x, y, z));
    }

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
        var nowhere = 0;
        try
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var feet = FeetBlock;
                if (goal.IsReached(feet.X, feet.Y, feet.Z)) return true;
                var (steps, complete) = Pathfinder.FindPath(feet, goal, current);
                // (Each in turn in one go: with one step per attempt, no path at the first meant giving up before digging was ever tried.) Last of all, natural blocks near someone's building: stuck is worse than that. He stood in the
                // leaves of a tree for 50 minutes, a crafting table 3 blocks off making every block round him off limits (live 2026-11-05). The built blocks themselves stay out of it.
                while (steps.Count == 0 && escalation < 3)
                {
                    escalation++;
                    current = new PathOptions
                    {
                        CanDig = current.CanDig || escalation >= 2,
                        CanPlace = current.CanPlace,
                        // A drop he accepts when stuck costs hearts: 19 blocks cost 8 of them (live 2026-11-03: "I lost 9.5 hearts in a few seconds", nothing near, in daylight, again and again). 8 blocks is 2.5
                        // hearts; further than that he digs round, builds a way down, or looks for another way.
                        MaxDrop = Math.Max(current.MaxDrop, Math.Min(8, 3 + Math.Max(0, (int)Health - 4))),
                        MaxNodes = current.MaxNodes,
                        DigTicks = current.DigTicks,
                        Sprint = current.Sprint,
                        DigNearBuilds = current.DigNearBuilds || escalation >= 3,
                    };
                    Log($"no path with the current rules: escalating to max drop {current.MaxDrop}{(current.CanDig ? ", digging" : "")}{(current.DigNearBuilds ? ", near buildings too" : "")}");
                    (steps, complete) = Pathfinder.FindPath(feet, goal, current);
                    // (Only when this is what found the way: said on every search with no way, it said "stuck" 104 times in 19 minutes, many of them along a river, while he was getting on with things.)
                    if (escalation == 3 && steps.Count > 0) DigNote?.Invoke($"stuck at {feet.X} {feet.Y} {feet.Z}: digging near a building to get out");
                }
                PathPlanned?.Invoke(steps);
                Log($"path attempt {attempt}: from {feet} (at {Position.X:0.##} {Position.Y:0.##} {Position.Z:0.##}), {steps.Count} steps, {(complete ? "complete" : "partial")}"
                    + (steps.Count > 0 ? $", first {steps[0].Kind}({steps[0].X},{steps[0].Y},{steps[0].Z}) last {steps[^1].Kind}({steps[^1].X},{steps[^1].Y},{steps[^1].Z})" : ""));
                if (steps.Count == 0)
                {
                    Log($"no path from {feet} ({(complete ? "already there" : "nothing reachable closer")})");
                    return goal.IsReached(feet.X, feet.Y, feet.Z);
                }
                var from = feet;
                var followed = await FollowAsync(steps, current.Sprint, cancellationToken);
                feet = FeetBlock;
                if (goal.IsReached(feet.X, feet.Y, feet.Z)) return true;
                // Tried, and not one block further: that move does not work, whatever the plan says, and planning it again is all he would do. TJ watched him jump 4 times, stop for 3 seconds and start over in a tunnel, more than 20 times (2026-11-05). Not again for 5
                // minutes; three of those from one spot and this way is given up, so whoever asked can try something else.
                if (!followed && feet == from)
                {
                    var step = steps[0];
                    lock (_failedMoves) _failedMoves[(from, (step.X, step.Y, step.Z))] = DateTime.UtcNow;
                    string At(int x, int y, int z) => World.GetBlock(x, y, z)?.Name.Replace("minecraft:", "") ?? "?";
                    DigNote?.Invoke($"move failed: {step.Kind} from {from.X} {from.Y} {from.Z} (at {Position.Y:0.##}) to {step.X} {step.Y} {step.Z}; over me {At(from.X, from.Y + 2, from.Z)}, {At(from.X, from.Y + 3, from.Z)}; there {At(step.X, step.Y - 1, step.Z)} under, {At(step.X, step.Y, step.Z)}, {At(step.X, step.Y + 1, step.Z)}, {At(step.X, step.Y + 2, step.Z)}");
                    if (++nowhere >= 3) return false;
                }
                else nowhere = 0;
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

    /// <summary>Ticks to break a block state with the best tool carried; null = he will not (unbreakable, someone's build, a modded block nothing is known about). What PathOptions.DigTicks wants, for planning without moving.</summary>
    public int? BestDigTicks(int state)
    {
        if (World.Data.Hardness[state] < 0) return null;
        // Never through someone's build, nor through modded blocks we know nothing about.
        if (World.Data.IsUnknownBlock(state) || IsBuiltBlock(World.Data.BlockOf(state))) return null;
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

    private async Task<bool> FollowAsync(List<PathStep> steps, bool sprint, CancellationToken ct)
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

            if (!await MoveToStepAsync(step, last, sprint, ct)) return false;
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

    private bool IsDoorwayOpen(int x, int y, int z) => World.GetState(x, y, z) is var s && s > 0 && World.Data.IsOpen(s);

    /// <summary>Steers onto one step's block. False when it stops making progress.</summary>
    private async Task<bool> MoveToStepAsync(PathStep step, bool last, bool sprint, CancellationToken ct)
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
            // A swimmer floats with his feet about 1.3 below the surface: at y 198.7 on water whose surface the path gives as the air block at y 200, two blocks from the step's level however close he gets. Asking for less stalled
            // him on each such step until it gave up after 2 seconds, and he replanned: "he swims about 3 blocks. stops" (TJ, 2026-11-04), 0.65 blocks a second with 12 pauses on a 37 block swim.
            var levelOk = feet.Y == step.Y || (body.InWater && Math.Abs(feet.Y - step.Y) <= 2);
            var swimming = step.Kind == MoveKind.Swim || (body.InWater && step.Kind != MoveKind.Climb);
            // Close enough: tight on the final block, looser mid-path so we flow into the next step. In the water looser still, past the half block within which he stops pushing (below): between the two he would drift to a stop and never arrive.
            // (The last step in the water must end inside its block: a goal is a block, and half a block short of it he has not arrived.)
            if (levelOk && (body.OnGround || body.InWater || body.OnClimbable) && dist < (swimming ? (last ? 0.4 : 0.6) : last ? 0.2 : 0.45))
            {
                if (last) Controls.Clear();
                return true;
            }

            if (dist < bestDist - 0.01) { bestDist = dist; sinceProgress = 0; }
            else if (++sinceProgress > 40 && !(step.Kind is MoveKind.Swim or MoveKind.Climb)) { Controls.Clear(); return false; }

            // Aim at the block only while it is somewhere else: within a third of a block the direction to its middle is noise, and in the water (a swim-up step is straight above him, and he
            // drifts) the head swung round on every tick (TJ, 2026-11-02: "looking around like a madman" between short hops).
            // In the water he coasts: pushing forward at a block half a step away overshot it, aiming turned him round to face it, he overshot back, and so on, every 14 ticks (measured: 76 swings
            // of the head of up to 180 degrees on a 13 block swim). In the water (and on swim steps) the dead zone is the same half block the step counts as reached from; what is left is up or down.
            var coasting = step.Kind == MoveKind.Swim || (body.InWater && step.Kind != MoveKind.Climb);      // not a ladder: up a ladder he has to keep pressing against it
            var aim = dist > (coasting && !last ? 0.5 : 0.3);
            if (aim) Look((float)(Math.Atan2(-dx, dz) * 180 / Math.PI), 0);
            // Coasting distance on the ground after letting go: v * f / (1 - f), f = 0.546 on normal blocks.
            var speed = Math.Sqrt(body.VelX * body.VelX + body.VelZ * body.VelZ);
            Controls.Forward = !(last && dist < speed * 1.2 + 0.02) && (aim || !coasting);      // straight up (or down) a ladder or the water: no pushing sideways
            Controls.Sprint = sprint && !last && step.Kind is MoveKind.Walk or MoveKind.Diagonal;
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
    private async Task<bool> PillarAsync(PathStep step, CancellationToken ct) =>
        await PillarUpAsync(s => ThrowawayBlocks.Contains(s.Name), ct) && FeetBlock.Y == step.Y;

    /// <summary>
    /// Towers up one block: jump, and as the feet clear the block we stood in, place one of <paramref name="block"/>
    /// there against the block below. True once we stand on it.
    /// </summary>
    public async Task<bool> PillarUpAsync(Func<ItemStack, bool> block, CancellationToken ct = default)
    {
        if (!await EquipAsync(block, ct)) return false;
        var (bx, by, bz) = FeetBlock;
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
                placed = await PlaceAgainstAsync(bx, by - 1, bz, BlockFace.Up, ct);
            }
        }
        Controls.Clear();
        return placed && await WaitUntilAsync(() => OnGround && FeetBlock.Y == by + 1, 20, ct);
    }
}
