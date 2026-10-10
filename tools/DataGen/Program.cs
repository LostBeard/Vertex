using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Regenerates SpawnDev.Minecraft's game tables from the game itself, so no packet id, block state, hardness or
// collision box is ever hand-typed. Usage:
//   dotnet run --project tools/DataGen -- <java.exe> <a Paper server folder that has run once> <minecraft-data pc/<v> folder>
// The Paper folder supplies cache/mojang_<v>.jar (vanilla bundler: data reports, translations, recipes, tags) and
// versions/<v>/paper-<v>.jar + libraries/ (Mojang-mapped classes: java/VertexDump.java bootstraps the registries
// and dumps hardness, collision shapes, friction, fluids, entity sizes, block items).
// minecraft-data (MIT) supplies protocol.json's type definitions, used to decode item stacks and entity metadata.
// Writes: Protocol/PacketIds.g.cs, Data/en_us.json.br, Data/game-data.json.br, Data/protocol-types.json.br.

if (args.Length != 3)
{
    Console.WriteLine("usage: DataGen <java.exe> <paper server folder> <minecraft-data pc/<version> folder>");
    return 2;
}
var java = args[0];
var paperDir = args[1];
var mcData = args[2];
var repo = FindRepoRoot();
var dataDir = Path.Combine(repo, "SpawnDev.Minecraft", "Data");
Directory.CreateDirectory(dataDir);

var bundler = Directory.GetFiles(Path.Combine(paperDir, "cache"), "mojang_*.jar").Single();
var version = ReadVersion(bundler);
var paperJar = Path.Combine(paperDir, "versions", version, $"paper-{version}.jar");
if (!File.Exists(paperJar)) throw new FileNotFoundException("Paper jar not found", paperJar);

