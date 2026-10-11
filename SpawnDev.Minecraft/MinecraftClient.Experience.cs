using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Items;

namespace SpawnDev.Minecraft;

/// <summary>What right-clicking a block did: the states before and after (equal: nothing happened), and the block above.</summary>
public sealed record UseProbe(int RawState, int TurnedInto, int? UpperRawState = null, int? UpperTurnedInto = null)
{
    public bool Changed => TurnedInto != RawState;
}

/// <summary>What one break taught: the state, how many ticks the server took, with what in hand, and what dropped.</summary>
public sealed record BreakProbe(int RawState, int Ticks, string? Tool, IReadOnlyList<string> Drops);

// Learning about a block the way a player does when touching is not enough: right-click it and see what happens,
// break it and see how long it takes and what drops, place one and see what appears. The server decides every
// outcome, so whatever is learned is what this server really does.
public sealed partial class MinecraftClient
{
    // Items whose right-click on a block does something to it (till, strip, pour, light, plant, spawn...).
    private static readonly string[] UsesOnBlocks =
    [
        "_axe", "_shovel", "_hoe", "bucket", "flint_and_steel", "fire_charge", "bone_meal", "_spawn_egg", "shears",
        "brush", "_seeds", "lead", "name_tag", "potion", "honeycomb", "ender_eye", "end_crystal", "minecart", "_boat",
        "_raft", "armor_stand", "item_frame", "painting", "debug_stick", "written_book", "writable_book", "filled_map",
    ];

    /// <summary>
    /// An item a right-click on a block cannot do anything with: nothing at all, or a vanilla item that is no block
    /// and has no use on blocks (a stick, food, an ingot). A modded item could do anything, so it does not count.
    /// </summary>
    public static bool IsInert(ItemStack s) => s.IsEmpty
        || s.Type is { Block: null } t && GameData.Vanilla.Item(t.Name) is { Block: null } && !UsesOnBlocks.Any(t.Name.Contains);

    /// <summary>
    /// Holds nothing that a right-click could place or use: an empty hotbar slot if there is one, else an inert
    /// item already on the hotbar. False when every hotbar slot holds something that would act on the block.
    /// </summary>
    public async Task<bool> HoldInertAsync(CancellationToken ct = default)
    {
        if (IsInert(Inventory.MainHand)) return true;
        foreach (var pick in new Func<ItemStack, bool>[] { s => s.IsEmpty, IsInert })
            for (var i = 0; i < 9; i++)
                if (pick(Inventory[PlayerInventory.HotbarStart + i]))
                {
                    await SelectHotbarAsync(i, ct);
                    return true;
                }
        return false;
    }

    /// <summary>
    /// Right-clicks the block at x,y,z holding nothing that acts on it, and watches what the server changes there
    /// and right above (a door's upper half switches with it). Closes anything it opened (a chest). Null when it
    /// could not try (out of reach, nothing inert to hold).
    /// </summary>
    public async Task<UseProbe?> TryUseAsync(int x, int y, int z, CancellationToken ct = default)
    {
        if (DistanceToBlock(x, y, z) > BlockReach - 0.25 || !await HoldInertAsync(ct)) return null;
        var raw = World.GetRawState(x, y, z);
        var up = World.GetRawState(x, y + 1, z);
        var window = OpenWindow;
        Controls.Clear();
        await UseOnBlockAsync(x, y, z, FaceToward(x, y, z), Hand.Main, null, ct);
        var changed = await WaitUntilAsync(() => World.GetRawState(x, y, z) != raw, 10, ct);
        await WaitTicksAsync(1, ct); // the other half's update is sent in the same tick
        if (OpenWindow != null && OpenWindow != window) await CloseWindowAsync(ct);
        if (!changed) return new UseProbe(raw, raw);
        var upNow = World.GetRawState(x, y + 1, z);
        return upNow != up && up >= World.Data.KnownStateCount && upNow >= World.Data.KnownStateCount
            ? new UseProbe(raw, World.GetRawState(x, y, z), up, upNow)
            : new UseProbe(raw, World.GetRawState(x, y, z));
    }

    /// <summary>
    /// The server's unknown blocks that its block names or tags say open by hand: doors and fence gates. Names do
    /// not say which state ids are theirs, but they say whether a door-shaped unknown block is worth a right-click.
    /// </summary>
    public IReadOnlyList<string> UnknownDoorNames()
    {
        if (!_fabricRegistries.TryGetValue("minecraft:block", out var blocks)) return [];
        var tagged = Tag("minecraft:block", "minecraft:wooden_doors").Concat(Tag("minecraft:block", "minecraft:fence_gates")).ToHashSet();
        return UnknownModdedBlocks.Where(n => n.EndsWith("_door") && !n.EndsWith("trapdoor") || n.EndsWith("_gate")
            || blocks.TryGetValue(n, out var id) && tagged.Contains(id)).ToList();
    }

