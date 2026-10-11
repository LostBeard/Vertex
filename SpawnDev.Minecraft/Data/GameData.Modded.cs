using System.IO.Compression;
using System.Text.Json.Nodes;

namespace SpawnDev.Minecraft.Data;

/// <summary>
/// One modded block's facts, dumped from the mod's own code by tools/ModDump: its properties (as StateDefinition
/// orders them, which is also how its states are numbered) and, per state, the same facts the vanilla table has.
/// </summary>
public sealed class ModdedBlock
{
    public string Name { get; init; } = "";
    public BlockProperty[] Properties { get; init; } = [];
    /// <summary>The default state, as an index into this block's states.</summary>
    public int DefaultIndex { get; init; }
    public float ExplosionResistance { get; init; }
    public float[] Hardness { get; init; } = [];
    public byte[] Flags { get; init; } = [];
    public Box[][] Shapes { get; init; } = [];
    public float[] Friction { get; init; } = [];
    public float[] SpeedFactor { get; init; } = [];
    public float[] JumpFactor { get; init; } = [];
    public byte[] FluidAmount { get; init; } = [];
    public int StateCount => Hardness.Length;
}

// Modded servers: Fabric syncs which blocks exist (names and raw ids) but nothing about them. Shapes, hardness and
// the rest come from tools/ModDump, run once against the server's mod set; this turns them into state tables.
public sealed partial class GameData
{
    private static readonly Lazy<IReadOnlyDictionary<string, ModdedBlock>> _modded = new(() => LoadModded("SpawnDev.Minecraft.Data.modded-blocks.json.br"));

    /// <summary>Every modded block tools/ModDump has data for, by name (empty if none was generated).</summary>
    public static IReadOnlyDictionary<string, ModdedBlock> ModdedCatalog => _modded.Value;

    private static IReadOnlyDictionary<string, ModdedBlock> LoadModded(string resource)
    {
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream(resource);
        if (stream == null) return new Dictionary<string, ModdedBlock>();
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        return ReadModdedCatalog(JsonNode.Parse(brotli)!);
    }

    /// <summary>Reads a modded block catalog (the embedded one, or one ModDump.ToCatalog wrote for a server).</summary>
    public static IReadOnlyDictionary<string, ModdedBlock> ReadModdedCatalog(JsonNode root)
    {
        var shapes = root["shapes"]!.AsArray().Select(sh => sh!.AsArray().Select(b =>
        {
            var a = b!.AsArray();
            return new Box(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>(), a[4]!.GetValue<double>(), a[5]!.GetValue<double>());
        }).ToArray()).ToArray();
        var result = new Dictionary<string, ModdedBlock>();
        foreach (var b in root["blocks"]!.AsArray())
        {
            var states = b!["states"]!.AsArray().Select(s => s!.AsArray()).ToArray();
            var block = new ModdedBlock
            {
                Name = b["name"]!.GetValue<string>(),
                Properties = b["props"]!.AsArray().Select(p => new BlockProperty(p![0]!.GetValue<string>(), p[1]!.AsArray().Select(v => v!.GetValue<string>()).ToArray())).ToArray(),
                DefaultIndex = b["default"]!.GetValue<int>(),
                ExplosionResistance = b["resistance"]!.GetValue<float>(),
                Hardness = states.Select(s => s[0]!.GetValue<float>()).ToArray(),
                Flags = states.Select(s => (byte)s[1]!.GetValue<int>()).ToArray(),
                Shapes = states.Select(s => shapes[s[2]!.GetValue<int>()]).ToArray(),
                Friction = states.Select(s => s[3]!.GetValue<float>()).ToArray(),
                SpeedFactor = states.Select(s => s[4]!.GetValue<float>()).ToArray(),
                JumpFactor = states.Select(s => s[5]!.GetValue<float>()).ToArray(),
                FluidAmount = states.Select(s => (byte)s[6]!.GetValue<int>()).ToArray(),
            };
            if (block.StateCount != block.Properties.Aggregate(1, (n, p) => n * p.Values.Length))
                throw new InvalidDataException($"{block.Name}: {block.StateCount} states for properties {string.Join(", ", block.Properties.Select(p => p.Name))}");
            result[block.Name] = block;
        }
        return result;
    }

