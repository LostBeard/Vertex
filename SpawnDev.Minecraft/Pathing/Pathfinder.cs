using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.World;

namespace SpawnDev.Minecraft.Pathing;

/// <summary>Where the pathfinder should get to.</summary>
public abstract class Goal
{
    /// <summary>True when standing (feet block) at x,y,z satisfies the goal.</summary>
    public abstract bool IsReached(int x, int y, int z);
    /// <summary>Admissible-ish distance estimate in blocks.</summary>
    public abstract double Heuristic(int x, int y, int z);

    public static Goal Block(int x, int y, int z) => new GoalNear(x, y, z, 0);
    public static Goal Near(double x, double y, double z, double range) => new GoalNear(x, y, z, range);
    public static Goal XZ(int x, int z) => new GoalXZ(x, z);
}

/// <summary>Within range (blocks, Euclidean) of a point; range 0 = exactly that feet block.</summary>
public sealed class GoalNear(double x, double y, double z, double range) : Goal
{
    public override bool IsReached(int bx, int by, int bz)
    {
        if (range <= 0) return bx == (int)Math.Floor(x) && by == (int)Math.Floor(y) && bz == (int)Math.Floor(z);
        double dx = bx + 0.5 - x, dy = by - y, dz = bz + 0.5 - z;
        return dx * dx + dy * dy + dz * dz <= range * range;
    }
    public override double Heuristic(int bx, int by, int bz)
    {
        double dx = bx + 0.5 - x, dy = by - y, dz = bz + 0.5 - z;
        return Math.Max(0, Math.Sqrt(dx * dx + dy * dy + dz * dz) - range);
    }
}

public sealed class GoalXZ(int x, int z) : Goal
{
    public override bool IsReached(int bx, int by, int bz) => bx == x && bz == z;
    public override double Heuristic(int bx, int by, int bz) => Math.Sqrt((bx - x) * (bx - x) + (bz - z) * (bz - z));
}

/// <summary>Standing anywhere from which a block can be reached (to dig it, open it, place against it).</summary>
public sealed class GoalReachBlock(int x, int y, int z, double reach = 4.0) : Goal
{
    public override bool IsReached(int bx, int by, int bz)
    {
        // Eyes at feet + 1.62; distance to the block's nearest point, like the server's reach check.
        double ex = bx + 0.5, ey = by + 1.62, ez = bz + 0.5;
        double cx = Math.Clamp(ex, x, x + 1), cy = Math.Clamp(ey, y, y + 1), cz = Math.Clamp(ez, z, z + 1);
        var inside = bx == x && (by == y || by + 1 == y) && bz == z;
        return !inside && (ex - cx) * (ex - cx) + (ey - cy) * (ey - cy) + (ez - cz) * (ez - cz) <= reach * reach;
    }
    public override double Heuristic(int bx, int by, int bz) =>
        Math.Max(0, Math.Sqrt((bx + 0.5 - x - 0.5) * (bx + 0.5 - x - 0.5) + (by - y) * (by - y) + (bz + 0.5 - z - 0.5) * (bz + 0.5 - z - 0.5)) - reach);
}

public enum MoveKind { Walk, Diagonal, JumpUp, Drop, Swim, Climb, DigThrough, DigDown, Pillar }

/// <summary>One step of a path: stand at To (feet block), after doing Kind. Dig/Place list blocks to change first.</summary>
public sealed record PathStep(int X, int Y, int Z, MoveKind Kind, (int X, int Y, int Z)[] Dig, (int X, int Y, int Z)? Place);

public sealed class PathOptions
{
    /// <summary>Allow breaking blocks in the way (cost grows with break time).</summary>
    public bool CanDig { get; set; }
    /// <summary>Allow placing blocks (pillar up). Needs a placeable block in the inventory to follow.</summary>
    public bool CanPlace { get; set; }
    /// <summary>Highest drop taken without hesitation (3 = no fall damage).</summary>
    public int MaxDrop { get; set; } = 3;
    public int MaxNodes { get; set; } = 60_000;
    /// <summary>Estimated ticks to break a block state with the best tool we have; null = cannot.</summary>
    public Func<int, int?>? DigTicks { get; set; }
}

/// <summary>
/// A* over feet positions. A node is a block position whose space a standing player fits in (1.8 tall, measured
/// on the real collision boxes, so slabs, stairs, carpets, snow layers and fences behave as in game) with solid
/// support below, or water. The result is a list of steps for PathFollower.
/// </summary>
public sealed class Pathfinder
{
    private readonly GameWorld _world;
    private readonly GameData _data;
    private readonly HashSet<int> _avoidBlocks;
    private readonly HashSet<int> _climbable;
    private readonly HashSet<int> _doorways;
    private readonly int _water, _lava;