    /// <summary>Thin in one direction across a whole block in the other: a door's or gate's closed plate.</summary>
    public static bool LooksLikeDoor(Box[]? shape) => shape is [var b]
        && Math.Min(b.MaxX - b.MinX, b.MaxZ - b.MinZ) <= 0.25 + 1e-9 && Math.Max(b.MaxX - b.MinX, b.MaxZ - b.MinZ) >= 0.9 && b.MaxY >= 0.9;

    /// <summary>Whether a body (0.6 wide, in the middle of the block) gets through a shape moving along x (or z).</summary>
    private static bool LetsThrough(Box[] shape, bool alongX) => shape.All(b => b.MaxY <= 1e-9
        || (alongX ? b.MinZ >= 0.8 - 1e-9 || b.MaxZ <= 0.2 + 1e-9 : b.MinX >= 0.8 - 1e-9 || b.MaxX <= 0.2 + 1e-9));

    /// <summary>
    /// Finds out what an unknown block does when right-clicked, and puts it back the way it was. A door or gate
    /// (one state blocks the way, the state a click makes lets you through) is learned as a doorway, both halves,
    /// so pathing opens it and walks through from then on. Feels the block's shapes first if needed (one before,
    /// one after the click). Returns the use probe, or null when it could not try.
    /// </summary>
    public async Task<UseProbe?> LearnUseAsync(int x, int y, int z, CancellationToken ct = default)
    {
        var raw = World.GetRawState(x, y, z);
        if (raw < World.Data.KnownStateCount) return null;
        var before = World.Data.Learned(raw)?.Shape;
        if (before == null)
        {
            if (await MeasureBlockAsync(x, y, z, ct) is not { } probe) return null;
            Learn(probe);
            before = probe.Shape;
        }
        PhysicsEnabled = true;
        if (DistanceToBlock(x, y, z) > BlockReach - 0.25
            && !await GoToAsync(new Pathing.GoalReachBlock(x, y, z, BlockReach - 0.5), new Pathing.PathOptions { MaxNodes = 10_000 }, ct)) return null;
        var use = await TryUseAsync(x, y, z, ct);
        if (use == null) return null;
        if (!use.Changed)
        {
            Learn((raw, new LearnedState { UseDoesNothing = true }));
            return use;
        }
        try
        {
            var after = World.Data.Learned(use.TurnedInto)?.Shape;
            if (after == null && await MeasureBlockAsync(x, y, z, ct) is { } probe && probe.RawState == use.TurnedInto)
            {
                Learn(probe);
                after = probe.Shape;
            }
            var facts = new List<(int, LearnedState)>();
            // Which way it lets you through: across its thin closed plate, else whichever way works.
            bool? alongX = null;
            if (after != null)
                foreach (var ax in new[] { true, false })
                {
                    var closed = LetsThrough(before, ax) ? after : before;
                    var open = ReferenceEquals(closed, before) ? after : before;
                    if (!LetsThrough(closed, ax) && LetsThrough(open, ax) && (alongX == null || IsThinAlong(closed, ax))) alongX = ax;
                }
            if (alongX is bool along)
            {
                var openAfter = LetsThrough(after!, along);
                facts.Add((raw, new LearnedState { UseTurnsInto = use.TurnedInto, Doorway = true, Open = !openAfter, AlongX = along }));
                facts.Add((use.TurnedInto, new LearnedState { UseTurnsInto = raw, Doorway = true, Open = openAfter, AlongX = along }));
                if (use.UpperRawState is int u && use.UpperTurnedInto is int ut)
                {
                    facts.Add((u, new LearnedState { UseTurnsInto = ut, Doorway = true, Open = !openAfter, AlongX = along, Shape = World.Data.Learned(u)?.Shape ?? before }));
                    facts.Add((ut, new LearnedState { UseTurnsInto = u, Doorway = true, Open = openAfter, AlongX = along, Shape = World.Data.Learned(ut)?.Shape ?? after }));
                }
            }
            else
            {
                facts.Add((raw, new LearnedState { UseTurnsInto = use.TurnedInto }));
                if (use.UpperRawState is int u && use.UpperTurnedInto is int ut) facts.Add((u, new LearnedState { UseTurnsInto = ut }));
            }
            Learn([.. facts]);
            return use;
        }
        finally
        {
            // Leave it as we found it (a lever or a mod's machine may take a few clicks to come back around).
            for (var i = 0; i < 4 && World.GetRawState(x, y, z) != raw; i++)
            {
                PhysicsEnabled = true;
                if (DistanceToBlock(x, y, z) > BlockReach - 0.25)
                    await GoToAsync(new Pathing.GoalReachBlock(x, y, z, BlockReach - 0.5), new Pathing.PathOptions { MaxNodes = 10_000 }, CancellationToken.None);
                if (await TryUseAsync(x, y, z, CancellationToken.None) is not { Changed: true }) break;
            }
            PhysicsEnabled = true;
        }
    }

