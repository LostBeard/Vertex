using System.Text.Json.Nodes;

namespace SpawnDev.Minecraft.Data;

/// <summary>
/// Reads the JSON that tools/ModDump's VertexModDump mod writes inside a modded server (every block and state the
/// server has, vanilla first), checks its vanilla half against our own table, and turns its modded half into
/// <see cref="ModdedBlock"/>s. Shared by the build-time tool and the runtime dump of a server's mods.
/// </summary>
public static class ModDump
{
    /// <summary>The modded blocks in a dump, in the dump server's order.</summary>
    public static List<ModdedBlock> Read(JsonNode dump, GameData vanilla)
    {
        var blocks = dump["blocks"]!.AsArray();
        var states = dump["states"]!.AsArray();
        var shapes = dump["shapes"]!.AsArray().Select(s => s!.AsArray().Select(b =>
        {
            var a = b!.AsArray();
            return new Box(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>(), a[4]!.GetValue<double>(), a[5]!.GetValue<double>());
        }).ToArray()).ToArray();
        CheckVanilla(vanilla, blocks, states, shapes);

        var result = new List<ModdedBlock>();
        for (var i = vanilla.Blocks.Length - 1 - vanilla.ModdedBlockCount; i < blocks.Count; i++)
        {
            var b = blocks[i]!.AsArray();
            var name = b[0]!.GetValue<string>();
            int first = b[1]!.GetValue<int>(), count = b[2]!.GetValue<int>();
            var rows = Enumerable.Range(first, count).Select(s => states[s]!.AsArray()).ToArray();
            if (rows.Any(r => r[0]!.GetValue<int>() != i)) throw new InvalidDataException($"{name}: its states are not all its own");
            result.Add(new ModdedBlock
            {
                Name = name,
                Properties = b[3]!.AsArray().Select(p => new BlockProperty(p![0]!.GetValue<string>(), p[1]!.AsArray().Select(v => v!.GetValue<string>()).ToArray())).ToArray(),
                DefaultIndex = b[4]!.GetValue<int>() - first,
                ExplosionResistance = b[5]!.GetValue<float>(),
                Hardness = rows.Select(r => r[1]!.GetValue<float>()).ToArray(),
                Flags = rows.Select(r => (byte)r[2]!.GetValue<int>()).ToArray(),
                Shapes = rows.Select(r => shapes[r[3]!.GetValue<int>()]).ToArray(),
                Friction = rows.Select(r => r[4]!.GetValue<float>()).ToArray(),
                SpeedFactor = rows.Select(r => r[5]!.GetValue<float>()).ToArray(),
                JumpFactor = rows.Select(r => r[6]!.GetValue<float>()).ToArray(),
                FluidAmount = rows.Select(r => (byte)r[7]!.GetValue<int>()).ToArray(),
            });
        }
        return result;
    }

    /// <summary>
    /// Everything a dump says about the mods: their blocks (Read), their items with the components Vertex keeps
    /// (GameData.KeptItemComponents) and their entity types' sizes. Dumps from before items and entities were
    /// added have none of either.
    /// </summary>
    public static ModdedContent ReadContent(JsonNode dump, GameData vanilla) => new()
    {
        Blocks = Read(dump, vanilla).ToDictionary(b => b.Name),
        Items = (dump["items"] as JsonArray ?? []).Select(i =>
        {
            var components = new JsonObject();
            foreach (var (k, v) in i!["components"]!.AsObject())
                if (GameData.KeptItemComponents.Contains(k)) components[k] = v!.DeepClone();
            var item = new JsonObject { ["name"] = i["name"]!.GetValue<string>(), ["components"] = components };
            if (i["block"] is { } b) item["block"] = b.GetValue<string>();
            return GameData.ParseItem(-1, item);
        }).ToDictionary(i => i.Name),
        Entities = (dump["entities"] as JsonArray ?? []).Select(e => new EntityTypeInfo(-1, e![0]!.GetValue<string>(),
            e[1]!.GetValue<float>(), e[2]!.GetValue<float>(), e[3]!.GetValue<float>())).ToDictionary(e => e.Name),
    };

