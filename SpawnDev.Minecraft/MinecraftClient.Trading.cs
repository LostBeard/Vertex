using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Entities;
using SpawnDev.Minecraft.Items;
using SpawnDev.Minecraft.Protocol;
using CB = SpawnDev.Minecraft.Protocol.PacketIds;

namespace SpawnDev.Minecraft;

public sealed partial class MinecraftClient
{
    /// <summary>The minecraft:menu registry id of a merchant's trading screen.</summary>
    public static int MerchantMenuId => Array.IndexOf(GameData.Vanilla.Registries["minecraft:menu"], "minecraft:merchant");

    public bool TradingScreenOpen => OpenWindow is { } w && w.MenuType == MerchantMenuId;

    /// <summary>
    /// Right-clicks a villager or wandering trader and waits for its trading screen and its trades. Returns the window, or null when it would not trade (a nitwit, a villager with no job,
    /// one that is asleep, a baby, or out of reach: the server just shakes its head and sends nothing).
    /// </summary>
    public async Task<ContainerWindow?> OpenTradesAsync(Entity merchant, CancellationToken cancellationToken = default)
    {
        if (OpenWindow != null) await CloseWindowAsync(cancellationToken);
        await InteractAsync(merchant, Hand.Main, cancellationToken);
        var ok = await WaitUntilAsync(() => OpenWindow is { } w && w.MenuType == MerchantMenuId && w.Trades.Count > 0 && w.Slots.Length >= MerchantSlots, 40, cancellationToken);
        return ok ? OpenWindow : null;
    }

    // The merchant screen: slots 0 and 1 are the ingredients, 2 the result, then the player's 27 main and 9 hotbar slots.
    private const int MerchantResultSlot = 2, MerchantFirstPlayerSlot = 3, MerchantSlots = 39;

    /// <summary>
    /// Does a trade of the open merchant window <paramref name="times"/> times, like a player: pick the trade (the server moves the ingredients from our inventory into the two input slots),
    /// take the result on the cursor, put it in the inventory, repeat. Stops early when the villager is sold out, the ingredients run out, or the server stops offering the result.
    /// Returns how many times it traded (the result items are in the inventory).
    /// </summary>
    public async Task<int> TradeAsync(int tradeIndex, int times = 1, CancellationToken cancellationToken = default)
    {
        if (OpenWindow is not { } window || window.MenuType != MerchantMenuId) return 0;
        if ((uint)tradeIndex >= (uint)window.Trades.Count) return 0;
        var trade = window.Trades[tradeIndex];
        var done = 0;
        while (done < times && OpenWindow?.Id == window.Id)
        {
            // (Re)select: the server fills the input slots from the inventory with what the trade needs.
            using (var p = new PacketWriter(CB.Play.Serverbound.SelectTrade))
            {
                p.WriteVarInt(tradeIndex);
                await SendAsync(p, cancellationToken);
            }
            var offered = await WaitUntilAsync(() => OpenWindow?.Id == window.Id && OpenWindow.Slots.Length > MerchantResultSlot
                && !OpenWindow.Slots[MerchantResultSlot].IsEmpty && OpenWindow.Slots[MerchantResultSlot].Name == trade.Result.Name, 20, cancellationToken);
            if (!offered)
            {
                Log($"trade {tradeIndex} ({trade}): the server did not offer the result; ingredient slots hold {string.Join(" | ", OpenWindow?.Slots.Take(3) ?? [])}");
                await ReturnIngredientsAsync(window.Id, cancellationToken);
                break;
            }

            // Take the result on the cursor, then put the cursor away. Every click carries the window's state id, and the server only accepts a click made against the state it last sent:
            // its slot updates (the new state id) can arrive AFTER the cursor update, so wait for the state to move on before the next click, or the next click is ignored and the window resynced.
            var state = OpenWindow!.StateId;
            await ClickAsync(window.Id, MerchantResultSlot, 0, 0, cancellationToken);
            if (!await WaitUntilAsync(() => !Inventory.Cursor.IsEmpty && OpenWindow?.StateId != state, 20, cancellationToken))
            {
                Log($"trade {tradeIndex}: clicked the result but the cursor stayed empty (result slot: {OpenWindow?.Slots[MerchantResultSlot]}, cursor: {Inventory.Cursor})");
                break;
            }
            var held = Inventory.Cursor;
            var target = PlaceForCursor(OpenWindow!, held);
            if (target < 0) { await CloseWindowAsync(cancellationToken); break; }    // no room: closing drops the cursor stack at our feet, as a player closing it would
            state = OpenWindow!.StateId;
            var inSlotBefore = OpenWindow.Slots[target].Count;
            await ClickAsync(window.Id, target, 0, 0, cancellationToken);
            // The cursor cannot be waited on: a click says (to the server) that the cursor is empty afterwards, and when it really is, the server has nothing to correct and sends nothing. The
            // slot it went into is the proof.
            if (!await WaitUntilAsync(() => OpenWindow?.StateId != state && OpenWindow.Slots[target].Count == inSlotBefore + held.Count, 20, cancellationToken))
            {
                Log($"trade {tradeIndex}: put the result in slot {target} but it did not arrive there (it holds {OpenWindow?.Slots[target]}; state {state} -> {OpenWindow?.StateId})");
                break;
            }
            Inventory.Cursor = ItemStack.Empty;
            done++;
        }
        await ReturnIngredientsAsync(window.Id, cancellationToken);      // what the server loaded for the next trade goes back, so the inventory is what we really have
        return done;
    }

    // Choosing a trade makes the server move the ingredients we have into the two input slots, whether or not they add up to a trade (19 wheat for a price of 20, a sold-out trade): put them
    // back in the inventory, as a player shift-clicking them out would, so what we carry is where it was.
    private async Task ReturnIngredientsAsync(int windowId, CancellationToken cancellationToken)
    {
        for (var slot = 0; slot < MerchantResultSlot; slot++)
        {
            if (OpenWindow?.Id != windowId || OpenWindow.Slots[slot].IsEmpty) continue;
            await ClickAsync(windowId, slot, 0, 1, cancellationToken);
            await WaitUntilAsync(() => OpenWindow?.Id != windowId || OpenWindow.Slots[slot].IsEmpty, 20, cancellationToken);
        }
    }

    // A window slot (in the player's part) the cursor stack can go in: a same-item stack with room first, else the first empty one.
    private static int PlaceForCursor(ContainerWindow window, ItemStack held)
    {
        for (var i = MerchantFirstPlayerSlot; i < window.Slots.Length; i++)
            if (!window.Slots[i].IsEmpty && window.Slots[i].Name == held.Name && window.Slots[i].Count + held.Count <= window.Slots[i].MaxStackSize
                && window.Slots[i].Components.Count == held.Components.Count) return i;
        for (var i = MerchantFirstPlayerSlot; i < window.Slots.Length; i++)
            if (window.Slots[i].IsEmpty) return i;
        return -1;
    }
}