    /// <summary>
    /// This table plus a Fabric server's modded blocks (its synced minecraft:block registry, name -> raw id), as the
    /// server numbers them: blocks in raw-id order, each block's states in order (Fabric's state id list). Vanilla
    /// blocks keep their ids; modded ones follow, with BlockType.Id = their raw id (so the server's tags, which use
    /// raw ids, match). Stops at the first modded block with no data: every state after it is unknown, since its
    /// state count decides where the next block's states start. Returns this table when nothing is added.
    /// </summary>
    public GameData WithModdedBlocks(IReadOnlyDictionary<string, int> blockRawIds, IReadOnlyDictionary<string, ModdedBlock>? catalog = null)
    {
        catalog ??= ModdedCatalog;
        var vanillaBlocks = Blocks.Length - 1 - ModdedBlockCount; // without the unknown stand-in
        var byRaw = blockRawIds.OrderBy(kv => kv.Value).ToList();
        // The vanilla blocks must be the first ids, in our order, or nothing after them can be trusted.
        if (byRaw.Count <= vanillaBlocks) return this;
        for (var i = 0; i < vanillaBlocks; i++)
            if (byRaw[i].Value != i || byRaw[i].Key != Blocks[i].Name) return this;

        var added = new List<ModdedBlock>();
        for (var i = vanillaBlocks; i < byRaw.Count; i++)
        {
            if (byRaw[i].Value != i || !catalog.TryGetValue(byRaw[i].Key, out var m)) break;
            added.Add(m);
        }
        if (added.Count == 0) return this;

        var known = VanillaStateCount + added.Sum(m => m.StateCount);
        var stateBlock = new ushort[known + 1];
        var hardness = new float[known + 1];
        var flags = new byte[known + 1];
        var shape = new ushort[known + 1];
        var friction = new float[known + 1];
        var speed = new float[known + 1];
        var jump = new float[known + 1];
        var fluid = new byte[known + 1];
        Array.Copy(StateBlock, stateBlock, VanillaStateCount);
        Array.Copy(Hardness, hardness, VanillaStateCount);
        Array.Copy(StateFlags, flags, VanillaStateCount);
        Array.Copy(StateShape, shape, VanillaStateCount);
        Array.Copy(Friction, friction, VanillaStateCount);
        Array.Copy(SpeedFactor, speed, VanillaStateCount);
        Array.Copy(JumpFactor, jump, VanillaStateCount);
        Array.Copy(FluidAmount, fluid, VanillaStateCount);

        var shapes = Shapes.ToList();
        var shapeIds = new Dictionary<Box[], int>(ReferenceEqualityComparer.Instance);
        var blocks = Blocks.Take(vanillaBlocks).ToList();
        var next = VanillaStateCount;
        foreach (var m in added)
        {
            var id = blocks.Count;
            blocks.Add(new BlockType
            {
                Id = id, Name = m.Name, MinState = next, MaxState = next + m.StateCount - 1, DefaultState = next + m.DefaultIndex,
                ExplosionResistance = m.ExplosionResistance, Properties = m.Properties,
            });
            for (var k = 0; k < m.StateCount; k++, next++)
            {
                if (!shapeIds.TryGetValue(m.Shapes[k], out var sid))
                {
                    sid = shapes.Count;
                    shapes.Add(m.Shapes[k]);
                    shapeIds[m.Shapes[k]] = sid;
                }
                stateBlock[next] = (ushort)id;
                hardness[next] = m.Hardness[k];
                flags[next] = m.Flags[k];
                shape[next] = (ushort)sid;
                friction[next] = m.Friction[k];
                speed[next] = m.SpeedFactor[k];
                jump[next] = m.JumpFactor[k];
                fluid[next] = m.FluidAmount[k];
            }
        }
        // The unknown stand-in moves to the end (its id is the first one no server block has).
        var unknown = Blocks[^1];
        blocks.Add(new BlockType { Id = blocks.Count, Name = unknown.Name, MinState = known, MaxState = known, DefaultState = known, ExplosionResistance = unknown.ExplosionResistance });
        stateBlock[known] = (ushort)(blocks.Count - 1);
        hardness[known] = Hardness[UnknownState];
        flags[known] = StateFlags[UnknownState];
        shape[known] = StateShape[UnknownState];
        friction[known] = Friction[UnknownState];
        speed[known] = SpeedFactor[UnknownState];
        jump[known] = JumpFactor[UnknownState];

        var data = new GameData
        {
            Version = Version, Blocks = blocks.ToArray(), Items = Items, Entities = Entities, Recipes = Recipes,
            Registries = Registries, PlayerAttributes = PlayerAttributes,
            StateBlock = stateBlock, Hardness = hardness, StateFlags = flags, StateShape = shape, Friction = friction,
            SpeedFactor = speed, JumpFactor = jump, FluidAmount = fluid, Shapes = shapes.ToArray(),
            VanillaStateCount = VanillaStateCount, KnownStateCount = known, ModdedBlockCount = added.Count,
        };
        data._blocksByName = data.Blocks.ToDictionary(b => b.Name);
        data._itemsByName = _itemsByName;
        data._entitiesByName = _entitiesByName;
        data._itemTags = _itemTags;
        data._blockTags = _blockTags;
        return data;
    }

    /// <summary>
    /// This table with the block tags a server sent (update_tags: tag -> raw block ids, already flattened), which on
    /// a modded server put modded blocks in vanilla tags (a mod's doors in minecraft:wooden_doors, its ladders in
    /// minecraft:climbable). Ids beyond this table's blocks are dropped.
    /// </summary>
    public GameData WithBlockTags(IReadOnlyDictionary<string, int[]> tags)
    {
        var resolved = new Dictionary<string, HashSet<string>>(_blockTags);
        foreach (var (tag, ids) in tags)
            resolved[Normalize(tag)] = ids.Where(i => (uint)i < (uint)(Blocks.Length - 1)).Select(i => Blocks[i].Name).ToHashSet();
        var data = (GameData)MemberwiseClone();
        data._blockTags = resolved;
        return data;
    }
}