    /// <summary>
    /// Every vanilla block and state of the dump must be exactly what we know: name, properties, ids, hardness,
    /// flags, collision boxes, friction, speed, jump, fluid. A wrong remap, or a state numbering other than the one we
    /// assume, shows up here before any modded fact is trusted.
    /// </summary>
    public static void CheckVanilla(GameData v, JsonArray blocks, JsonArray states, Box[][] shapes)
    {
        var vanillaBlocks = v.Blocks.Length - 1 - v.ModdedBlockCount;
        for (var i = 0; i < vanillaBlocks; i++)
        {
            var b = blocks[i]!.AsArray();
            var ours = v.Blocks[i];
            var props = b[3]!.AsArray().Select(p => $"{p![0]}=[{string.Join(",", p[1]!.AsArray().Select(x => x!.GetValue<string>()))}]");
            var expected = ours.Properties.Select(p => $"{p.Name}=[{string.Join(",", p.Values)}]");
            if (b[0]!.GetValue<string>() != ours.Name || b[1]!.GetValue<int>() != ours.MinState || b[2]!.GetValue<int>() != ours.MaxState - ours.MinState + 1
                || !props.SequenceEqual(expected) || b[4]!.GetValue<int>() != ours.DefaultState)
                throw new InvalidDataException($"block {i}: dump {b.ToJsonString()} vs ours {ours.Name} {ours.MinState}-{ours.MaxState} [{string.Join(" ", expected)}]");
        }
        for (var s = 0; s < v.VanillaStateCount; s++)
        {
            var st = states[s]!.AsArray();
            var same = st[0]!.GetValue<int>() == v.StateBlock[s] && st[1]!.GetValue<float>() == v.Hardness[s] && st[2]!.GetValue<int>() == v.StateFlags[s]
                && st[4]!.GetValue<float>() == v.Friction[s] && st[5]!.GetValue<float>() == v.SpeedFactor[s] && st[6]!.GetValue<float>() == v.JumpFactor[s]
                && st[7]!.GetValue<int>() == v.FluidAmount[s] && shapes[st[3]!.GetValue<int>()].SequenceEqual(v.CollisionBoxes(s));
            if (!same) throw new InvalidDataException($"state {s} ({v.BlockOf(s).Name}): dump {st.ToJsonString()} differs from the vanilla table");
        }
    }

    /// <summary>Writes blocks as a catalog (the format of Data/modded-blocks.json.br; GameData.ReadModdedCatalog reads it).</summary>
    public static JsonObject ToCatalog(IEnumerable<ModdedBlock> blocks, IEnumerable<string> sources)
    {
        var shapes = new JsonArray();
        var shapeIds = new Dictionary<string, int>();
        var list = new JsonArray();
        foreach (var m in blocks.OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            var rows = new JsonArray();
            for (var k = 0; k < m.StateCount; k++)
            {
                var shape = new JsonArray(m.Shapes[k].Select(b => (JsonNode)new JsonArray(b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ)).ToArray());
                var key = shape.ToJsonString();
                if (!shapeIds.TryGetValue(key, out var id))
                {
                    id = shapes.Count;
                    shapes.Add(shape);
                    shapeIds[key] = id;
                }
                rows.Add(new JsonArray(m.Hardness[k], (int)m.Flags[k], id, m.Friction[k], m.SpeedFactor[k], m.JumpFactor[k], (int)m.FluidAmount[k]));
            }
            list.Add(new JsonObject
            {
                ["name"] = m.Name,
                ["props"] = new JsonArray(m.Properties.Select(p => (JsonNode)new JsonArray(p.Name, new JsonArray(p.Values.Select(v => (JsonNode)v).ToArray()))).ToArray()),
                ["default"] = m.DefaultIndex,
                ["resistance"] = m.ExplosionResistance,
                ["states"] = rows,
            });
        }
        return new JsonObject { ["sources"] = new JsonArray(sources.Select(s => (JsonNode)s).ToArray()), ["shapes"] = shapes, ["blocks"] = list };
    }
}