var work = Path.Combine(Path.GetTempPath(), "vertex-datagen-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
try
{
    // 1. Vanilla data reports.
    Run(java, ["-DbundlerMainClass=net.minecraft.data.Main", "-jar", Path.GetFullPath(bundler), "--reports", "--output", Path.Combine(work, "out")], work);
    var reports = Path.Combine(work, "out", "reports");

    // 2. Facts the reports leave out, from the game's own registries.
    var classpath = string.Join(Path.PathSeparator, new[] { paperJar }.Concat(Directory.GetFiles(Path.Combine(paperDir, "libraries"), "*.jar", SearchOption.AllDirectories)));
    var dumpFile = Path.Combine(work, "dump.json");
    Run(java, ["-cp", classpath, Path.Combine(repo, "tools", "DataGen", "java", "VertexDump.java"), dumpFile], work);
    var dump = JsonNode.Parse(File.ReadAllText(dumpFile))!;

    // 3. The vanilla server jar inside the bundler: translations, recipes, tags.
    using var bundlerZip = ZipFile.OpenRead(bundler);
    using var innerStream = new MemoryStream();
    using (var s = bundlerZip.GetEntry($"META-INF/versions/{version}/server-{version}.jar")!.Open()) s.CopyTo(innerStream);
    innerStream.Position = 0;
    using var server = new ZipArchive(innerStream);

    File.WriteAllText(Path.Combine(repo, "SpawnDev.Minecraft", "Protocol", "PacketIds.g.cs"), PacketIds(Path.Combine(reports, "packets.json"), version));
    using (var lang = server.GetEntry("assets/minecraft/lang/en_us.json")!.Open())
        WriteBrotli(Path.Combine(dataDir, "en_us.json.br"), lang);

    var gameData = GameData(version, reports, dump, server);
    WriteBrotli(Path.Combine(dataDir, "game-data.json.br"), new MemoryStream(Encoding.UTF8.GetBytes(gameData.ToJsonString())));

    var protocol = JsonNode.Parse(File.ReadAllText(Path.Combine(mcData, "protocol.json")))!;
    var types = new JsonObject { ["version"] = version, ["types"] = protocol["types"]!.DeepClone() };
    WriteBrotli(Path.Combine(dataDir, "protocol-types.json.br"), new MemoryStream(Encoding.UTF8.GetBytes(types.ToJsonString())));

    foreach (var f in Directory.GetFiles(dataDir)) Console.WriteLine($"{Path.GetFileName(f)}: {new FileInfo(f).Length:N0} bytes");
    return 0;
}
finally
{
    Directory.Delete(work, recursive: true);
}

static void Run(string exe, string[] arguments, string cwd)
{
    var psi = new ProcessStartInfo(exe, arguments)
    {
        WorkingDirectory = cwd,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    if (p.ExitCode != 0)
        throw new InvalidOperationException($"{Path.GetFileName(exe)} exited {p.ExitCode}\n{stdout.Result}\n{stderr.Result}");
}

static JsonObject GameData(string version, string reports, JsonNode dump, ZipArchive server)
{
    var blockReport = JsonNode.Parse(File.ReadAllText(Path.Combine(reports, "blocks.json")))!.AsObject();
    var dumpBlocks = dump["blocks"]!.AsArray();
    var states = dump["states"]!.AsArray();

    // Blocks by id. State ids enumerate the properties SORTED BY NAME (vanilla's StateDefinition keeps them in a
    // sorted map), last one fastest. The report lists some blocks' properties unsorted (chest, piston_head...), so
    // sort, then check every state the report lists against that order.
    var blocks = new JsonArray();
    for (var id = 0; id < dumpBlocks.Count; id++)
    {
        var name = dumpBlocks[id]![0]!.GetValue<string>();
        var report = blockReport[name]!;
        var reportStates = report["states"]!.AsArray();
        var min = reportStates.Min(s => s!["id"]!.GetValue<int>());
        var max = reportStates.Max(s => s!["id"]!.GetValue<int>());
        var props = new JsonArray();
        var propNames = new List<string>();
        var propValues = new List<List<string>>();
        if (report["properties"] is JsonObject properties)
            foreach (var (pn, pv) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var values = pv!.AsArray().Select(v => v!.GetValue<string>()).ToList();
                propNames.Add(pn);
                propValues.Add(values);
                props.Add(new JsonArray(pn, new JsonArray(values.Select(v => (JsonNode)v!).ToArray())));
            }
        foreach (var st in reportStates)
        {
            var computed = min;
            var stride = 1;
            for (var p = propNames.Count - 1; p >= 0; p--)
            {
                computed += propValues[p].IndexOf(st!["properties"]![propNames[p]]!.GetValue<string>()) * stride;
                stride *= propValues[p].Count;
            }
            if (computed != st!["id"]!.GetValue<int>()) throw new InvalidDataException($"state order assumption broken for {name}");
            if (dump["states"]![computed]![0]!.GetValue<int>() != id) throw new InvalidDataException($"dump/report disagree at state {computed}");
        }
        blocks.Add(new JsonObject
        {
            ["name"] = name,
            ["min"] = min,
            ["max"] = max,
            ["default"] = dumpBlocks[id]![1]!.GetValue<int>(),
            ["resistance"] = dumpBlocks[id]![2]!.DeepClone(),
            ["props"] = props,
        });
    }

    // Items by protocol id: the components a player cares about, plus the block it places.
    var itemReport = JsonNode.Parse(File.ReadAllText(Path.Combine(reports, "items.json")))!.AsObject();
    var registries = JsonNode.Parse(File.ReadAllText(Path.Combine(reports, "registries.json")))!;
    var itemIds = registries["minecraft:item"]!["entries"]!.AsObject();
    var dumpItems = dump["items"]!.AsArray();
    var keep = new[] { "max_stack_size", "max_damage", "tool", "food", "consumable", "weapon", "equippable", "attribute_modifiers", "use_remainder" };
    var items = new JsonArray();
    for (var id = 0; id < dumpItems.Count; id++)
    {
        var name = dumpItems[id]![0]!.GetValue<string>();
        if (itemIds[name]!["protocol_id"]!.GetValue<int>() != id) throw new InvalidDataException($"item id mismatch for {name}");
        var components = new JsonObject();
        foreach (var (k, v) in itemReport[name]!["components"]!.AsObject())
            if (keep.Contains(k.Replace("minecraft:", ""))) components[k.Replace("minecraft:", "")] = v!.DeepClone();
        var placed = dumpItems[id]![1]!.GetValue<string>();
        var item = new JsonObject { ["name"] = name, ["components"] = components };
        if (placed.Length > 0) item["block"] = placed;
        items.Add(item);
    }

    // Recipes and tags straight from the jar's data pack.
    var recipes = new JsonObject();
    var itemTags = new JsonObject();
    var blockTags = new JsonObject();
    foreach (var entry in server.Entries)
    {
        var path = entry.FullName;
        if (!path.EndsWith(".json", StringComparison.Ordinal)) continue;
        JsonObject? target = null;
        string? key = null;
        if (path.StartsWith("data/minecraft/recipe/", StringComparison.Ordinal)) { target = recipes; key = "minecraft:" + path["data/minecraft/recipe/".Length..^5]; }
        else if (path.StartsWith("data/minecraft/tags/item/", StringComparison.Ordinal)) { target = itemTags; key = "minecraft:" + path["data/minecraft/tags/item/".Length..^5]; }
        else if (path.StartsWith("data/minecraft/tags/block/", StringComparison.Ordinal)) { target = blockTags; key = "minecraft:" + path["data/minecraft/tags/block/".Length..^5]; }
        if (target == null) continue;
        using var s = entry.Open();
        var node = JsonNode.Parse(s)!;
        target[key!] = target == recipes ? node : node["values"]!.DeepClone();
    }

    var entities = new JsonArray();
    var entityIds = registries["minecraft:entity_type"]!["entries"]!.AsObject();
    var dumpEntities = dump["entities"]!.AsArray();
    for (var id = 0; id < dumpEntities.Count; id++)
    {
        var e = dumpEntities[id]!;
        if (entityIds[e[0]!.GetValue<string>()]!["protocol_id"]!.GetValue<int>() != id) throw new InvalidDataException("entity id mismatch");
        entities.Add(e.DeepClone());
    }

    // Static registries whose network ids the protocol uses directly (attribute ids in update_attributes, menu
    // types in open_screen...), as name lists in protocol-id order.
    var staticRegistries = new JsonObject();
    foreach (var reg in new[] { "minecraft:attribute", "minecraft:menu", "minecraft:mob_effect", "minecraft:block_entity_type", "minecraft:sound_event" })
    {
        var entries = registries[reg]!["entries"]!.AsObject().OrderBy(e => e.Value!["protocol_id"]!.GetValue<int>()).ToList();
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Value!["protocol_id"]!.GetValue<int>() != i) throw new InvalidDataException($"{reg} ids not dense");
        staticRegistries[reg] = new JsonArray(entries.Select(e => (JsonNode)e.Key!).ToArray());
    }

    return new JsonObject
    {
        ["version"] = version,
        ["registries"] = staticRegistries,
        ["playerAttributes"] = dump["playerAttributes"]!.DeepClone(),
        ["blocks"] = blocks,
        ["states"] = states.DeepClone(),
        ["shapes"] = dump["shapes"]!.DeepClone(),
        ["items"] = items,
        ["entities"] = entities,
        ["recipes"] = recipes,
        ["itemTags"] = itemTags,
        ["blockTags"] = blockTags,
    };
}

static void WriteBrotli(string target, Stream source)
{
    using var dst = File.Create(target);
    using var brotli = new BrotliStream(dst, CompressionLevel.SmallestSize);
    source.CopyTo(brotli);
}

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir != null && !File.Exists(Path.Combine(dir, "Vertex.slnx"))) dir = Path.GetDirectoryName(dir);
    return dir ?? throw new InvalidOperationException("Vertex.slnx not found above " + AppContext.BaseDirectory);
}