    public Pathfinder(GameWorld world)
    {
        _world = world;
        _data = world.Data;
        _avoidBlocks = new[] { "lava", "fire", "soul_fire", "magma_block", "cactus", "sweet_berry_bush", "cobweb", "powder_snow", "wither_rose", "campfire", "soul_campfire", "pointed_dripstone" }
            .Select(n => _data.Block(n)!.Id).ToHashSet();
        _climbable = _data.BlockTag("minecraft:climbable").Select(n => _data.Block(n)!.Id).ToHashSet();
        // Wooden doors and fence gates open by hand (iron ones need redstone and stay walls).
        _doorways = _data.BlockTag("minecraft:wooden_doors").Concat(_data.BlockTag("minecraft:fence_gates")).Select(n => _data.Block(n)!.Id).ToHashSet();
        _water = _data.Block("water")!.Id;
        _lava = _data.Block("lava")!.Id;
    }

    private int State(int x, int y, int z) => _world.GetState(x, y, z);

    private bool Loaded(int x, int z) => _world.IsLoaded(x, z);

    /// <summary>Top of the collision boxes in a block (0 if none), relative to the block's y.</summary>
    private double Top(int state)
    {
        if (state <= 0) return 0;
        var top = 0.0;
        foreach (var b in _data.CollisionBoxes(state)) top = Math.Max(top, b.MaxY);
        return top;
    }

    private bool Solid(int state) => state > 0 && _data.HasCollision(state);
    private bool IsFluid(int state) => state > 0 && (_data.StateFlags[state] & (GameData.FlagWater | GameData.FlagLava)) != 0;
    private bool IsWater(int state) => state > 0 && (_data.StateFlags[state] & GameData.FlagWater) != 0;
    /// <summary>A wooden door or fence gate: walked through (opened first when closed), straight along its axis.</summary>
    public bool IsDoorway(int state) => state > 0 && _doorways.Contains(_data.StateBlock[state]);

    /// <summary>Whether a door or gate lets you through moving along x (it faces east or west) rather than z.</summary>
    private bool DoorAlongX(int state) => _data.GetProperty(state, "facing") is "east" or "west";

    private bool Dangerous(int state) => state > 0 && (_avoidBlocks.Contains(_data.StateBlock[state]) || (_data.StateFlags[state] & GameData.FlagLava) != 0);

    /// <summary>
    /// Where the feet are when standing in block (x,y,z): y + the top of a low collision shape in it (slab 0.5,
    /// carpet 0.0625...), or y itself on top of a full block below. Null when nobody can stand there.
    /// </summary>
    public double? FeetHeight(int x, int y, int z)
    {
        var here = State(x, y, z);
        var below = State(x, y - 1, z);
        if (here < 0 || below < 0) return null;
        if (Dangerous(here) || Dangerous(below)) return null;
        double floor;
        var doorway = IsDoorway(here);
        if (Solid(here) && !doorway)
        {
            var t = Top(here);
            if (t >= 1.0) return null; // a full-height block (or a fence/wall) is a wall, not a floor
            floor = y + t;               // slabs 0.5, soul sand 0.875, farmland 0.9375, carpets 0.0625...
        }
        else if (IsWater(here)) floor = y; // swimming counts as standing
        else if (_climbable.Contains(Math.Max(0, here) == 0 ? -1 : _data.StateBlock[here])) floor = y;
        else
        {
            // Supported by the block below: its top must reach this block's floor (full blocks, top slabs...).
            if (!Solid(below) || Top(below) < 1.0 - 1e-9)
            {
                if (!IsWater(below) && !(below > 0 && _climbable.Contains(_data.StateBlock[below]))) return null;
            }
            if (Top(below) > 1.0 + 1e-9) return null; // fences/walls: cannot stand on (1.5 tall)
            floor = y;
        }
        // Body: 1.8 tall from the floor must be free of collision (in a doorway, the door's own upper half).
        if (!(Clear(x, y + 1, z) || doorway && IsDoorway(State(x, y + 1, z))) || (floor - y + 1.8 > 2.0 && !Clear(x, y + 2, z))) return null;
        return floor;
    }

    private bool JumpHeadroom(int x, int z, double fromFloor, double rise)
    {
        var top = fromFloor + 1.8 + rise + 0.05;
        for (var y = (int)Math.Floor(fromFloor + 1.8); y < top; y++)
            if (!Clear(x, y, z)) return false;
        return true;
    }

    private bool Clear(int x, int y, int z)
    {
        var s = State(x, y, z);
        return s >= 0 && !Solid(s) && !Dangerous(s);
    }

    private readonly record struct Node(int X, int Y, int Z);

