using SpawnDev.Minecraft.Crafting;
using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Items;

namespace SpawnDev.Minecraft;

// Crafting with real container clicks: the 2x2 inventory grid, or an open crafting table.
public sealed partial class MinecraftClient
{
    /// <summary>The minecraft:menu id of a crafting table screen.</summary>
    public static int CraftingMenuId => Array.IndexOf(GameData.Vanilla.Registries["minecraft:menu"], "minecraft:crafting");

    public bool CraftingTableOpen => OpenWindow is { } w && w.MenuType == CraftingMenuId;

    /// <summary>How many batches of a recipe the inventory has ingredients for.</summary>
    public int CraftableBatches(CraftingRecipe recipe)
    {
        var counts = new Dictionary<string, int>();
        for (var i = PlayerInventory.MainStart; i < PlayerInventory.Size; i++)
            if (!Inventory[i].IsEmpty) counts[Inventory[i].Name] = counts.GetValueOrDefault(Inventory[i].Name) + Inventory[i].Count;
        // Greedy per cell: each cell takes from the item it matches that we have most of.
        for (var batches = 64; batches > 0; batches--)
        {
            var left = new Dictionary<string, int>(counts);
            var ok = true;
            foreach (var cell in recipe.Cells)
            {
                if (cell == null) continue;
                var pick = left.Where(kv => kv.Value >= batches && cell.Matches(kv.Key)).Select(kv => kv.Key).FirstOrDefault();
                if (pick == null) { ok = false; break; }
                left[pick] -= batches;
            }
            if (ok) return batches;
        }
        return 0;
    }

    /// <summary>
    /// Crafts `batches` of a recipe from the inventory: in the 2x2 grid when it fits and no table is open, else in
    /// the open crafting table. Ingredients are placed with real clicks (pick up a stack, right-click one item per
    /// batch into each grid cell, put the rest back), then the result is shift-clicked into the inventory.
    /// Returns false when ingredients or the right screen are missing, or the server did not produce the result.
    /// </summary>
    public async Task<bool> CraftAsync(CraftingRecipe recipe, int batches, CancellationToken cancellationToken = default)
    {
        if (batches <= 0 || CraftableBatches(recipe) < batches) return false;
        var table = CraftingTableOpen;
        if (!table && !recipe.FitsInInventory) return false;
        if (!table && OpenWindow != null) await CloseWindowAsync(cancellationToken);
        var windowId = table ? OpenWindow!.Id : 0;
        var gridWidth = table ? 3 : 2;
        // Player inventory slot (9..44) as this window numbers it: the same in window 0; +1 in a crafting table.
        int Local(int inventorySlot) => table ? inventorySlot + 1 : inventorySlot;

        var before = Inventory.Count(recipe.Result);

        // A local copy of our stacks to plan the clicks against (the server's updates arrive later).
        var stacks = new Dictionary<int, (string Name, int Count)>();
        for (var i = PlayerInventory.MainStart; i < PlayerInventory.Size - 1; i++)
            if (!Inventory[i].IsEmpty) stacks[i] = (Inventory[i].Name, Inventory[i].Count);

        for (var r = 0; r < recipe.Height; r++)
        for (var c = 0; c < recipe.Width; c++)
        {
            var cell = recipe.Cells[r * recipe.Width + c];
            if (cell == null) continue;
            var target = 1 + r * gridWidth + c;
            var needed = batches;
            while (needed > 0)
            {
                var source = stacks.Where(kv => cell.Matches(kv.Value.Name) && kv.Value.Count > 0).OrderByDescending(kv => kv.Value.Count).Select(kv => kv.Key).DefaultIfEmpty(-1).First();
                if (source < 0) return false;
                var (name, count) = stacks[source];
                await ClickAsync(windowId, Local(source), 0, 0, cancellationToken); // pick up the stack
                var put = Math.Min(needed, count);
                for (var i = 0; i < put; i++) await ClickAsync(windowId, target, 1, 0, cancellationToken); // one item each
                await ClickAsync(windowId, Local(source), 0, 0, cancellationToken); // put the rest back
                stacks[source] = (name, count - put);
                needed -= put;
            }
        }

        // The server computes the result slot from the grid.
        var resultName = recipe.Result;
        bool ResultShown() => table ? OpenWindow?.Slots.Length > 0 && OpenWindow.Slots[0].Name == resultName : Inventory[0].Name == resultName;
        if (!await WaitUntilAsync(ResultShown, 40, cancellationToken))
        {
            await ClearCraftingGridAsync(windowId, table, cancellationToken);
            return false;
        }
        await ClickAsync(windowId, 0, 0, 1, cancellationToken); // shift-click: craft everything the grid allows
        var expected = before + recipe.ResultCount * batches;
        var ok = await WaitUntilAsync(() => Inventory.Count(resultName) >= expected, 40, cancellationToken);
        await ClearCraftingGridAsync(windowId, table, cancellationToken);
        return ok;
    }

