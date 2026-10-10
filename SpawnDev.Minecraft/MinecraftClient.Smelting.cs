using SpawnDev.Minecraft.Crafting;
using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Items;

namespace SpawnDev.Minecraft;

// Smelting in a furnace with real container clicks.
public sealed partial class MinecraftClient
{
    public static int FurnaceMenuId => Array.IndexOf(GameData.Vanilla.Registries["minecraft:menu"], "minecraft:furnace");

    /// <summary>How many items one of each fuel smelts (vanilla AbstractFurnaceBlockEntity burn times / 200).</summary>
    private static readonly (string Item, double Smelts)[] Fuels =
    [
        ("minecraft:coal", 8), ("minecraft:charcoal", 8), ("minecraft:coal_block", 80), ("minecraft:lava_bucket", 100),
        ("minecraft:blaze_rod", 12), ("minecraft:dried_kelp_block", 20),
        ("minecraft:oak_log", 1.5), ("minecraft:spruce_log", 1.5), ("minecraft:birch_log", 1.5), ("minecraft:jungle_log", 1.5),
        ("minecraft:acacia_log", 1.5), ("minecraft:dark_oak_log", 1.5), ("minecraft:mangrove_log", 1.5), ("minecraft:cherry_log", 1.5),
        ("minecraft:oak_planks", 1.5), ("minecraft:spruce_planks", 1.5), ("minecraft:birch_planks", 1.5), ("minecraft:jungle_planks", 1.5),
        ("minecraft:acacia_planks", 1.5), ("minecraft:dark_oak_planks", 1.5), ("minecraft:mangrove_planks", 1.5), ("minecraft:cherry_planks", 1.5),
        ("minecraft:stick", 0.5),
    ];

    /// <summary>
    /// Smelts items into `count` of the result (iron ingots from raw iron, glass from sand, cooked food...): finds a
    /// furnace within reach or places one (crafting it from cobblestone if needed), loads input and fuel, waits for
    /// the server to cook them (10 s each), and takes the output.
    /// </summary>
    public async Task<(bool Success, string Message)> SmeltAsync(string result, int count, CancellationToken cancellationToken = default)
    {
        result = GameData.Normalize(result);
        var recipe = RecipeBook.Vanilla.CookingRecipesFor(result).FirstOrDefault(r => r.Type == "minecraft:smelting" && Inventory.Find(s => r.Input.Matches(s.Name)).Any());
        if (recipe == null)
            return (false, RecipeBook.Vanilla.CookingRecipesFor(result).Any() ? $"I have nothing to smelt into {Pretty(result)}" : $"{Pretty(result)} is not made in a furnace");
        var input = Inventory.Slots.Skip(PlayerInventory.MainStart).First(s => !s.IsEmpty && recipe.Input.Matches(s.Name)).Name;
        count = Math.Min(count, Inventory.Count(input));
        if (!Fuels.Any(f => Inventory.Count(f.Item) > 0)) return (false, "I need fuel (coal, wood or planks) to smelt");

        // The furnace first: making one can use up planks (a crafting table) we would otherwise burn.
        if (!await OpenFurnaceAsync(cancellationToken)) return (false, "I need a furnace (and could not make or reach one)");
        // The fuel that covers the job with the least burn time wasted (keeps coal for torches when planks do).
        var fuel = Fuels.Where(f => Inventory.Count(f.Item) * f.Smelts >= count)
                .OrderBy(f => Math.Ceiling(count / f.Smelts) * f.Smelts - count).ThenByDescending(f => f.Smelts).Select(f => f.Item).FirstOrDefault()
            ?? Fuels.Where(f => Inventory.Count(f.Item) > 0).OrderByDescending(f => Inventory.Count(f.Item) * f.Smelts).Select(f => f.Item).FirstOrDefault();
        if (fuel == null)
        {
            await CloseWindowAsync(cancellationToken);
            return (false, "I need fuel (coal, wood or planks) to smelt");
        }
        var fuelPerItem = Fuels.First(f => f.Item == fuel).Smelts;
        count = Math.Min(count, (int)(Inventory.Count(fuel) * fuelPerItem));
        var fuelCount = (int)Math.Ceiling(count / fuelPerItem);

        var window = OpenWindow!;
        // Furnace window: 0 input, 1 fuel, 2 output, then the player's main (3..29) and hotbar (30..38).
        static int Local(int inventorySlot) => inventorySlot - 6;
        var before = Inventory.Count(result);
        await LoadSlotAsync(window.Id, input, count, 0, Local, cancellationToken);
        await LoadSlotAsync(window.Id, fuel, fuelCount, 1, Local, cancellationToken);

        // Each item takes the recipe's cooking time; collect as they come out.
        var deadline = Ticks + recipe.CookingTicks * count + 100;
        while (Inventory.Count(result) < before + count && Ticks < deadline)
        {
            await WaitTicksAsync(20, cancellationToken);
            if (OpenWindow?.Id == window.Id && OpenWindow.Slots.Length > 2 && !OpenWindow.Slots[2].IsEmpty)
                await ClickAsync(window.Id, 2, 0, 1, cancellationToken); // shift-click the output into the inventory
        }
        var made = Inventory.Count(result) - before;
        await CloseWindowAsync(cancellationToken);
        return made > 0 ? (made >= count, $"I smelted {made} {Pretty(result)}") : (false, "the furnace did not produce anything");
    }

    /// <summary>Moves `count` of an item into one container slot (pick up stacks, right-click one item each).</summary>
    private async Task LoadSlotAsync(int windowId, string item, int count, int targetSlot, Func<int, int> local, CancellationToken ct)
    {
        var left = count;
        foreach (var slot in Inventory.Find(s => s.Name == item).ToList())
        {
            if (left <= 0) break;
            var have = Inventory[slot].Count;
            await ClickAsync(windowId, local(slot), 0, 0, ct);   // pick up
            if (have <= left)
            {
                await ClickAsync(windowId, targetSlot, 0, 0, ct); // put the whole stack in
                left -= have;
            }
            else
            {
                for (var i = 0; i < left; i++) await ClickAsync(windowId, targetSlot, 1, 0, ct);
                await ClickAsync(windowId, local(slot), 0, 0, ct); // the rest back
                left = 0;
            }
        }
        await WaitTicksAsync(2, ct);
    }

    /// <summary>Opens a furnace within reach, or places one (crafting it from 8 cobblestone if needed).</summary>
    public async Task<bool> OpenFurnaceAsync(CancellationToken ct = default)
    {
        var furnace = World.Data.Block("furnace")!;
        var near = World.FindBlocks(s => s > 0 && World.Data.StateBlock[s] == furnace.Id, EyePosition, BlockReach - 0.5, 1);
        if (near.Count == 0)
        {
            if (Inventory.Count("furnace") == 0 && !(await CraftItemAsync("furnace", 1, ct)).Success) return false;
            if (!await EquipAsync("furnace", ct)) return false;
            var feet = FeetBlock;
            var placed = false;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) })
                if (await PlaceBlockAsync(feet.X + dx, feet.Y, feet.Z + dz, ct)) { placed = true; break; }
            if (!placed) return false;
            near = World.FindBlocks(s => s > 0 && World.Data.StateBlock[s] == furnace.Id, EyePosition, BlockReach, 1);
            if (near.Count == 0) return false;
        }
        var (fx, fy, fz) = near[0];
        await UseOnBlockAsync(fx, fy, fz, FaceToward(fx, fy, fz), Hand.Main, null, ct);
        return await WaitUntilAsync(() => OpenWindow is { } w && w.MenuType == FurnaceMenuId && w.Slots.Length >= 39, 40, ct);
    }
}
