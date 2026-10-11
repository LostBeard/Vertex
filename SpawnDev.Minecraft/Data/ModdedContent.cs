using System.Text.Json.Nodes;

namespace SpawnDev.Minecraft.Data;

/// <summary>
/// What a server's mods add that Fabric does not sync: their blocks' facts (shapes, hardness...), their items' default
/// components (stack size, durability, tool rules, food) and their mobs' sizes. Dumped from the mods' own code by
/// tools/ModDump, by name; the server's registry sync then says which ids they have there. Items and entity types
/// carry Id -1 here.
/// </summary>
public sealed class ModdedContent
{
    public IReadOnlyDictionary<string, ModdedBlock> Blocks { get; init; } = new Dictionary<string, ModdedBlock>();
    public IReadOnlyDictionary<string, ItemType> Items { get; init; } = new Dictionary<string, ItemType>();
    public IReadOnlyDictionary<string, EntityTypeInfo> Entities { get; init; } = new Dictionary<string, EntityTypeInfo>();

    public static ModdedContent Empty { get; } = new();

    public bool IsEmpty => Blocks.Count == 0 && Items.Count == 0 && Entities.Count == 0;

    /// <summary>This plus newer facts (newer wins where both have a name).</summary>
    public ModdedContent With(ModdedContent newer) => newer.IsEmpty ? this : IsEmpty ? newer : new()
    {
        Blocks = Merge(Blocks, newer.Blocks),
        Items = Merge(Items, newer.Items),
        Entities = Merge(Entities, newer.Entities),
    };

    private static Dictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T> a, IReadOnlyDictionary<string, T> b)
    {
        var d = a.ToDictionary();
        foreach (var (k, v) in b) d[k] = v;
        return d;
    }

    /// <summary>Reads a catalog (Data/modded-blocks.json.br, a world's .blocks.json); items and entities are optional.</summary>
    public static ModdedContent Read(JsonNode root) => new()
    {
        Blocks = GameData.ReadModdedCatalog(root),
        Items = (root["items"] as JsonArray ?? []).Select(i => GameData.ParseItem(-1, i!)).ToDictionary(i => i.Name),
        Entities = (root["entities"] as JsonArray ?? []).Select(e => new EntityTypeInfo(-1, e![0]!.GetValue<string>(),
            e[1]!.GetValue<float>(), e[2]!.GetValue<float>(), e[3]!.GetValue<float>())).ToDictionary(e => e.Name),
    };

    /// <summary>Writes this as a catalog (what Read reads).</summary>
    public JsonObject ToCatalog(IEnumerable<string> sources)
    {
        var root = ModDump.ToCatalog(Blocks.Values, sources);
        root["items"] = new JsonArray(Items.Values.OrderBy(i => i.Name, StringComparer.Ordinal).Select(i =>
        {
            var o = new JsonObject { ["name"] = i.Name };
            if (i.Block != null) o["block"] = i.Block;
            o["components"] = i.Components.DeepClone();
            return (JsonNode)o;
        }).ToArray());
        root["entities"] = new JsonArray(Entities.Values.OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => (JsonNode)new JsonArray(e.Name, e.Width, e.Height, e.EyeHeight)).ToArray());
        return root;
    }
}