    private static bool IsThinAlong(Box[] shape, bool alongX) => shape is [var b] && (alongX ? b.MaxX - b.MinX : b.MaxZ - b.MinZ) <= 0.25 + 1e-9;

    /// <summary>
    /// Breaks the block at x,y,z with the server as the clock: START_DESTROY then STOP_DESTROY at once. Vanilla's
    /// server (ServerPlayerGameMode) answers an early stop with a "delayed destroy": it keeps the dig going itself
    /// and breaks the block on the tick its progress reaches 1, the same time a player holding the button needs.
    /// Counts the ticks until the block is gone and notes what drops at it. Null when it could not (out of reach,
    /// refused: the block comes back, or never went).
    /// </summary>
    public async Task<BreakProbe?> TimeBreakAsync(int x, int y, int z, int maxTicks = 600, CancellationToken ct = default)
    {
        var raw = World.GetRawState(x, y, z);
        if (raw <= 0 || World.Data.IsAir(World.GetState(x, y, z)) || DistanceToBlock(x, y, z) > BlockReach) return null;
        var tool = Inventory.MainHand is { IsEmpty: false } held && held.Type?.ToolRules.Length > 0 ? held.Name : null;
        var face = FaceToward(x, y, z);
        LookAt(x + 0.5, y + 0.5, z + 0.5);
        var drops = new List<string>();
        void OnSpawn(Entities.Entity e) { if (e.IsItem) _pendingDrops.Add(e); }
        _pendingDrops.Clear();
        EntitySpawned += OnSpawn;
        try
        {
            var start = Ticks;
            await SendActionAsync(0, x, y, z, face, ct); // START_DESTROY_BLOCK
            await SwingAsync(Hand.Main, ct);
            await SendActionAsync(2, x, y, z, face, ct); // STOP_DESTROY_BLOCK at once: the server finishes it
            if (!await WaitUntilAsync(() => World.GetRawState(x, y, z) != raw, maxTicks, ct))
            {
                await SendActionAsync(1, x, y, z, face, ct); // ABORT: never mind
                return null;
            }
            var ticks = (int)(Ticks - start);
            // Drops spawn with the break; their item arrives in entity data a moment later.
            await WaitTicksAsync(4, ct);
            foreach (var e in _pendingDrops.ToArray())
                if (e.Item is { IsEmpty: false } item && Math.Abs(e.X - (x + 0.5)) < 1.5 && Math.Abs(e.Y - (y + 0.5)) < 1.5 && Math.Abs(e.Z - (z + 0.5)) < 1.5)
                    drops.Add(item.Name);
            return World.Data.IsAir(Math.Max(0, World.GetState(x, y, z))) ? new BreakProbe(raw, ticks, tool, drops) : null;
        }
        finally { EntitySpawned -= OnSpawn; }
    }

    private readonly System.Collections.Concurrent.ConcurrentBag<Entities.Entity> _pendingDrops = [];

    /// <summary>
    /// Breaks an unknown block and learns from it: how long the server took (with what in hand) and, when it drops
    /// itself (a block item), its name, which brings in everything the server's tags say about that name. The
    /// caller decides the block may be broken (asked to, or nobody's build).
    /// </summary>
    public async Task<BreakProbe?> LearnByBreakingAsync(int x, int y, int z, CancellationToken ct = default)
    {
        if (await TimeBreakAsync(x, y, z, ct: ct) is not { } b) return null;
        if (b.RawState < World.Data.KnownStateCount) return b;
        var name = b.Drops.Select(d => World.Data.Item(d)?.Block).FirstOrDefault(n => n != null);
        Learn((b.RawState, new LearnedState { DigTicks = b.Ticks, DigTool = b.Tool, Name = name }));
        return b;
    }

    /// <summary>
    /// After placing a block item: the state it made at x,y,z (and right above, for a door's upper half) is that
    /// block. A modded block we had no data for gets its name this way.
    /// </summary>
    private void LearnPlaced(ItemStack held, int x, int y, int z, int aboveBefore)
    {
        if (held.Type?.Block is not { } name) return;
        var known = World.Data.KnownStateCount;
        var raw = World.GetRawState(x, y, z);
        if (raw < known) return;
        var above = World.GetRawState(x, y + 1, z);
        var fact = new LearnedState { Name = name };
        Learn(above != aboveBefore && above >= known ? [(raw, fact), (above, fact)] : [(raw, fact)]);
    }
}
