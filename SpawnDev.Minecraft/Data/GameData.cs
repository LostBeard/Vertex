using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpawnDev.Minecraft.Data;

public sealed record BlockProperty(string Name, string[] Values);

/// <summary>A block type (all its states share it). State ids MinState..MaxState enumerate Properties, last fastest.</summary>
public sealed class BlockType
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int MinState { get; init; }
    public int MaxState { get; init; }
    public int DefaultState { get; init; }
    public float ExplosionResistance { get; init; }
    public BlockProperty[] Properties { get; init; } = [];
    public override string ToString() => Name;
}

/// <summary>An axis-aligned box in block-local coordinates (0..1, some shapes reach 1.5).</summary>
public readonly record struct Box(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

public sealed record ToolRule(string[]? Blocks, string? BlockTag, float? Speed, bool? CorrectForDrops);

public sealed class ItemType
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int MaxStackSize { get; init; } = 64;
    public int MaxDamage { get; init; }
    /// <summary>The block this item places, if it is a block item.</summary>
    public string? Block { get; init; }
    public ToolRule[] ToolRules { get; init; } = [];
    public float ToolDefaultSpeed { get; init; } = 1;
    public int FoodNutrition { get; init; }
    public float FoodSaturation { get; init; }
    public bool IsFood => FoodNutrition > 0;
    /// <summary>The vanilla components Vertex keeps (tool, food, weapon, equippable, attribute modifiers...).</summary>
    public JsonObject Components { get; init; } = [];
    public override string ToString() => Name;
}

public sealed record EntityTypeInfo(int Id, string Name, float Width, float Height, float EyeHeight)
{
    public override string ToString() => Name;
}

/// <summary>
/// The game's tables for one version, generated from the game itself by tools/DataGen: blocks and every block
/// state (hardness, collision boxes, friction, fluids), items (tool rules, food, stack sizes), entity sizes,
/// recipes and the vanilla tags. Per-state facts are flat arrays indexed by state id (hot path for physics and
/// pathfinding).
/// </summary>
public sealed class GameData
{
    private static readonly Lazy<GameData> _vanilla = new(() => Load("SpawnDev.Minecraft.Data.game-data.json.br"));

    /// <summary>Minecraft 1.21.5.</summary>
    public static GameData Vanilla => _vanilla.Value;

    public const byte FlagAir = 1, FlagNeedsTool = 2, FlagReplaceable = 4, FlagWater = 8, FlagLava = 16, FlagFluidSource = 32, FlagBlocksMotion = 64;

    public string Version { get; private init; } = "";
    public BlockType[] Blocks { get; private init; } = [];
    public ItemType[] Items { get; private init; } = [];
    public EntityTypeInfo[] Entities { get; private init; } = [];
    public JsonObject Recipes { get; private init; } = [];

    /// <summary>Static registries by name (minecraft:attribute, minecraft:menu, minecraft:mob_effect...), names in network-id order.</summary>
    public IReadOnlyDictionary<string, string[]> Registries { get; private init; } = new Dictionary<string, string[]>();

    /// <summary>The name of a static registry entry by network id, or null.</summary>
    public string? RegistryName(string registry, int id) =>
        Registries.TryGetValue(registry, out var names) && (uint)id < (uint)names.Length ? names[id] : null;

    // Per state id.
    public ushort[] StateBlock { get; private init; } = [];
    public float[] Hardness { get; private init; } = [];
    public byte[] StateFlags { get; private init; } = [];
    public ushort[] StateShape { get; private init; } = [];
    public float[] Friction { get; private init; } = [];
    public float[] SpeedFactor { get; private init; } = [];
    public float[] JumpFactor { get; private init; } = [];
    public byte[] FluidAmount { get; private init; } = [];
    public Box[][] Shapes { get; private init; } = [];

    private Dictionary<string, BlockType> _blocksByName = [];
    private Dictionary<string, ItemType> _itemsByName = [];
    private Dictionary<string, EntityTypeInfo> _entitiesByName = [];
    private Dictionary<string, HashSet<string>> _itemTags = [];
    private Dictionary<string, HashSet<string>> _blockTags = [];

    public int StateCount => StateBlock.Length;