    /// <summary>Finds a path from the feet block to the goal (or toward it, when not reachable: partial).</summary>
    public (List<PathStep> Steps, bool Complete) FindPath((int X, int Y, int Z) start, Goal goal, PathOptions options)
    {
        var open = new PriorityQueue<Node, double>();
        var g = new Dictionary<Node, double>();
        var came = new Dictionary<Node, (Node From, PathStep Step)>();
        var s = new Node(start.X, start.Y, start.Z);
        g[s] = 0;
        open.Enqueue(s, goal.Heuristic(s.X, s.Y, s.Z));
        var best = s;
        var bestH = goal.Heuristic(s.X, s.Y, s.Z);
        var expanded = 0;
        while (open.Count > 0 && expanded < options.MaxNodes)
        {
            var n = open.Dequeue();
            if (goal.IsReached(n.X, n.Y, n.Z)) return (Reconstruct(came, n), true);
            expanded++;
            var gn = g[n];
            foreach (var (next, cost, step) in Neighbours(n, options))
            {
                var ng = gn + cost;
                if (g.TryGetValue(next, out var old) && old <= ng) continue;
                g[next] = ng;
                came[next] = (n, step);
                var h = goal.Heuristic(next.X, next.Y, next.Z);
                if (h < bestH) { bestH = h; best = next; }
                open.Enqueue(next, ng + h * 1.01);
            }
        }
        return (Reconstruct(came, best), false);
    }

    private static List<PathStep> Reconstruct(Dictionary<Node, (Node From, PathStep Step)> came, Node end)
    {
        var steps = new List<PathStep>();
        var n = end;
        while (came.TryGetValue(n, out var c))
        {
            steps.Add(c.Step);
            n = c.From;
        }
        steps.Reverse();
        return steps;
    }