    /// <summary>
    /// Makes `count` of an item, crafting what is missing on the way (planks from whichever logs we carry, sticks
    /// from planks...). Uses a crafting table when needed: one within reach, else one from the inventory placed
    /// next to us, else one crafted first. Returns false with what is missing ("need 2 more cobblestone").
    /// </summary>
    public async Task<(bool Success, string Message)> CraftItemAsync(string item, int count, CancellationToken cancellationToken = default)
    {
        item = GameData.Normalize(item);
        var book = RecipeBook.Vanilla;
        var recipes = book.RecipesFor(item);
        if (recipes.Count == 0) return (false, $"there is no crafting recipe for {Pretty(item)}");
        string? missing = null;
        foreach (var recipe in recipes.OrderBy(r => r.FitsInInventory ? 0 : 1))
        {
            var batches = (count + recipe.ResultCount - 1) / recipe.ResultCount;
            // The table first: making one eats planks the recipe itself may need (sub-crafts work in the table too).
            if (!recipe.FitsInInventory && !CraftingTableOpen && !await OpenCraftingTableAsync(cancellationToken))
                return (false, "I need a crafting table (and could not make or reach one)");
            var (ready, why) = await EnsureIngredientsAsync(recipe, batches, 0, cancellationToken);
            if (!ready)
            {
                missing ??= why;
                if (CraftingTableOpen) await CloseWindowAsync(cancellationToken);
                continue;
            }
            var ok = await CraftAsync(recipe, batches, cancellationToken);
            if (CraftingTableOpen) await CloseWindowAsync(cancellationToken);
            return ok ? (true, $"I crafted {recipe.ResultCount * batches} {Pretty(item)}") : (false, $"crafting {Pretty(item)} did not work");
        }
        return (false, missing ?? $"I can't make {Pretty(item)}");
    }

    private static string Pretty(string item) => item.Replace("minecraft:", "").Replace('_', ' ');

    /// <summary>Makes sure the inventory holds enough for `batches` of a recipe, crafting intermediate items.</summary>
    private async Task<(bool, string)> EnsureIngredientsAsync(CraftingRecipe recipe, int batches, int depth, CancellationToken ct)
    {
        if (CraftableBatches(recipe) >= batches) return (true, "");
        if (depth >= 3) return (false, $"I don't have the materials for {Pretty(recipe.Result)}");
        // Count what each distinct ingredient needs and craft whichever ones we are short of, re-checking after every
        // craft: making one ingredient can eat another (sticks are made of the planks a pickaxe also needs).
        var needs = recipe.Cells.Where(c => c != null).GroupBy(c => c!).Select(g => (Ingredient: g.Key, Count: g.Count() * batches)).ToList();
        for (var pass = 0; pass < 6 && CraftableBatches(recipe) < batches; pass++)
        {
            var progressed = false;
            foreach (var (ingredient, needCount) in needs)
            {
                var have = ingredient.Items.Sum(Inventory.Count);
                if (have >= needCount) continue;
                var shortBy = needCount - have;
                var made = false;
                // Prefer the variant whose own ingredients we can best supply (oak planks if we carry oak logs).
                foreach (var variant in ingredient.Items.OrderByDescending(v => RecipeBook.Vanilla.RecipesFor(v).Max(r => (int?)CraftableBatches(r)) ?? -1))
                {
                    foreach (var sub in RecipeBook.Vanilla.RecipesFor(variant).Where(r => r.FitsInInventory))
                    {
                        var subBatches = (shortBy + sub.ResultCount - 1) / sub.ResultCount;
                        var (ready, _) = await EnsureIngredientsAsync(sub, subBatches, depth + 1, ct);
                        if (ready && await CraftAsync(sub, subBatches, ct)) { made = true; break; }
                    }
                    if (made) break;
                }
                if (!made) return (false, $"I need {shortBy} more {Pretty(ingredient.Items.FirstOrDefault() ?? "materials")}");
                progressed = true;
            }
            if (!progressed) break;
        }
        return CraftableBatches(recipe) >= batches ? (true, "") : (false, $"I don't have enough for {Pretty(recipe.Result)}");
    }

    /// <summary>Opens a crafting table: one within reach, else one we place (crafting it from planks if needed).</summary>
    public async Task<bool> OpenCraftingTableAsync(CancellationToken ct = default)
    {
        var tableBlock = World.Data.Block("crafting_table")!;
        var near = World.FindBlocks(s => s > 0 && World.Data.StateBlock[s] == tableBlock.Id, EyePosition, BlockReach - 0.5, 1);
        if (near.Count == 0)
        {
            if (Inventory.Count("crafting_table") == 0)
            {
                var (made, _) = await CraftItemAsync("crafting_table", 1, ct);
                if (!made) return false;
            }
            if (!await EquipAsync("crafting_table", ct)) return false;
            var feet = FeetBlock;
            var spots = new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) }
                .Select(d => (X: feet.X + d.Item1, feet.Y, Z: feet.Z + d.Item2));
            var placed = false;
            foreach (var s in spots)
                if (await PlaceBlockAsync(s.X, s.Y, s.Z, ct)) { placed = true; break; }
            if (!placed) return false;
            near = World.FindBlocks(st => st > 0 && World.Data.StateBlock[st] == tableBlock.Id, EyePosition, BlockReach, 1);
            if (near.Count == 0) return false;
        }
        var (tx, ty, tz) = near[0];
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpen(Items.ContainerWindow w) { if (w.MenuType == CraftingMenuId) opened.TrySetResult(); }
        WindowOpened += OnOpen;
        try
        {
            await UseOnBlockAsync(tx, ty, tz, FaceToward(tx, ty, tz), Hand.Main, null, ct);
            return await WaitUntilAsync(() => opened.Task.IsCompleted && OpenWindow?.Slots.Length >= 46, 40, ct);
        }
        finally
        {
            WindowOpened -= OnOpen;
        }
    }

    /// <summary>Shift-clicks anything left in the crafting grid back into the inventory.</summary>
    private async Task ClearCraftingGridAsync(int windowId, bool table, CancellationToken ct)
    {
        var cells = table ? 9 : 4;
        for (var s = 1; s <= cells; s++)
        {
            var stack = table ? (OpenWindow?.Slots.Length > s ? OpenWindow.Slots[s] : ItemStack.Empty) : Inventory[s];
            if (!stack.IsEmpty) await ClickAsync(windowId, s, 0, 1, ct);
        }
    }
}