    public BlockType? Block(string name) => _blocksByName.GetValueOrDefault(Normalize(name));
    public ItemType? Item(string name) => _itemsByName.GetValueOrDefault(Normalize(name));
    public EntityTypeInfo? Entity(string name) => _entitiesByName.GetValueOrDefault(Normalize(name));
    public ItemType? Item(int id) => id >= 0 && id < Items.Length ? Items[id] : null;
    public EntityTypeInfo? Entity(int id) => id >= 0 && id < Entities.Length ? Entities[id] : null;

    public static string Normalize(string name) => name.Contains(':') ? name : "minecraft:" + name;

    public BlockType BlockOf(int state) => Blocks[StateBlock[state]];
    public bool IsAir(int state) => (StateFlags[state] & FlagAir) != 0;
    public Box[] CollisionBoxes(int state) => Shapes[StateShape[state]];
    public bool HasCollision(int state) => Shapes[StateShape[state]].Length > 0;

    /// <summary>The value of one property of a state (e.g. "facing" of a stair state), or null.</summary>
    public string? GetProperty(int state, string property)
    {
        var block = BlockOf(state);
        var offset = state - block.MinState;
        for (var i = block.Properties.Length - 1; i >= 0; i--)
        {
            var p = block.Properties[i];
            var index = offset % p.Values.Length;
            offset /= p.Values.Length;
            if (p.Name == property) return p.Values[index];
        }
        return null;
    }

    /// <summary>The same block's state with one property changed.</summary>
    public int WithProperty(int state, string property, string value)
    {
        var block = BlockOf(state);
        var stride = 1;
        for (var i = block.Properties.Length - 1; i >= 0; i--)
        {
            var p = block.Properties[i];
            if (p.Name == property)
            {
                var current = (state - block.MinState) / stride % p.Values.Length;
                var wanted = Array.IndexOf(p.Values, value);
                if (wanted < 0) throw new ArgumentException($"{block.Name} has no {property}={value}");
                return state + (wanted - current) * stride;
            }
            stride *= p.Values.Length;
        }
        throw new ArgumentException($"{block.Name} has no property {property}");
    }

    public IReadOnlySet<string> ItemTag(string tag) => _itemTags.GetValueOrDefault(Normalize(tag.TrimStart('#'))) ?? (IReadOnlySet<string>)new HashSet<string>();
    public IReadOnlySet<string> BlockTag(string tag) => _blockTags.GetValueOrDefault(Normalize(tag.TrimStart('#'))) ?? (IReadOnlySet<string>)new HashSet<string>();

    public static GameData Load(string resource)
    {
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded {resource} missing (run tools/DataGen)");
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        var root = JsonNode.Parse(brotli)!;

        var blocks = root["blocks"]!.AsArray().Select((b, i) => new BlockType
        {
            Id = i,
            Name = b!["name"]!.GetValue<string>(),
            MinState = b["min"]!.GetValue<int>(),
            MaxState = b["max"]!.GetValue<int>(),
            DefaultState = b["default"]!.GetValue<int>(),
            ExplosionResistance = b["resistance"]!.GetValue<float>(),
            Properties = b["props"]!.AsArray().Select(p => new BlockProperty(p![0]!.GetValue<string>(), p[1]!.AsArray().Select(v => v!.GetValue<string>()).ToArray())).ToArray(),
        }).ToArray();

        var states = root["states"]!.AsArray();
        var n = states.Count;
        var stateBlock = new ushort[n];
        var hardness = new float[n];
        var flags = new byte[n];
        var shape = new ushort[n];
        var friction = new float[n];
        var speed = new float[n];
        var jump = new float[n];
        var fluid = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var s = states[i]!.AsArray();
            stateBlock[i] = (ushort)s[0]!.GetValue<int>();
            hardness[i] = s[1]!.GetValue<float>();
            flags[i] = (byte)s[2]!.GetValue<int>();
            shape[i] = (ushort)s[3]!.GetValue<int>();
            friction[i] = s[4]!.GetValue<float>();
            speed[i] = s[5]!.GetValue<float>();
            jump[i] = s[6]!.GetValue<float>();
            fluid[i] = (byte)s[7]!.GetValue<int>();
        }
        var shapes = root["shapes"]!.AsArray().Select(sh => sh!.AsArray().Select(b =>
        {
            var a = b!.AsArray();
            return new Box(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>(), a[4]!.GetValue<double>(), a[5]!.GetValue<double>());
        }).ToArray()).ToArray();

