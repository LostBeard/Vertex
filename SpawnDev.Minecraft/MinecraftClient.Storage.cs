using SpawnDev.Minecraft.Items;
using SpawnDev.Minecraft.Pathing;

namespace SpawnDev.Minecraft;

// Chests and barrels (store, take), and beds.
public sealed partial class MinecraftClient
{
    private static readonly string[] StorageBlocks = ["minecraft:chest", "minecraft:trapped_chest", "minecraft:barrel"];

    /// <summary>Loaded chests and barrels within maxDistance, nearest first.</summary>
    public List<(int X, int Y, int Z)> FindStorage(double maxDistance = 32, int maxCount = 16)
    {
        var ids = StorageBlocks.Select(n => World.Data.Block(n)!.Id).ToHashSet();
        return World.FindBlocks(s => s > 0 && ids.Contains(World.Data.StateBlock[s]), Position, maxDistance, maxCount);
    }

    /// <summary>Walks within reach of a container block and opens it. True once its window is open.</summary>
    public async Task<bool> OpenContainerAsync(int x, int y, int z, CancellationToken ct = default)
    {
        if (OpenWindow != null) await CloseWindowAsync(ct);
        if (DistanceToBlock(x, y, z) > BlockReach - 0.5
            && !await GoToAsync(new GoalReachBlock(x, y, z, BlockReach - 0.5), new PathOptions { CanDig = false, MaxNodes = 30_000 }, ct))
            return false;
        await UseOnBlockAsync(x, y, z, FaceToward(x, y, z), Hand.Main, null, ct);
        return await WaitUntilAsync(() => OpenWindow is { ContainerSlotCount: > 0 }, 40, ct);
    }

    /// <summary>Shift-clicks every matching stack from our inventory into the open container. Returns the items moved.</summary>
    public async Task<int> StoreAsync(Func<ItemStack, bool> match, CancellationToken ct = default)
    {
        if (OpenWindow is not { } window) return 0;
        var before = CarriedCount();
        for (var i = window.ContainerSlotCount; i < window.Slots.Length; i++)
            if (!window.Slots[i].IsEmpty && match(window.Slots[i]))
                await ClickAsync(window.Id, i, 0, 1, ct);
        await WaitTicksAsync(4, ct);
        return before - CarriedCount();
    }

    /// <summary>Shift-clicks matching stacks from the open container into our inventory until at least `max` items came over.</summary>
    public async Task<int> TakeAsync(Func<ItemStack, bool> match, int max = int.MaxValue, CancellationToken ct = default)
    {
        if (OpenWindow is not { } window) return 0;
        var before = CarriedCount();
        var queued = 0;
        for (var i = 0; i < window.ContainerSlotCount && queued < max; i++)
        {
            var s = window.Slots[i];
            if (s.IsEmpty || !match(s)) continue;
            queued += s.Count;
            await ClickAsync(window.Id, i, 0, 1, ct);
        }
        await WaitTicksAsync(4, ct);
        return CarriedCount() - before;
    }

    private int CarriedCount()
    {
        var n = 0;
        for (var i = PlayerInventory.MainStart; i < PlayerInventory.Size; i++) n += Inventory[i].Count;
        return n;
    }

    // ---------------------------------------------------------------- beds

    /// <summary>Loaded beds within maxDistance, nearest first (either half).</summary>
    public List<(int X, int Y, int Z)> FindBeds(double maxDistance = 32, int maxCount = 8)
    {
        var beds = World.Data.Blocks.Where(b => b.Name.EndsWith("_bed")).Select(b => b.Id).ToHashSet();
        return World.FindBlocks(s => s > 0 && beds.Contains(World.Data.StateBlock[s]), Position, maxDistance, maxCount);
    }

    /// <summary>
    /// Walks to a bed and lies down. Returns null once asleep, or the server's reason it refused ("You can sleep
    /// only at night and during thunderstorms", "You may not rest now; there are monsters nearby", ...).
    /// </summary>
    public async Task<string?> LieDownAsync(int x, int y, int z, CancellationToken ct = default)
    {
        // BedBlock.isReachableBed: within 3 blocks horizontally and 2 vertically.
        if (DistanceToBlock(x, y, z) > 2.5
            && !await GoToAsync(new GoalReachBlock(x, y, z, 2.5), new PathOptions { CanDig = false, MaxNodes = 30_000 }, ct))
            return "I could not get to the bed";
        // Refusals are Player.BedSleepingProblem messages (plus BedBlock's "occupied") on the action bar. Other
        // text comes too and is no refusal: "Respawn point set" in chat, and on success "Sleeping through this
        // night" on the action bar, sometimes before our sleeping pose arrives.
        string? refusal = null;
        void OnChat(ChatMessage m) { if (m.Kind == ChatKind.ActionBar && BedRefusals.Value.Contains(m.Text)) refusal ??= m.Text; }
        ChatReceived += OnChat;
        try
        {
            await UseOnBlockAsync(x, y, z, BlockFace.Up, Hand.Main, null, ct);
            await WaitUntilAsync(() => Sleeping || refusal != null, 40, ct);
        }
        finally
        {
            ChatReceived -= OnChat;
        }
        return Sleeping ? null : refusal ?? "the bed did nothing";
    }

    private static readonly Lazy<HashSet<string>> BedRefusals = new(() =>
        new[] { "no_sleep", "not_safe", "obstructed", "too_far_away", "occupied" }.Select(k => Text.TextComponent.Translate("block.minecraft.bed." + k)!).ToHashSet());

    /// <summary>Gets out of bed (the "Leave Bed" button).</summary>
    public async Task LeaveBedAsync(CancellationToken ct = default)
    {
        if (!Sleeping) return;
        await PlayerCommandAsync(2, ct); // STOP_SLEEPING
        await WaitUntilAsync(() => !Sleeping, 40, ct);
    }
}
