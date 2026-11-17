using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Entities;
using SpawnDev.Minecraft.Items;
using SpawnDev.Minecraft.Physics;
using SpawnDev.Minecraft.Protocol;
using SpawnDev.Minecraft.World;
using CB = SpawnDev.Minecraft.Protocol.PacketIds;

namespace SpawnDev.Minecraft;

public enum Hand { Main = 0, Off = 1 }

/// <summary>Block faces in protocol order (down, up, north, south, west, east).</summary>
public enum BlockFace { Down = 0, Up = 1, North = 2, South = 3, West = 4, East = 5 }

// Doing things in the world: dig, place, use, eat, attack, hotbar, container clicks.
public sealed partial class MinecraftClient
{
    private int _sequence;
    private readonly List<(int Target, TaskCompletionSource Done)> _tickWaiters = [];
    private long _ticks;

    /// <summary>Client ticks run since connecting.</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    /// <summary>Survival block reach (block_interaction_range); the server allows +1 more.</summary>
    public double BlockReach => Attribute("block_interaction_range") ?? 4.5;
    public double EntityReach => Attribute("entity_interaction_range") ?? 3.0;

    /// <summary>Is the block within <paramref name="reach"/> of the eyes, measured to its nearest point, as the server's reach check and <see cref="Pathing.GoalReachBlock"/> measure it?</summary>
    public static bool WithinReach((double X, double Y, double Z) eye, int x, int y, int z, double reach)
    {
        double cx = Math.Clamp(eye.X, x, x + 1), cy = Math.Clamp(eye.Y, y, y + 1), cz = Math.Clamp(eye.Z, z, z + 1);
        return (eye.X - cx) * (eye.X - cx) + (eye.Y - cy) * (eye.Y - cy) + (eye.Z - cz) * (eye.Z - cz) <= reach * reach;
    }

    /// <summary>
    /// The blocks of a kind that he can reach from where he stands, nearest first. To the nearest point, not the centre (which is up to 0.87 further): a walk "to within reach of the table" ended where its
    /// centre was 4.07 away, the table then counted as out of reach, and with 3 planks he could not make another: "I need a crafting table (and could not make or reach one)", 12 jobs in 19 hours (2026-11-06).
    /// </summary>
    public List<(int X, int Y, int Z)> BlocksInReach(Func<int, bool> stateMatches, double reach, int maxCount = 1)
    {
        var eye = EyePosition;
        return World.FindBlocks(stateMatches, eye, reach + 0.9, 64).Where(b => WithinReach(eye, b.X, b.Y, b.Z, reach)).Take(maxCount).ToList();
    }

    public (double X, double Y, double Z) EyePosition
    {
        get
        {
            var (x, y, z) = Position;
            return (x, y + PlayerState.EyeHeight, z);
        }
    }

