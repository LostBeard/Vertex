using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Nbt;
using SpawnDev.Minecraft.Protocol;
using SpawnDev.Minecraft.Text;

namespace SpawnDev.Minecraft.Items;

/// <summary>
/// An item stack as the server sent it: item id, count, and the data components it adds to (or removes from)
/// the item's defaults. Components are decoded generically (ProtoDef) and keyed by name ("damage", "enchantments").
/// </summary>
public sealed class ItemStack
{
    public static readonly ItemStack Empty = new(0, 0, new Dictionary<string, object?>(), []);

    /// <param name="type">The item's type when the vanilla table does not say (a modded server's items, by its registry).</param>
    public ItemStack(int itemId, int count, IReadOnlyDictionary<string, object?> components, IReadOnlyList<string> removedComponents, ItemType? type = null)
    {
        ItemId = itemId;
        Count = count;
        Components = components;
        RemovedComponents = removedComponents;
        _type = type;
    }

    private readonly ItemType? _type;

    public int ItemId { get; }
    public int Count { get; }
    public IReadOnlyDictionary<string, object?> Components { get; }
    public IReadOnlyList<string> RemovedComponents { get; }

    public bool IsEmpty => Count <= 0;
    public ItemType? Type => IsEmpty ? null : _type ?? GameData.Vanilla.Item(ItemId);
    public string Name => Type?.Name ?? "minecraft:air";

    public int Damage => Components.TryGetValue("damage", out var d) && d is int i ? i : 0;
    public int MaxDamage => Components.TryGetValue("max_damage", out var d) && d is int i ? i : Type?.MaxDamage ?? 0;
    public int MaxStackSize => Components.TryGetValue("max_stack_size", out var d) && d is int i ? i : Type?.MaxStackSize ?? 64;
    public int DurabilityLeft => MaxDamage > 0 ? MaxDamage - Damage : int.MaxValue;
    public string? CustomName => Components.TryGetValue("custom_name", out var n) && n is NbtTag t ? TextComponent.ToPlainText(t) : null;

    /// <summary>Enchantment registry ids and levels (ids index the server's minecraft:enchantment registry).</summary>
    public IEnumerable<(int Id, int Level)> Enchantments
    {
        get
        {
            if (!Components.TryGetValue("enchantments", out var e) || e is not Dictionary<string, object?> d) yield break;
            if (d.GetValueOrDefault("enchantments") is not List<object?> list) yield break;
            foreach (var x in list)
                if (x is Dictionary<string, object?> en) yield return ((int)en["id"]!, (int)en["level"]!);
        }
    }

    /// <summary>The same item, a different count (empty at 0).</summary>
    public ItemStack WithCount(int count) => count <= 0 ? Empty : new ItemStack(ItemId, count, Components, RemovedComponents, _type);

    public override string ToString() => IsEmpty ? "empty" : $"{Count}x {Name}" + (CustomName is string n ? $" \"{n}\"" : "");

    /// <summary>Reads a Slot (protocol 770 item stack with components).</summary>
    /// <param name="types">Item id -> type on a server that numbers items its own way (null: the vanilla table).</param>
    public static ItemStack Read(ref PacketReader r, Func<int, ItemType?>? types = null)
    {
        var value = ProtoDef.Vanilla.Read(ref r, "Slot") as Dictionary<string, object?>;
        return FromProtoDef(value, types);
    }

    internal static ItemStack FromProtoDef(object? value, Func<int, ItemType?>? types = null)
    {
        if (value is not Dictionary<string, object?> d || d.GetValueOrDefault("itemCount") is not int count || count <= 0) return Empty;
        var components = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (d.GetValueOrDefault("components") is List<object?> list)
            foreach (var c in list)
                if (c is Dictionary<string, object?> comp && comp.GetValueOrDefault("type") is string type)
                    components[type] = comp.GetValueOrDefault("data");
        var removed = new List<string>();
        if (d.GetValueOrDefault("removeComponents") is List<object?> rl)
            foreach (var c in rl)
                if (c is Dictionary<string, object?> comp && comp.GetValueOrDefault("type") is string type) removed.Add(type);
        var id = (int)d["itemId"]!;
        return new ItemStack(id, count, components, removed, types?.Invoke(id));
    }
}