    private static readonly (int Dx, int Dz)[] Cardinal = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int Dx, int Dz)[] Diagonals = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    private IEnumerable<(Node, double, PathStep)> Neighbours(Node n, PathOptions o)
    {
        var hereFeet = FeetHeight(n.X, n.Y, n.Z);
        var inWater = IsWater(State(n.X, n.Y, n.Z));
        var onClimbable = State(n.X, n.Y, n.Z) is var hs && hs > 0 && _climbable.Contains(_data.StateBlock[hs]);
        var fromFloor = hereFeet ?? n.Y;
        var hereDoor = State(n.X, n.Y, n.Z) is var hd && IsDoorway(hd) ? hd : 0;

        foreach (var (dx, dz) in Cardinal)
        {
            int x = n.X + dx, z = n.Z + dz;
            if (!Loaded(x, z)) continue;

            // Same block level or one up, classified by how far the floor really rises: up to the 0.6 step height
            // is a walk (slabs, carpets, slab-to-block), up to the 1.25 jump height a jump (blocks, soul sand).
            var moved = false;
            // Through a doorway only straight along its axis, on the level (no jumps, drops or digging there).
            var thereDoor = State(x, n.Y, z) is var td && IsDoorway(td) ? td : 0;
            if (hereDoor != 0 || thereDoor != 0)
            {
                if ((hereDoor == 0 || DoorAlongX(hereDoor) == (dx != 0)) && (thereDoor == 0 || DoorAlongX(thereDoor) == (dx != 0))
                    && FeetHeight(x, n.Y, z) is double df && Math.Abs(df - fromFloor) <= 0.6)
                    yield return (new Node(x, n.Y, z), 1.5, new PathStep(x, n.Y, z, MoveKind.Walk, [], null));
                continue;
            }
            foreach (var ny in new[] { n.Y, n.Y + 1 })
            {
                if (FeetHeight(x, ny, z) is not double f) continue;
                var rise = f - fromFloor;
                if (rise >= -0.6 && rise <= 0.6)
                {
                    var swim = inWater || IsWater(State(x, ny, z));
                    yield return (new Node(x, ny, z), swim ? 2 : 1, new PathStep(x, ny, z, swim ? MoveKind.Swim : MoveKind.Walk, [], null));
                    moved = true;
                    break;
                }
                // A jump: the head must clear floor + 1.8 + the rise (a 3-high tunnel still allows a 1-block step).
                if (rise > 0.6 && rise <= 1.25 && JumpHeadroom(n.X, n.Z, fromFloor, rise) && JumpHeadroom(x, z, fromFloor, rise))
                {
                    yield return (new Node(x, ny, z), 2.0, new PathStep(x, ny, z, MoveKind.JumpUp, [], null));
                    moved = true;
                    break;
                }
            }
            if (moved) continue;
            // Drop down: walk off into a column that is clear for our body, landing within MaxDrop.
            if (Clear(x, n.Y, z) && Clear(x, n.Y + 1, z) && !Solid(State(x, n.Y - 1, z)))
            {
                for (var d = 1; d <= Math.Max(o.MaxDrop, 1) + 20; d++)
                {
                    var ly = n.Y - d;
                    if (ly < _world.MinY) break;
                    var st = State(x, ly, z);
                    if (st < 0) break;
                    if (FeetHeight(x, ly, z) is double)
                    {
                        var water = IsWater(st);
                        if (d <= o.MaxDrop || water)
                            yield return (new Node(x, ly, z), 1 + d * 0.5, new PathStep(x, ly, z, MoveKind.Drop, [], null));
                        break;
                    }
                    if (Solid(st) || Dangerous(st)) break;
                }
            }
            // Dig through: break the (one or two) blocks in the way at our level.
            if (o.CanDig && o.DigTicks != null)
            {
                var dig = new List<(int, int, int)>();
                double ticks = 0;
                var ok = true;
                foreach (var dy in new[] { 0, 1 })
                {
                    var st = State(x, n.Y + dy, z);
                    if (st < 0 || Dangerous(st)) { ok = false; break; }
                    if (!Solid(st)) continue;
                    if (o.DigTicks(st) is not int t) { ok = false; break; }
                    ticks += t;
                    dig.Add((x, n.Y + dy, z));
                }
                // Never dig into fluid (a lava/water flood) and need a floor on the other side.
                if (ok && dig.Count > 0 && !IsFluid(State(x, n.Y + 2, z)) && Solid(State(x, n.Y - 1, z)) && Top(State(x, n.Y - 1, z)) >= 1.0)
                    yield return (new Node(x, n.Y, z), 1 + 2 + ticks / 10.0, new PathStep(x, n.Y, z, MoveKind.DigThrough, dig.ToArray(), null));
            }
        }

        // Diagonals: both cardinal neighbours must be passable at body height (no corner cutting).
        foreach (var (dx, dz) in Diagonals)
        {
            int x = n.X + dx, z = n.Z + dz;
            if (!Loaded(x, z)) continue;
            if (hereDoor == 0 && !IsDoorway(State(x, n.Y, z)) && FeetHeight(x, n.Y, z) is double f && Math.Abs(f - fromFloor) < 1e-9
                && Clear(n.X + dx, n.Y, n.Z) && Clear(n.X + dx, n.Y + 1, n.Z) && Clear(n.X, n.Y, n.Z + dz) && Clear(n.X, n.Y + 1, n.Z + dz))
                yield return (new Node(x, n.Y, z), Math.Sqrt(2), new PathStep(x, n.Y, z, MoveKind.Diagonal, [], null));
        }

        // Up and down in water or on a ladder/vine.
        if (inWater || onClimbable)
        {
            if (Clear(n.X, n.Y + 2, n.Z) && (IsWater(State(n.X, n.Y + 1, n.Z)) || (State(n.X, n.Y + 1, n.Z) is var a && a > 0 && _climbable.Contains(_data.StateBlock[a])) || FeetHeight(n.X, n.Y + 1, n.Z) is double))
                if (FeetHeight(n.X, n.Y + 1, n.Z) is double)
                    yield return (new Node(n.X, n.Y + 1, n.Z), 1.5, new PathStep(n.X, n.Y + 1, n.Z, inWater ? MoveKind.Swim : MoveKind.Climb, [], null));
        }

        // Dig straight down one block (the way out of a tree top, a pit, or onto a lower level): needs a solid floor
        // under the dug block and no fluid around it to flood in.
        if (o.CanDig && o.DigTicks != null && !inWater && hereDoor == 0)
        {
            var below = State(n.X, n.Y - 1, n.Z);
            var under = State(n.X, n.Y - 2, n.Z);
            if (Solid(below) && !Dangerous(below) && o.DigTicks(below) is int t && Solid(under) && Top(under) >= 1.0 && !Dangerous(under)
                && !IsFluid(State(n.X + 1, n.Y - 1, n.Z)) && !IsFluid(State(n.X - 1, n.Y - 1, n.Z)) && !IsFluid(State(n.X, n.Y - 1, n.Z + 1)) && !IsFluid(State(n.X, n.Y - 1, n.Z - 1)))
                yield return (new Node(n.X, n.Y - 1, n.Z), 1.5 + t / 10.0, new PathStep(n.X, n.Y - 1, n.Z, MoveKind.DigDown, [(n.X, n.Y - 1, n.Z)], null));
        }

        // Pillar: jump and place a block under ourselves. A node with no support is one we pillared up to (nodes are
        // otherwise always standable), so it stands on our own placed block and can pillar again.
        if (o.CanPlace && !inWater && hereDoor == 0 && Math.Abs((hereFeet ?? n.Y) - n.Y) < 1e-9 && Clear(n.X, n.Y + 2, n.Z))
            yield return (new Node(n.X, n.Y + 1, n.Z), 4.0, new PathStep(n.X, n.Y + 1, n.Z, MoveKind.Pillar, [], (n.X, n.Y, n.Z)));
    }
}