    /// <summary>Completes after n more client ticks (or throws when disconnected).</summary>
    public Task WaitTicksAsync(int n, CancellationToken cancellationToken = default)
    {
        if (n <= 0) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_tickWaiters) _tickWaiters.Add(((int)(Ticks + n), tcs));
        return tcs.Task.WaitAsync(cancellationToken);
    }

    // Called by the tick loop once per tick (after physics).
    private void AdvanceTickWaiters()
    {
        var now = Interlocked.Increment(ref _ticks);
        List<TaskCompletionSource>? due = null;
        lock (_tickWaiters)
        {
            for (var i = _tickWaiters.Count - 1; i >= 0; i--)
            {
                if (_tickWaiters[i].Target > now) continue;
                (due ??= []).Add(_tickWaiters[i].Done);
                _tickWaiters.RemoveAt(i);
            }
        }
        if (due != null) foreach (var t in due) t.TrySetResult();
    }

    private void FailTickWaiters()
    {
        lock (_tickWaiters)
        {
            foreach (var w in _tickWaiters) w.Done.TrySetException(new InvalidOperationException("disconnected: " + DisconnectReason));
            _tickWaiters.Clear();
        }
    }

    private int NextSequence() => Interlocked.Increment(ref _sequence);

    private ValueTask SendAsync(PacketWriter packet, CancellationToken ct) =>
        (_connection ?? throw new InvalidOperationException("not connected")).SendAsync(packet, ct);

    // ---------------------------------------------------------------- hotbar

    /// <summary>Selects hotbar slot 0-8.</summary>
    public async Task SelectHotbarAsync(int index, CancellationToken cancellationToken = default)
    {
        if (index is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(index));
        using var p = new PacketWriter(CB.Play.Serverbound.SetCarriedItem);
        p.WriteShort((short)index);
        await SendAsync(p, cancellationToken);
        if (Inventory.SelectedHotbar != index) _lastHeldChangeTick = Ticks;
        Inventory.SelectedHotbar = index;
    }

    /// <summary>
    /// Puts the best stack matching an item (by name) into the main hand: selects it if it is on the hotbar,
    /// else swaps it into the selected hotbar slot. Returns false when we have none.
    /// </summary>
    public async Task<bool> EquipAsync(Func<ItemStack, bool> match, CancellationToken cancellationToken = default)
    {
        if (!Inventory.MainHand.IsEmpty && match(Inventory.MainHand)) return true;
        for (var i = 0; i < 9; i++)
            if (!Inventory[PlayerInventory.HotbarStart + i].IsEmpty && match(Inventory[PlayerInventory.HotbarStart + i]))
            {
                await SelectHotbarAsync(i, cancellationToken);
                return true;
            }
        var slot = Inventory.Find(match).FirstOrDefault(-1);
        if (slot < 0) return false;
        // Mode 2 (SWAP): the clicked slot swaps with hotbar slot "button".
        await ClickAsync(0, slot, Inventory.SelectedHotbar, 2, cancellationToken);
        await WaitUntilAsync(() => !Inventory.MainHand.IsEmpty && match(Inventory.MainHand), 40, cancellationToken);
        return !Inventory.MainHand.IsEmpty && match(Inventory.MainHand);
    }

    public Task<bool> EquipAsync(string itemName, CancellationToken cancellationToken = default)
    {
        var name = GameData.Normalize(itemName);
        return EquipAsync(s => s.Name == name, cancellationToken);
    }

    /// <summary>Waits up to maxTicks ticks for a condition (checked each tick).</summary>
    public async Task<bool> WaitUntilAsync(Func<bool> condition, int maxTicks, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (condition()) return true;
            await WaitTicksAsync(1, cancellationToken);
        }
        return condition();
    }

    // ---------------------------------------------------------------- armor

    private static readonly (string Slot, int Index)[] ArmorSlots = [("head", 5), ("chest", 6), ("legs", 7), ("feet", 8)];

    /// <summary>How good a stack is as armor for one slot (armor + half its toughness), or null if it is not armor for it.</summary>
    public static double? ArmorScore(ItemStack stack, string slot)
    {
        if (stack.IsEmpty) return null;
        double? score = null;
        foreach (var m in ItemModifiers(stack))
        {
            if (m.Slot != slot) continue;
            if (m.Attribute == "minecraft:armor") score = (score ?? 0) + m.Amount;
            else if (m.Attribute == "minecraft:armor_toughness") score = (score ?? 0) + m.Amount * 0.5;
        }
        return score;
    }

    /// <summary>The inventory slot of a piece better than what we wear in an armor slot, or -1.</summary>
    private int BetterArmor(string slot, int index)
    {
        var worn = ArmorScore(Inventory[index], slot) ?? -1;
        var best = -1;
        var bestScore = worn;
        foreach (var i in Inventory.Find(s => ArmorScore(s, slot) != null))
        {
            var score = ArmorScore(Inventory[i], slot)!.Value;
            if (score > bestScore) { best = i; bestScore = score; }
        }
        return best;
    }

    /// <summary>True when we carry armor better than what we wear (cheap: no clicks).</summary>
    public bool HasBetterArmor => ArmorSlots.Any(a => BetterArmor(a.Slot, a.Index) >= 0);

    /// <summary>
    /// Wears the best armor we carry, like a player shift-clicking it in the inventory screen (a worse piece comes
    /// off first, into a free slot). Returns how many pieces were put on.
    /// </summary>
    public async Task<int> EquipBestArmorAsync(CancellationToken cancellationToken = default)
    {
        if (OpenWindow != null) return 0; // the inventory screen is not ours while a container is open
        var put = 0;
        foreach (var (slot, index) in ArmorSlots)
        {
            var from = BetterArmor(slot, index);
            if (from < 0) continue;
            if (!Inventory[index].IsEmpty)
            {
                if (Inventory.FirstEmpty() == null) continue;
                await ClickAsync(0, index, 0, 1, cancellationToken); // shift-click: off, into the inventory
                if (!await WaitUntilAsync(() => Inventory[index].IsEmpty, 20, cancellationToken)) continue;
                from = BetterArmor(slot, index);
                if (from < 0) continue;
            }
            var name = Inventory[from].Name;
            await ClickAsync(0, from, 0, 1, cancellationToken); // shift-click armor: onto its slot
            if (await WaitUntilAsync(() => Inventory[index].Name == name, 20, cancellationToken)) put++;
        }
        return put;
    }

    // ---------------------------------------------------------------- containers

    /// <summary>
    /// A container click (vanilla ServerboundContainerClickPacket). Mode 0 pick/put (button 0 left, 1 right),
    /// 1 shift-click, 2 hotbar swap (button = hotbar 0-8, 40 = off hand), 3 middle, 4 drop (button 0 one, 1 all),
    /// 5 drag, 6 double-click. The predicted changes are left empty: the server applies the click and sends
    /// back every slot that differs, so our inventory is always the server's truth.
    /// </summary>
    public async Task ClickAsync(int windowId, int slot, int button, int mode, CancellationToken cancellationToken = default)
    {
        var stateId = windowId == 0 ? InventoryStateId : OpenWindow?.Id == windowId ? OpenWindow.StateId : 0;
        using var p = new PacketWriter(CB.Play.Serverbound.ContainerClick);
        p.WriteVarInt(windowId).WriteVarInt(stateId).WriteShort((short)slot).WriteSByte((sbyte)button).WriteVarInt(mode)
            .WriteVarInt(0)      // changed slots (no prediction)
            .WriteBool(false);   // carried item prediction: none
        await SendAsync(p, cancellationToken);
    }

    public async Task CloseWindowAsync(CancellationToken cancellationToken = default)
    {
        var id = OpenWindow?.Id ?? 0;
        using var p = new PacketWriter(CB.Play.Serverbound.ContainerClose);
        p.WriteVarInt(id);
        await SendAsync(p, cancellationToken);
        // Until the server has heard this, what it puts in our pockets is still sent as a change to this screen: it is kept to read those by (see ContainerSetSlot).
        _closedWindow = OpenWindow;
        OpenWindow = null;
    }

    /// <summary>Drops the held stack (Q / ctrl+Q).</summary>
    public async Task DropHeldAsync(bool wholeStack = true, CancellationToken cancellationToken = default)
    {
        using var p = new PacketWriter(CB.Play.Serverbound.PlayerAction);
        p.WriteVarInt(wholeStack ? 3 : 4).WritePosition(0, 0, 0).WriteByte(0).WriteVarInt(0);
        await SendAsync(p, cancellationToken);
    }

    // ---------------------------------------------------------------- digging

    /// <summary>Vanilla Player.getDestroySpeed for a block state with what is in the main hand now.</summary>
    public float DestroySpeed(int state)
    {
        var item = Inventory.MainHand;
        var f = ToolSpeed(item, state);
        if (f > 1.0f) f += (float)(Attribute("mining_efficiency") ?? 0);
        if (_effects.TryGetValue("minecraft:haste", out var haste) || _effects.TryGetValue("minecraft:conduit_power", out haste))
            f *= 1.0f + (haste + 1) * 0.2f;
        if (_effects.TryGetValue("minecraft:mining_fatigue", out var fatigue))
            f *= fatigue switch { 0 => 0.3f, 1 => 0.09f, 2 => 0.0027f, _ => 8.1E-4f };
        f *= (float)(Attribute("block_break_speed") ?? 1);
        if (EyeInWater()) f *= (float)(Attribute("submerged_mining_speed") ?? 0.2);
        if (!OnGround) f /= 5.0f;
        return f;
    }

    /// <summary>BlockState.getDestroyProgress: the fraction of the block broken per tick (1 or more = instant).</summary>
    public float DestroyProgressPerTick(int state)
    {
        if (state < 0) return 0; // unloaded: unknown, so not breakable as far as we know
        var hardness = World.Data.Hardness[state];
        if (hardness < 0) return 0; // bedrock, barriers
        if (hardness == 0) return 1;
        var correct = (World.Data.StateFlags[state] & GameData.FlagNeedsTool) == 0 || IsCorrectTool(Inventory.MainHand, state);
        return DestroySpeed(state) / hardness / (correct ? 30 : 100);
    }

    /// <summary>Whether the held item makes this block drop its item (Tool rules / requires-correct-tool).</summary>
    public bool CanHarvest(int state) =>
        state >= 0 && ((World.Data.StateFlags[state] & GameData.FlagNeedsTool) == 0 || IsCorrectTool(Inventory.MainHand, state));

    /// <summary>Ticks to break a block with the current hand (vanilla rounds progress per tick).</summary>
    public int TicksToBreak(int state)
    {
        var p = DestroyProgressPerTick(state);
        return p <= 0 ? int.MaxValue : p >= 1 ? 0 : (int)Math.Ceiling(1 / p);
    }

    private float ToolSpeed(ItemStack item, int state)
    {
        foreach (var rule in item.Type?.ToolRules ?? [])
            if (rule.Speed is float s && RuleMatches(rule, state)) return s;
        return item.Type?.ToolDefaultSpeed ?? 1;
    }

    private bool IsCorrectTool(ItemStack item, int state)
    {
        foreach (var rule in item.Type?.ToolRules ?? [])
            if (rule.CorrectForDrops is bool c && RuleMatches(rule, state)) return c;
        return false;
    }

    private bool RuleMatches(ToolRule rule, int state)
    {
        var block = World.Data.BlockOf(state);
        if (rule.Blocks != null) return rule.Blocks.Contains(block.Name);
        if (rule.BlockTag == null) return false;
        // The server's tags are the truth (data packs can change them); the jar's vanilla tags until they arrive.
        var ids = Tag("minecraft:block", rule.BlockTag);
        return ids.Count > 0 ? ids.Contains(block.Id) : World.Data.BlockTag(rule.BlockTag).Contains(block.Name);
    }

    private bool EyeInWater()
    {
        var (x, y, z) = EyePosition;
        var st = World.GetState((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
        return st > 0 && (World.Data.StateFlags[st] & GameData.FlagWater) != 0;
    }

    /// <summary>The face of a block that points most toward our eyes.</summary>
    public BlockFace FaceToward(int x, int y, int z)
    {
        var (ex, ey, ez) = EyePosition;
        double dx = ex - (x + 0.5), dy = ey - (y + 0.5), dz = ez - (z + 0.5);
        double ax = Math.Abs(dx), ay = Math.Abs(dy), az = Math.Abs(dz);
        if (ay >= ax && ay >= az) return dy > 0 ? BlockFace.Up : BlockFace.Down;
        if (ax >= az) return dx > 0 ? BlockFace.East : BlockFace.West;
        return dz > 0 ? BlockFace.South : BlockFace.North;
    }

    /// <summary>Distance from the eyes to the nearest point of a block (what the server's reach check measures).</summary>
    public double DistanceToBlock(int x, int y, int z)
    {
        var (ex, ey, ez) = EyePosition;
        double cx = Math.Clamp(ex, x, x + 1), cy = Math.Clamp(ey, y, y + 1), cz = Math.Clamp(ez, z, z + 1);
        double dx = ex - cx, dy = ey - cy, dz = ez - cz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Where a player with his eyes at <paramref name="eye"/> could put his crosshair on a block: a point on one of the faces turned toward him with no other block's shape between it and his eyes. Null when
    /// every such face is behind something, and <paramref name="inTheWay"/> is then the first block his look meets on the way to the face he sees most squarely. The server does not check this; a player's own client does,
    /// and he was breaking ore through the stone in front of it (TJ, 2026-11-16: "he mines blocks that a normal player would not be able to because they would not be able to see them").
    /// </summary>
    public static (double X, double Y, double Z)? AimPointOn(GameWorld world, (double X, double Y, double Z) eye, int x, int y, int z, out (int X, int Y, int Z)? inTheWay)
    {
        inTheWay = null;
        // His eyes are in it (buried in sand, a block set where he stands): whichever way he looks, that is the block he is looking at. Without this he could not dig himself out, and lost a heart a second.
        if ((int)Math.Floor(eye.X) == x && (int)Math.Floor(eye.Y) == y && (int)Math.Floor(eye.Z) == z) return (x + 0.5, y + 0.5, z + 0.5);
        // The faces turned toward the eyes, the one he sees most squarely first; on each, its middle and four points toward its corners (a player moves his crosshair over what he can see of it).
        var faces = new List<(double D, double X, double Y, double Z, int Axis)>();
        if (eye.X > x + 1) faces.Add((eye.X - (x + 1), x + 1, y + 0.5, z + 0.5, 0)); else if (eye.X < x) faces.Add((x - eye.X, x, y + 0.5, z + 0.5, 0));
        if (eye.Y > y + 1) faces.Add((eye.Y - (y + 1), x + 0.5, y + 1, z + 0.5, 1)); else if (eye.Y < y) faces.Add((y - eye.Y, x + 0.5, y, z + 0.5, 1));
        if (eye.Z > z + 1) faces.Add((eye.Z - (z + 1), x + 0.5, y + 0.5, z + 1, 2)); else if (eye.Z < z) faces.Add((z - eye.Z, x + 0.5, y + 0.5, z, 2));
        faces.Sort((a, b) => b.D.CompareTo(a.D));       // the face he is furthest out from is the one he looks at most squarely
        ReadOnlySpan<(double U, double V)> spots = [(0, 0), (0.3, 0.3), (-0.3, 0.3), (0.3, -0.3), (-0.3, -0.3)];
        foreach (var f in faces)
            foreach (var (u, v) in spots)
            {
                var p = f.Axis switch { 0 => (f.X, f.Y + u, f.Z + v), 1 => (f.X + u, f.Y, f.Z + v), _ => (f.X + u, f.Y + v, f.Z) };
                var hit = FirstShapeOnTheWay(world, eye, p, x, y, z);
                if (hit == null) return p;
                inTheWay ??= hit;
            }
        return null;
    }

    /// <summary>The first cell holding a block with a shape on the straight line from the eyes to a point, the target's own cell aside; null when the line is open.</summary>
    private static (int X, int Y, int Z)? FirstShapeOnTheWay(GameWorld world, (double X, double Y, double Z) eye, (double X, double Y, double Z) to, int tx, int ty, int tz)
    {
        double dx = to.X - eye.X, dy = to.Y - eye.Y, dz = to.Z - eye.Z;
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy + dz * dz) / 0.05));
        int lx = int.MinValue, ly = 0, lz = 0, state = 0;
        Box[]? shape = null;
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            double px = eye.X + dx * t, py = eye.Y + dy * t, pz = eye.Z + dz * t;
            int cx = (int)Math.Floor(px), cy = (int)Math.Floor(py), cz = (int)Math.Floor(pz);
            if (cx != lx || cy != ly || cz != lz)
            {
                (lx, ly, lz) = (cx, cy, cz);
                if (cx == tx && cy == ty && cz == tz) return null;      // his look has reached the block itself
                state = world.GetState(cx, cy, cz);
                if (state < 0) return (cx, cy, cz);                    // ground he does not know: he cannot say he sees through it
                shape = state > 0 && world.Data.HasCollision(state) ? world.Data.CollisionBoxes(state) : null;
            }
            if (shape == null) continue;
            // The block's own shape, not its whole cell: a look passes over a slab and beside a fence post.
            double fx = px - cx, fy = py - cy, fz = pz - cz;
            foreach (var b in shape)
                if (fx >= b.MinX && fx <= b.MaxX && fy >= b.MinY && fy <= b.MaxY && fz >= b.MinZ && fz <= b.MaxZ) return (cx, cy, cz);
        }
        return null;
    }

    /// <summary>Where he could put his crosshair on this block from where he stands, or null when it is behind something (see the static form).</summary>
    public (double X, double Y, double Z)? AimPointOn(int x, int y, int z) => AimPointOn(World, EyePosition, x, y, z, out _);

    /// <summary>
    /// Breaks a block as a player does: the one he is looking at. A block behind others is got at by breaking what is in front of it first, nearest first, up to eight of them; not through someone's build, a
    /// block that would let lava out, or one that will not break, and then it returns false. See <see cref="AimPointOn(GameWorld, ValueTuple{double, double, double}, int, int, int, out ValueTuple{int, int, int}?)"/>.
    /// </summary>
    public async Task<bool> DigAsync(int x, int y, int z, CancellationToken cancellationToken = default)
    {
        for (var cleared = 0; ; cleared++)
        {
            var state = World.GetState(x, y, z);
            if (state <= 0 || World.Data.IsAir(state) || World.Data.Hardness[state] < 0) return false;
            if (DistanceToBlock(x, y, z) > BlockReach) return false;
            if (AimPointOn(World, EyePosition, x, y, z, out var inTheWay) is { } aim) return await DigSeenAsync(x, y, z, aim, cancellationToken);
            // Eight at most: a look as long as his reach, cornerwise through solid rock, crosses seven blocks. (Four was too few: an ore within reach down and across through rock was left, and he went looking elsewhere.)
            if (inTheWay is not var (wx, wy, wz) || cleared >= 8 || WhyNotClearFromTheWay(wx, wy, wz) != null)
            {
                // Why it stays, in the line: four such lines in two hours of his play said only "left alone" (2026-11-17), and whether that was lava, a build or eight blocks cleared could not be told.
                var what = inTheWay is var (ix, iy, iz)
                    ? $"{World.GetBlock(ix, iy, iz)?.Name ?? "something"} at {ix} {iy} {iz} is in the way{(cleared >= 8 ? ", after clearing 8" : WhyNotClearFromTheWay(ix, iy, iz) is { } why ? " and " + why : "")}"
                    : "nothing I can look at it past";
                DigNote?.Invoke($"dig: {World.Data.BlockOf(state).Name} at {x} {y} {z}: not in view from here ({what}), left alone");
                return false;
            }
            // What is in the way is by its nature in view: his look meets it first.
            if (AimPointOn(World, EyePosition, wx, wy, wz, out _) is not { } front || !await DigSeenAsync(wx, wy, wz, front, cancellationToken)) return false;
        }
    }

    /// <summary>
    /// Why he may not break this to get at what is behind it, or null when he may. Not what someone built or what stands by it, not what will not break or did not a moment ago, and not a block
    /// with lava against it.
    /// </summary>
    private string? WhyNotClearFromTheWay(int x, int y, int z)
    {
        var s = World.GetState(x, y, z);
        if (s <= 0) return "is not a block I know";
        if (World.Data.Hardness[s] < 0) return "cannot be broken";
        if (IsBuiltBlock(World.Data.BlockOf(s))) return "is something built";
        if (PartOfABuild(x, y, z)) return "is part of a build";
        if (DigRefused(x, y, z)) return "would not break a moment ago";
        foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
            if (World.GetState(x + dx, y + dy, z + dz) is var n && n > 0 && (World.Data.StateFlags[n] & GameData.FlagLava) != 0) return "has lava against it";
        return null;
    }

    /// <summary>
    /// Breaks the block he is looking at, like the vanilla client: look at it, START_DESTROY, accumulate getDestroyProgress every
    /// tick (it changes if we fall, swap tools or get effects), STOP_DESTROY at 1.0, then wait for the server to
    /// confirm the block is gone. Returns false if it could not be broken (out of reach, unbreakable, refused).
    /// </summary>
    private async Task<bool> DigSeenAsync(int x, int y, int z, (double X, double Y, double Z) aim, CancellationToken cancellationToken)
    {
        var state = World.GetState(x, y, z);
        if (state <= 0 || World.Data.IsAir(state) || World.Data.Hardness[state] < 0) return false;
        if (DistanceToBlock(x, y, z) > BlockReach) return false;
        var face = FaceToward(x, y, z);
        LookAt(aim.X, aim.Y, aim.Z);
        var started = DateTime.UtcNow;
        var expected = TicksToBreak(state);
        var tool = Inventory.MainHand.IsEmpty ? "bare hands" : Inventory.MainHand.Name;

        void Note(string outcome)
        {
            var took = (DateTime.UtcNow - started).TotalSeconds;
            if (outcome != "broken" || took > 2)
                DigNote?.Invoke($"dig: {World.Data.BlockOf(state).Name} at {x} {y} {z} with {tool}: {took:0.0} s, expected {expected} ticks at the start, on the ground {OnGround}, head in water {EyeInWater()}: {outcome}");
        }

        await SendActionAsync(0, x, y, z, face, cancellationToken); // START_DESTROY_BLOCK
        await SwingAsync(Hand.Main, cancellationToken);
        var progress = DestroyProgressPerTick(state);
        var ticks = 0;
        while (progress < 1.0f)
        {
            await WaitTicksAsync(1, cancellationToken);
            var now = World.GetState(x, y, z);
            if (now != state)
            {
                if (World.Data.IsAir(Math.Max(0, now))) { Note("broken"); return true; } // broken by something else
                await SendActionAsync(1, x, y, z, face, cancellationToken); // ABORT
                Note("it changed into something else");
                return false;
            }
            if (DistanceToBlock(x, y, z) > BlockReach + 0.5)
            {
                await SendActionAsync(1, x, y, z, face, cancellationToken);
                Note("out of reach");
                return false;
            }
            // Past any block he should be spending this on: floating in water it is 25 times as slow, and stone by hand 187 seconds. There is always another block or a better place to stand (TJ: "he swings for a LONG time on a block", 2026-11-04).
            if (++ticks > MaxDigTicks)
            {
                await SendActionAsync(1, x, y, z, face, cancellationToken);
                Note($"gave up at {progress:P0}");
                NoteRefusedDig(x, y, z);
                return false;
            }
            LookAt(aim.X, aim.Y, aim.Z);
            progress += DestroyProgressPerTick(state);
            await SwingAsync(Hand.Main, cancellationToken);
        }
        if (World.Data.Hardness[state] > 0 && DestroyProgressPerTick(state) < 1.0f)
            await SendActionAsync(2, x, y, z, face, cancellationToken); // STOP_DESTROY_BLOCK (finish)
        var gone = await WaitUntilAsync(() => { var s = World.GetState(x, y, z); return s >= 0 && World.Data.IsAir(s) || s >= 0 && World.Data.BlockOf(s).Name != World.Data.BlockOf(state).Name; }, 20, cancellationToken);
        Note(gone ? "broken" : "the server did not break it");
        if (!gone) NoteRefusedDig(x, y, z);
        return gone;
    }

    /// <summary>The longest he spends on one block.</summary>
    public const int MaxDigTicks = 400;

    /// <summary>A dig that went wrong or took over 2 seconds, in words: what it was, with what, how long, and how he stood. So that "he swings for a long time" has an answer in the log.</summary>
    public Action<string>? DigNote { get; set; }

    private readonly Dictionary<(int X, int Y, int Z), DateTime> _refusedDigs = [];

    private void NoteRefusedDig(int x, int y, int z) { lock (_refusedDigs) _refusedDigs[(x, y, z)] = DateTime.UtcNow; }

    /// <summary>Did this block not come away when he tried, in the last 5 minutes? Then a plan should not count on it: the same path planned again means the same swinging, a dozen times over.</summary>
    public bool DigRefused(int x, int y, int z)
    {
        lock (_refusedDigs) return _refusedDigs.TryGetValue((x, y, z), out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(5);
    }

    private async Task SendActionAsync(int status, int x, int y, int z, BlockFace face, CancellationToken ct)
    {
        using var p = new PacketWriter(CB.Play.Serverbound.PlayerAction);
        p.WriteVarInt(status).WritePosition(x, y, z).WriteByte((byte)face).WriteVarInt(NextSequence());
        await SendAsync(p, ct);
    }

    public async Task SwingAsync(Hand hand = Hand.Main, CancellationToken cancellationToken = default)
    {
        using var p = new PacketWriter(CB.Play.Serverbound.Swing);
        p.WriteVarInt((int)hand);
        await SendAsync(p, cancellationToken);
    }

    // ---------------------------------------------------------------- placing and using

    /// <summary>
    /// Right-clicks a block face (vanilla ServerboundUseItemOnPacket): places the held block against it, or uses
    /// the block (opens a chest, presses a button...). cursor = where on the face (0..1), default its centre.
    /// </summary>
    public async Task UseOnBlockAsync(int x, int y, int z, BlockFace face, Hand hand = Hand.Main, (float X, float Y, float Z)? cursor = null, CancellationToken cancellationToken = default)
    {
        var c = cursor ?? face switch
        {
            BlockFace.Down => (0.5f, 0f, 0.5f),
            BlockFace.Up => (0.5f, 1f, 0.5f),
            BlockFace.North => (0.5f, 0.5f, 0f),
            BlockFace.South => (0.5f, 0.5f, 1f),
            BlockFace.West => (0f, 0.5f, 0.5f),
            _ => (1f, 0.5f, 0.5f),
        };
        LookAt(x + c.Item1, y + c.Item2, z + c.Item3);
        using var p = new PacketWriter(CB.Play.Serverbound.UseItemOn);
        p.WriteVarInt((int)hand).WritePosition(x, y, z).WriteVarInt((int)face)
            .WriteFloat(c.Item1).WriteFloat(c.Item2).WriteFloat(c.Item3)
            .WriteBool(false)   // inside block
            .WriteBool(false)   // world border hit
            .WriteVarInt(NextSequence());
        await SendAsync(p, cancellationToken);
        await SwingAsync(hand, cancellationToken);
    }

    public static (int X, int Y, int Z) Offset((int X, int Y, int Z) p, BlockFace face) => face switch
    {
        BlockFace.Down => (p.X, p.Y - 1, p.Z),
        BlockFace.Up => (p.X, p.Y + 1, p.Z),
        BlockFace.North => (p.X, p.Y, p.Z - 1),
        BlockFace.South => (p.X, p.Y, p.Z + 1),
        BlockFace.West => (p.X - 1, p.Y, p.Z),
        _ => (p.X + 1, p.Y, p.Z),
    };

    public static BlockFace Opposite(BlockFace f) => f switch
    {
        BlockFace.Down => BlockFace.Up, BlockFace.Up => BlockFace.Down,
        BlockFace.North => BlockFace.South, BlockFace.South => BlockFace.North,
        BlockFace.West => BlockFace.East, _ => BlockFace.West,
    };

    /// <summary>
    /// Places the held block AT x,y,z by clicking a solid neighbour's face. Returns false if there is nothing to
    /// place against, the spot is occupied, or the server refused.
    /// </summary>
    private static readonly string[] InteractiveParts =
    [
        "chest", "barrel", "crafting_table", "furnace", "smoker", "door", "fence_gate", "_bed", "lever", "_button", "anvil",
        "enchanting_table", "brewing_stand", "hopper", "dispenser", "dropper", "shulker_box", "loom", "stonecutter", "grindstone",
        "cartography_table", "smithing_table", "lectern", "note_block", "repeater", "comparator", "daylight_detector", "bell",
        "cake", "beacon", "jukebox", "crafter", "_sign", "composter", "cauldron", "flower_pot", "respawn_anchor", "campfire",
        "decorated_pot", "chiseled_bookshelf", "lodestone", "command_block", "structure_block", "jigsaw",
    ];

    /// <summary>Blocks that do something when right-clicked (open, toggle, edit): clicking one does not place against it.</summary>
    private bool Interactive(int state) => World.Data.BlockOf(state).Name is var n && InteractiveParts.Any(n.Contains)
        || World.Data.IsUnknownBlock(state) && World.Data.Learned(state)?.UseDoesNothing != true; // a modded block could be a door or a chest

    public async Task<bool> PlaceBlockAsync(int x, int y, int z, CancellationToken cancellationToken = default)
    {
        var here = World.GetState(x, y, z);
        if (here < 0 || !(World.Data.IsAir(here) || (World.Data.StateFlags[here] & GameData.FlagReplaceable) != 0)) return false;
        if (Inventory.MainHand.Type?.Block == null) return false;
        // Prefer the neighbour face we can see best (below first: the most natural click).
        var (ex, ey, ez) = EyePosition;
        var candidates = new[] { BlockFace.Down, BlockFace.North, BlockFace.South, BlockFace.West, BlockFace.East, BlockFace.Up }
            .Select(f => (Face: f, N: Offset((x, y, z), f)))
            .Where(c => { var s = World.GetState(c.N.X, c.N.Y, c.N.Z); return s > 0 && World.Data.HasCollision(s) && (World.Data.StateFlags[s] & GameData.FlagReplaceable) == 0 && !Interactive(s); })
            .OrderBy(c => Math.Pow(c.N.X + 0.5 - ex, 2) + Math.Pow(c.N.Y + 0.5 - ey, 2) + Math.Pow(c.N.Z + 0.5 - ez, 2))
            .ToList();
        if (candidates.Count == 0) return false;
        var (face, n) = candidates[0];
        if (DistanceToBlock(n.X, n.Y, n.Z) > BlockReach) return false;
        return await PlaceAgainstAsync(n.X, n.Y, n.Z, Opposite(face), cancellationToken);
    }

    /// <summary>
    /// Clicks the given face of block x,y,z with the held block and waits until a block appears in the cell in front
    /// of that face. The held stack then shrinks by one at once, as vanilla's client predicts it (the server's own
    /// slot update follows a tick later; without the prediction the next click goes out with a stack the server no
    /// longer has, and nothing is placed).
    /// </summary>
    public async Task<bool> PlaceAgainstAsync(int x, int y, int z, BlockFace face, CancellationToken cancellationToken = default)
    {
        var (tx, ty, tz) = Offset((x, y, z), face);
        var before = World.GetState(tx, ty, tz);
        var slot = PlayerInventory.HotbarStart + Inventory.SelectedHotbar;
        var held = Inventory[slot];
        var aboveBefore = World.GetRawState(tx, ty + 1, tz);
        await UseOnBlockAsync(x, y, z, face, Hand.Main, null, cancellationToken);
        if (!await WaitUntilAsync(() => { var s = World.GetState(tx, ty, tz); return s > 0 && !World.Data.IsAir(s) && s != before; }, 20, cancellationToken))
            return false;
        if (!InstaBuild) Inventory.PredictOneUsed(slot, held);
        LearnPlaced(held, tx, ty, tz, aboveBefore);
        // His own work is his to take down. A bed or a door is two blocks, and he clicked for one.
        NotePlacedByMe(tx, ty, tz);
        var placed = World.GetState(tx, ty, tz);
        foreach (var f in new[] { BlockFace.Down, BlockFace.Up, BlockFace.North, BlockFace.South, BlockFace.West, BlockFace.East })
        {
            var other = Offset((tx, ty, tz), f);
            var s = World.GetState(other.X, other.Y, other.Z);
            if (s > 0 && placed > 0 && World.Data.StateBlock[s] == World.Data.StateBlock[placed] && World.Data.BlockOf(placed).Name is var name && (name.EndsWith("_bed") || name.EndsWith("_door"))) NotePlacedByMe(other.X, other.Y, other.Z);
        }
        return true;
    }

    /// <summary>Uses the held item in the air (eat, drink, throw, draw a bow...). Releasing is UseRelease.</summary>
    public async Task UseItemAsync(Hand hand = Hand.Main, CancellationToken cancellationToken = default)
    {
        var (yaw, pitch) = Rotation;
        using var p = new PacketWriter(CB.Play.Serverbound.UseItem);
        p.WriteVarInt((int)hand).WriteVarInt(NextSequence()).WriteFloat(yaw).WriteFloat(pitch);
        await SendAsync(p, cancellationToken);
    }

    /// <summary>Stops using the held item (shoots a drawn bow, cancels eating).</summary>
    public async Task ReleaseUseItemAsync(CancellationToken cancellationToken = default)
    {
        using var p = new PacketWriter(CB.Play.Serverbound.PlayerAction);
        p.WriteVarInt(5).WritePosition(0, 0, 0).WriteByte(0).WriteVarInt(0); // RELEASE_USE_ITEM
        await SendAsync(p, cancellationToken);
    }

    /// <summary>Eats the held food until the server consumes it (food changes or the stack shrinks).</summary>
    public async Task<bool> EatAsync(CancellationToken cancellationToken = default)
    {
        var held = Inventory.MainHand;
        if (held.Type is not { IsFood: true } && !held.Components.ContainsKey("food")) return false;
        var count = held.Count;
        var food = Food;
        await UseItemAsync(Hand.Main, cancellationToken);
        // 32 ticks for normal food (consumable component); wait a little longer for the server's word.
        return await WaitUntilAsync(() => Food != food || Inventory.MainHand.Count != count || Inventory.MainHand.Name != held.Name, 60, cancellationToken);
    }

    // ---------------------------------------------------------------- entities

    /// <summary>Could a player standing here hit this creature: is there a straight line from his eyes to its body (middle, head or feet) with no block's shape in it? As with blocks, the server does not ask; a player's crosshair does.</summary>
    public bool HasClearLineTo(Entity target)
    {
        var eye = EyePosition;
        var h = target.Type.Height;
        foreach (var part in (ReadOnlySpan<double>)[0.5, 0.9, 0.15])
            if (FirstShapeOnTheWay(World, eye, (target.X, target.Y + h * part, target.Z), int.MinValue, 0, 0) == null) return true;
        return false;
    }

    /// <summary>Attacks an entity (vanilla: interact ATTACK then swing). Mind AttackCooldownTicks for full damage. Nothing happens when a block is between them: a sword does not go through a wall.</summary>
    public async Task AttackAsync(Entity target, CancellationToken cancellationToken = default)
    {
        if (!HasClearLineTo(target)) return;
        LookAt(target.X, target.Y + target.Type.Height * 0.5, target.Z);
        using (var p = new PacketWriter(CB.Play.Serverbound.Interact))
        {
            p.WriteVarInt(target.Id).WriteVarInt(1).WriteBool(Controls.Sneak);
            await SendAsync(p, cancellationToken);
        }
        await SwingAsync(Hand.Main, cancellationToken);
        _lastAttackTick = Ticks;
    }

    private long _lastAttackTick = -1000;
    private long _lastHeldChangeTick = -1000;

    /// <summary>Ticks for a fully charged attack with the held item (20 / attack_speed).</summary>
    public int AttackCooldownTicks => (int)Math.Ceiling(20.0 / (Attribute("attack_speed") ?? 4.0));

    /// <summary>
    /// True when an attack now would do full damage ON THE SERVER. The server restarts the charge when it
    /// processes an attack or a change of held item, a tick or two after we send it, and its ticks are not in
    /// phase with ours (measured: a hit 13 client ticks after a hotbar change landed at 92% charge). So count from
    /// the later of the two plus a margin for the round trip.
    /// </summary>
    public bool AttackReady => Ticks - Math.Max(_lastAttackTick, _lastHeldChangeTick) >= AttackCooldownTicks + AttackLatencyMarginTicks;

    /// <summary>Ticks until AttackReady (0 when ready).</summary>
    public int TicksUntilAttackReady => (int)Math.Max(0, AttackCooldownTicks + AttackLatencyMarginTicks - (Ticks - Math.Max(_lastAttackTick, _lastHeldChangeTick)));

    /// <summary>
    /// A critical hit (vanilla Player.attack: x1.5 damage when fully charged, falling - the server's fallDistance
    /// &gt; 0 - not on the ground, not climbing, not in water, not sprinting). Jumps, waits until the server has heard
    /// us moving down, and hits at full charge; on the ground again first, it hits normally. Returns true when the
    /// hit went out mid-fall.
    /// </summary>
    public async Task<bool> CritAttackAsync(Entity target, CancellationToken cancellationToken = default)
    {
        var body = BodySnapshot();
        if (!body.OnGround || body.InWater || body.OnClimbable)
        {
            await AttackAsync(target, cancellationToken);
            return false;
        }
        Controls.Sprint = false; // sprinting turns a crit into a knockback hit
        Controls.Jump = true;
        await WaitTicksAsync(1, cancellationToken);
        Controls.Jump = false;
        // MEASURED on Paper 1.21.5: a hit right after the FIRST downward step (VelY -0.15) lands as a plain hit;
        // after the second (VelY -0.23) it is a critical. So wait for two steps down.
        await WaitUntilAsync(() => { var b = BodySnapshot(); return b.OnGround || b.VelY < -0.2; }, 16, cancellationToken);
        await WaitUntilAsync(() => AttackReady || BodySnapshot().OnGround, 12, cancellationToken);
        var crit = !BodySnapshot().OnGround && AttackReady;
        LookAt(target.X, target.Y + target.Type.Height * 0.5, target.Z);
        await AttackAsync(target, cancellationToken);
        return crit;
    }

    /// <summary>Extra ticks AttackReady waits for the server to catch up (2 + round-trip time in ticks).</summary>
    public int AttackLatencyMarginTicks => 2 + (Players.TryGetValue(Profile.Id, out var me) ? (me.LatencyMs + 49) / 50 : 1);

    /// <summary>Right-clicks an entity (trade with a villager, feed an animal, sit a dog).</summary>
    public async Task InteractAsync(Entity target, Hand hand = Hand.Main, CancellationToken cancellationToken = default)
    {
        LookAt(target.X, target.Y + target.Type.Height * 0.5, target.Z);
        using var p = new PacketWriter(CB.Play.Serverbound.Interact);
        p.WriteVarInt(target.Id).WriteVarInt(0).WriteVarInt((int)hand).WriteBool(Controls.Sneak);
        await SendAsync(p, cancellationToken);
        await SwingAsync(hand, cancellationToken);
    }

    // ---------------------------------------------------------------- effects

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _effects = new();

    /// <summary>Active effects on us by name ("minecraft:haste") with amplifier (0 = level I).</summary>
    public IReadOnlyDictionary<string, int> Effects => _effects;
}