static string ReadVersion(string jar)
{
    // The bundler jar names its inner server jar META-INF/versions/<version>/server-<version>.jar.
    using var zip = ZipFile.OpenRead(jar);
    foreach (var e in zip.Entries)
    {
        var parts = e.FullName.Split('/');
        if (parts.Length == 4 && parts[0] == "META-INF" && parts[1] == "versions" && parts[3].StartsWith("server-", StringComparison.Ordinal))
            return parts[2];
    }
    throw new InvalidDataException("no META-INF/versions/<v>/server-<v>.jar in " + jar);
}

static string PacketIds(string packetsJson, string version)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(packetsJson));
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated>");
    sb.AppendLine($"// Generated by tools/DataGen from the vanilla {version} server's packets.json report. Do not edit.");
    sb.AppendLine("// </auto-generated>");
    sb.AppendLine("namespace SpawnDev.Minecraft.Protocol;");
    sb.AppendLine();
    sb.AppendLine($"/// <summary>Packet ids for Minecraft {version}, straight from the vanilla server's packet report.</summary>");
    sb.AppendLine("public static class PacketIds");
    sb.AppendLine("{");
    foreach (var state in doc.RootElement.EnumerateObject())
    {
        sb.AppendLine($"    public static class {Pascal(state.Name)}");
        sb.AppendLine("    {");
        foreach (var direction in state.Value.EnumerateObject())
        {
            sb.AppendLine($"        public static class {Pascal(direction.Name)}");
            sb.AppendLine("        {");
            foreach (var packet in direction.Value.EnumerateObject().OrderBy(p => p.Value.GetProperty("protocol_id").GetInt32()))
            {
                var name = packet.Name.Split(':')[1];
                sb.AppendLine($"            public const int {Pascal(name)} = 0x{packet.Value.GetProperty("protocol_id").GetInt32():X2};");
            }
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
    }
    sb.AppendLine("}");
    return sb.ToString().ReplaceLineEndings("\r\n");
}

static string Pascal(string snake) =>
    string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