        var items = root["items"]!.AsArray().Select((it, i) =>
        {
            var c = it!["components"]!.AsObject();
            var tool = c["tool"];
            var food = c["food"];
            return new ItemType
            {
                Id = i,
                Name = it["name"]!.GetValue<string>(),
                MaxStackSize = c["max_stack_size"]?.GetValue<int>() ?? 64,
                MaxDamage = c["max_damage"]?.GetValue<int>() ?? 0,
                Block = it["block"]?.GetValue<string>(),
                ToolRules = tool?["rules"]?.AsArray().Select(r => ParseToolRule(r!)).ToArray() ?? [],
                ToolDefaultSpeed = tool?["default_mining_speed"]?.GetValue<float>() ?? 1,
                FoodNutrition = food?["nutrition"]?.GetValue<int>() ?? 0,
                FoodSaturation = food?["saturation"]?.GetValue<float>() ?? 0,
                Components = c,
            };
        }).ToArray();

        var entities = root["entities"]!.AsArray().Select((e, i) => new EntityTypeInfo(i, e![0]!.GetValue<string>(),
            e[1]!.GetValue<float>(), e[2]!.GetValue<float>(), e[3]!.GetValue<float>())).ToArray();

        var data = new GameData
        {
            Version = root["version"]!.GetValue<string>(),
            Blocks = blocks,
            Items = items,
            Entities = entities,
            Recipes = root["recipes"]!.AsObject(),
            Registries = root["registries"]!.AsObject().ToDictionary(r => r.Key, r => r.Value!.AsArray().Select(n => n!.GetValue<string>()).ToArray()),
            StateBlock = stateBlock,
            Hardness = hardness,
            StateFlags = flags,
            StateShape = shape,
            Friction = friction,
            SpeedFactor = speed,
            JumpFactor = jump,
            FluidAmount = fluid,
            Shapes = shapes,
        };
        data._blocksByName = blocks.ToDictionary(b => b.Name);
        data._itemsByName = items.ToDictionary(i => i.Name);
        data._entitiesByName = entities.ToDictionary(e => e.Name);
        data._itemTags = ResolveTags(root["itemTags"]!.AsObject());
        data._blockTags = ResolveTags(root["blockTags"]!.AsObject());
        return data;
    }

    // A tool rule's "blocks" is a tag ("#minecraft:mineable/pickaxe"), one block name, or a list of names.
    private static ToolRule ParseToolRule(JsonNode rule)
    {
        string[]? names = null;
        string? tag = null;
        switch (rule["blocks"])
        {
            case JsonArray list: names = list.Select(x => x!.GetValue<string>()).ToArray(); break;
            case JsonValue v when v.GetValue<string>().StartsWith('#'): tag = v.GetValue<string>()[1..]; break;
            case JsonValue v: names = [v.GetValue<string>()]; break;
        }
        return new ToolRule(names, tag, rule["speed"]?.GetValue<float>(), rule["correct_for_drops"]?.GetValue<bool>());
    }

    /// <summary>Flattens tags that include other tags ("#minecraft:logs").</summary>
    private static Dictionary<string, HashSet<string>> ResolveTags(JsonObject raw)
    {
        var result = new Dictionary<string, HashSet<string>>();
        HashSet<string> Resolve(string tag, HashSet<string> visiting)
        {
            if (result.TryGetValue(tag, out var done)) return done;
            var set = new HashSet<string>();
            if (raw[tag] is JsonArray values && visiting.Add(tag))
                foreach (var v in values)
                {
                    // An entry is "name", "#tag", or {"id": ..., "required": false}.
                    var id = v is JsonObject o ? o["id"]!.GetValue<string>() : v!.GetValue<string>();
                    if (id.StartsWith('#')) set.UnionWith(Resolve(id[1..], visiting));
                    else set.Add(id);
                }
            result[tag] = set;
            return set;
        }
        foreach (var (tag, _) in raw) Resolve(tag, []);
        return result;
    }
}
