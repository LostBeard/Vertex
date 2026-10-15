using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Protocol;

namespace SpawnDev.Minecraft.Items;

/// <summary>One trade a villager (or wandering trader) offers: what it wants, what it gives, and how much of it is left.</summary>
/// <remarks>
/// Wire format (protocol 770, Mojang's MerchantOffer codec; minecraft-data's "trade_list"): cost A (item id, count, exact component matchers), result (a Slot), an optional cost B, then
/// out-of-stock, uses, max uses, xp, special price, price multiplier, demand.
/// </remarks>
public sealed class Trade
{
    /// <summary>Position in the villager's list: what <c>select_trade</c> takes.</summary>
    public int Index { get; init; }

    /// <summary>The base cost of the first ingredient, before demand and special prices move it (see <see cref="PriceA"/>).</summary>
    public required ItemStack CostA { get; init; }

    /// <summary>The second ingredient (never repriced), or null.</summary>
    public ItemStack? CostB { get; init; }

    public required ItemStack Result { get; init; }

    /// <summary>The villager has run out of this trade until it restocks.</summary>
    public bool OutOfStock { get; init; }
    public int Uses { get; init; }
    public int MaxUses { get; init; }
    /// <summary>Experience the villager gains from the trade.</summary>
    public int Xp { get; init; }
    /// <summary>Price change for this player (reputation, Hero of the Village), added to the first cost.</summary>
    public int SpecialPrice { get; init; }
    public float PriceMultiplier { get; init; }
    /// <summary>How wanted the result is: it raises the price.</summary>
    public int Demand { get; init; }

    /// <summary>Trades left before the villager has to restock.</summary>
    public int UsesLeft => OutOfStock ? 0 : Math.Max(0, MaxUses - Uses);

    /// <summary>
    /// What the first ingredient costs NOW: vanilla MerchantOffer.getCostA, the base count plus floor(count * demand * multiplier) (demand never lowers it) plus the special price,
    /// kept between 1 and the item's stack size.
    /// </summary>
    public int PriceA
    {
        get
        {
            var count = CostA.Count;
            var demandRise = Math.Max(0, (int)Math.Floor(count * Demand * PriceMultiplier));
            return Math.Clamp(count + demandRise + SpecialPrice, 1, Math.Max(1, CostA.MaxStackSize));
        }
    }

    /// <summary>The second ingredient's count (0 when there is none).</summary>
    public int PriceB => CostB?.Count ?? 0;

    /// <summary>Whether this trade gives <paramref name="item"/> (a name, with or without "minecraft:").</summary>
    public bool Gives(string item) => NameMatches(Result.Name, item);

    /// <summary>Whether this trade wants <paramref name="item"/> as an ingredient.</summary>
    public bool Wants(string item) => NameMatches(CostA.Name, item) || (CostB != null && NameMatches(CostB.Name, item));

    private static bool NameMatches(string name, string item) =>
        string.Equals(name, item, StringComparison.OrdinalIgnoreCase) || string.Equals(name, "minecraft:" + item, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(item.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase) && item.Length >= 3;

    public override string ToString() =>
        $"{PriceA}x {Short(CostA.Name)}{(CostB != null ? " + " + CostB.Count + "x " + Short(CostB.Name) : "")} -> {Result.Count}x {Short(Result.Name)}" +
        (OutOfStock ? " (sold out)" : $" ({UsesLeft} left)");

    private static string Short(string name) => name.StartsWith("minecraft:") ? name[10..] : name;

    /// <summary>What a merchant_offers packet carries after its window id.</summary>
    public sealed record Offers(IReadOnlyList<Trade> Trades, int VillagerLevel, int VillagerXp, bool ShowProgress, bool CanRestock);

    /// <summary>Reads the body of a merchant_offers packet (after the window id).</summary>
    public static Offers Read(ref PacketReader r, Func<int, ItemType?>? types = null)
    {
        var count = r.ReadVarInt();
        var trades = new List<Trade>(count);
        for (var i = 0; i < count; i++)
        {
            var costA = ReadCost(ref r, types);
            var result = ItemStack.Read(ref r, types);
            var costB = r.ReadBool() ? ReadCost(ref r, types) : null;
            var outOfStock = r.ReadBool();
            var uses = r.ReadInt();
            var maxUses = r.ReadInt();
            var xp = r.ReadInt();
            var special = r.ReadInt();
            var multiplier = r.ReadFloat();
            var demand = r.ReadInt();
            trades.Add(new Trade
            {
                Index = i, CostA = costA, CostB = costB, Result = result, OutOfStock = outOfStock, Uses = uses, MaxUses = maxUses,
                Xp = xp, SpecialPrice = special, PriceMultiplier = multiplier, Demand = demand,
            });
        }
        var level = r.ReadVarInt();
        var villagerXp = r.ReadVarInt();
        var showProgress = r.ReadBool();
        var canRestock = r.ReadBool();
        return new Offers(trades, level, villagerXp, showProgress, canRestock);
    }

    // An ingredient: item id, count, and the components the item must have exactly (an array of typed values).
    private static ItemStack ReadCost(ref PacketReader r, Func<int, ItemType?>? types)
    {
        var itemId = r.ReadVarInt();
        var count = r.ReadVarInt();
        var components = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (ProtoDef.Vanilla.Read(ref r, "ExactComponentMatcher") is List<object?> list)
            foreach (var c in list)
                if (c is Dictionary<string, object?> comp && comp.GetValueOrDefault("type") is string type)
                    components[type] = comp.GetValueOrDefault("data");
        return new ItemStack(itemId, count, components, [], types?.Invoke(itemId));
    }
}
