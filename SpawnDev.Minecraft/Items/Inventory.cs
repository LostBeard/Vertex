namespace SpawnDev.Minecraft.Items;

/// <summary>
/// The player's own inventory in container-0 slot numbering (what the server's packets use):
/// 0 crafting result, 1-4 crafting grid, 5-8 armor (head, chest, legs, feet), 9-35 main, 36-44 hotbar, 45 off hand.
/// </summary>
public sealed class PlayerInventory
{
    public const int Size = 46;
    public const int HotbarStart = 36;
    public const int OffHand = 45;
    public const int MainStart = 9;

    private readonly ItemStack[] _slots = Enumerable.Repeat(ItemStack.Empty, Size).ToArray();

    public ItemStack this[int slot] => _slots[slot];
    public IReadOnlyList<ItemStack> Slots => _slots;

    /// <summary>Selected hotbar index 0-8.</summary>
    public int SelectedHotbar { get; internal set; }
    public ItemStack MainHand => _slots[HotbarStart + SelectedHotbar];
    public ItemStack OffHandItem => _slots[OffHand];
    public ItemStack Head => _slots[5];
    public ItemStack Chest => _slots[6];
    public ItemStack Legs => _slots[7];
    public ItemStack Feet => _slots[8];

    /// <summary>The stack held on the mouse cursor (while a container is open).</summary>
    public ItemStack Cursor { get; internal set; } = ItemStack.Empty;

    internal void Set(int slot, ItemStack stack)
    {
        if ((uint)slot < Size) _slots[slot] = stack;
    }

    /// <summary>
    /// Vanilla's client shrinks the held stack itself when it places a block (BlockItem.useOn runs on both sides);
    /// the server's own slot update comes a tick later. Without this, a build that empties a stack clicks again
    /// with the old count, holding nothing on the server, and that block is never placed. Only if the slot still
    /// holds exactly what it held before the click: an update from the server that already arrived wins.
    /// </summary>
    internal void PredictOneUsed(int slot, ItemStack before)
    {
        if ((uint)slot >= Size || before.IsEmpty) return;
        var after = before.WithCount(before.Count - 1);
        Interlocked.CompareExchange(ref _slots[slot], after, before);
    }

    /// <summary>Total count of an item (by name) in main inventory, hotbar and off hand.</summary>
    public int Count(string itemName)
    {
        itemName = Data.GameData.Normalize(itemName);
        var total = 0;
        for (var i = MainStart; i < Size; i++)
            if (_slots[i].Name == itemName) total += _slots[i].Count;
        return total;
    }

    /// <summary>Slots (9-45) holding items that match.</summary>
    public IEnumerable<int> Find(Func<ItemStack, bool> match)
    {
        for (var i = MainStart; i < Size; i++)
            if (!_slots[i].IsEmpty && match(_slots[i])) yield return i;
    }

    public int? FirstEmpty(bool hotbarFirst = false)
    {
        if (hotbarFirst)
            for (var i = HotbarStart; i < HotbarStart + 9; i++)
                if (_slots[i].IsEmpty) return i;
        for (var i = MainStart; i < HotbarStart + 9; i++)
            if (_slots[i].IsEmpty) return i;
        return null;
    }

    /// <summary>
    /// Maps set_player_inventory's Inventory index (0-8 hotbar, 9-35 main, 36-39 armor feet..head, 40 off hand)
    /// to container-0 slots.
    /// </summary>
    internal static int FromInventoryIndex(int index) => index switch
    {
        < 9 => HotbarStart + index,
        < 36 => index,
        < 40 => 8 - (index - 36),
        40 => OffHand,
        _ => -1,
    };

    public override string ToString() => string.Join(", ", Enumerable.Range(MainStart, Size - MainStart)
        .Where(i => !_slots[i].IsEmpty).Select(i => $"{i}:{_slots[i]}"));
}

/// <summary>A container screen the server opened (chest, furnace, crafting table...).</summary>
public sealed class ContainerWindow
{
    public ContainerWindow(int id, int menuType, string title)
    {
        Id = id;
        MenuType = menuType;
        Title = title;
    }

    public int Id { get; }
    /// <summary>The minecraft:menu registry id (e.g. generic_9x3 for a chest).</summary>
    public int MenuType { get; }
    public string Title { get; }
    public int StateId { get; internal set; }
    public ItemStack[] Slots { get; internal set; } = [];

    /// <summary>The trades on offer when this is a merchant's window (empty otherwise, and until the server sends them).</summary>
    public IReadOnlyList<Trade> Trades { get; internal set; } = [];

    /// <summary>A merchant's level (1 novice to 5 master) and the experience it has toward the next one.</summary>
    public int MerchantLevel { get; internal set; }
    public int MerchantXp { get; internal set; }
    public bool MerchantCanRestock { get; internal set; }

    /// <summary>Slots that belong to the container itself (the rest are the player's 36 inventory slots).</summary>
    public int ContainerSlotCount => Math.Max(0, Slots.Length - 36);

    public override string ToString() => $"{Title} (window {Id}, {ContainerSlotCount} slots)";
}
