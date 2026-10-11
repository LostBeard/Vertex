using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SpawnDev.Minecraft.Data;

// Dumps a modded (Fabric) server's blocks from the mods' own code, so Vertex knows their shapes, hardness, friction
// and properties instead of treating them as solid unknown cubes. Usage:
//   dotnet run --project tools/ModDump -- <JDK bin folder (java, javac, jar)> <Fabric server folder that has run once> <Paper server folder that has run once>
// Steps (all with what those folders already hold, plus Mojang's published server mappings):
//   1. Mojang's 1.21.5 server mappings (ProGuard format), downloaded and SHA-1 checked. Their license forbids
//      redistributing them: cached under %LOCALAPPDATA%\Vertex.Tools, never committed.
//   2. java/VertexModDump.java (Mojang names, like DataGen's VertexDump) compiled against the Mojang-mapped Paper jar.
//   3. java/RemapToIntermediary.java remaps it to Fabric's intermediary names (the loader's own tiny-remapper and
//      mapping-io; the two mapping sets meet on the official names).
//   4. A throwaway copy of the server runs with it; once started it writes every block and state and halts.
//   5. The vanilla half must equal SpawnDev.Minecraft's vanilla table, state for state (that proves the remap and
//      the state numbering); the modded blocks are merged into SpawnDev.Minecraft/Data/modded-blocks.json.br.

if (args.Length != 3)
{
    Console.WriteLine("usage: ModDump <JDK bin folder> <Fabric server folder> <Paper server folder>");
    return 2;
}
var jdk = args[0];
var fabric = Path.GetFullPath(args[1]);
var paper = Path.GetFullPath(args[2]);
string Tool(string name) => Path.Combine(jdk, OperatingSystem.IsWindows() ? name + ".exe" : name);
var repo = FindRepoRoot();
var javaDir = Path.Combine(repo, "tools", "ModDump", "java");
var output = Path.Combine(repo, "SpawnDev.Minecraft", "Data", "modded-blocks.json.br");
var vanilla = GameData.Vanilla;

var work = Path.Combine(Path.GetTempPath(), "vertex-moddump-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
try
{
    // 1. Mappings.
    var mappings = await MojangServerMappingsAsync(vanilla.Version);

    // 2. Compile against Mojang names.
    var paperJar = Path.Combine(paper, "versions", vanilla.Version, $"paper-{vanilla.Version}.jar");
    var loaderJar = Directory.GetFiles(Path.Combine(fabric, "libraries", "net", "fabricmc", "fabric-loader"), "fabric-loader-*.jar", SearchOption.AllDirectories).Single();
    var intermediaryJar = Directory.GetFiles(Path.Combine(fabric, "libraries", "net", "fabricmc", "intermediary"), "intermediary-*.jar", SearchOption.AllDirectories).Single();
    var compileClasspath = string.Join(Path.PathSeparator, new[] { paperJar, loaderJar }
        .Concat(Directory.GetFiles(Path.Combine(paper, "libraries"), "*.jar", SearchOption.AllDirectories)));
    var classes = Path.Combine(work, "classes");
    Directory.CreateDirectory(classes);
    Run(Tool("javac"), ["--release", "21", "-nowarn", "-cp", compileClasspath, "-d", classes, Path.Combine(javaDir, "VertexModDump.java")], work);
    File.WriteAllText(Path.Combine(classes, "fabric.mod.json"), """
        {"schemaVersion":1,"id":"vertexmoddump","version":"1.0.0","name":"Vertex block dump (tooling, never deployed)","environment":"server",
         "entrypoints":{"server":["VertexModDump"]},"depends":{"fabricloader":"*","fabric-lifecycle-events-v1":"*"}}
        """);
    var named = Path.Combine(work, "named.jar");
    Run(Tool("jar"), ["cf", named, "-C", classes, "."], work);

    // 3. Remap to intermediary.
    var remapped = Path.Combine(work, "vertex-moddump.jar");
    var asm = Directory.GetFiles(Path.Combine(fabric, "libraries", "org", "ow2", "asm"), "*.jar", SearchOption.AllDirectories)
        .GroupBy(j => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(j)))) // one version of each asm artifact: the newest
        .Select(g => g.OrderByDescending(j => Version.Parse(Path.GetFileName(Path.GetDirectoryName(j))!)).First());
    var remapClasspath = string.Join(Path.PathSeparator, new[] { loaderJar }.Concat(asm));
    Run(Tool("java"), ["-cp", remapClasspath, Path.Combine(javaDir, "RemapToIntermediary.java"), mappings, intermediaryJar, named, remapped, paperJar], work);

    // 4. Run the server once with it.
    var server = Path.Combine(work, "server");
    foreach (var sub in new[] { "libraries", "versions", "mods", "config", "defaultconfigs", ".fabric" })
        CopyDir(Path.Combine(fabric, sub), Path.Combine(server, sub));
    foreach (var f in new[] { "server.jar", "fabric-server-launch.jar", "fabric-server-launcher.properties" })
        File.Copy(Path.Combine(fabric, f), Path.Combine(server, f));
    // Proxy-only mods refuse a server that is not behind their proxy; they add no blocks.
    foreach (var jar in Directory.GetFiles(Path.Combine(server, "mods"), "*.jar")
        .Where(j => Path.GetFileName(j).StartsWith("FabricProxy", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(j).StartsWith("Floodgate", StringComparison.OrdinalIgnoreCase)))
        File.Delete(jar);
    var modList = Directory.GetFiles(Path.Combine(server, "mods"), "*.jar").Select(Path.GetFileName).Order().ToList();
    File.Copy(remapped, Path.Combine(server, "mods", "vertex-moddump.jar"));
    File.WriteAllText(Path.Combine(server, "eula.txt"), "eula=true\n");
    File.WriteAllText(Path.Combine(server, "server.properties"),
        "server-ip=127.0.0.1\nserver-port=25947\nonline-mode=false\nlevel-type=minecraft\\:flat\nview-distance=2\nsimulation-distance=2\n");
    var dumpFile = Path.Combine(work, "dump.json");
    var log = Run(Tool("java"), ["-Xmx2G", "-Dvertex.moddump=" + dumpFile, "-jar", "fabric-server-launch.jar", "nogui"], server, TimeSpan.FromMinutes(10));
    if (!log.Contains("VERTEX-MODDUMP-DONE 0") || !File.Exists(dumpFile))
        throw new InvalidOperationException("the dump did not finish:\n" + string.Join('\n', log.Split('\n').TakeLast(40)));

    // 5. Check (inside ModDump.Read), then merge into the catalog.
    var dumped = ModDump.Read(JsonNode.Parse(File.ReadAllText(dumpFile))!, vanilla);
    var existing = File.Exists(output) ? ReadBrotliJson(output) : null;
    var blocks = (existing == null ? new Dictionary<string, ModdedBlock>() : GameData.ReadModdedCatalog(existing).ToDictionary());
    foreach (var m in dumped) blocks[m.Name] = m;
    var sources = existing?["sources"]!.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
    sources.Add($"{DateTime.UtcNow:yyyy-MM-dd} {Path.GetFileName(fabric)}: {string.Join(", ", modList)}");
    using (var dst = File.Create(output))
    using (var brotli = new BrotliStream(dst, CompressionLevel.SmallestSize))
        brotli.Write(Encoding.UTF8.GetBytes(ModDump.ToCatalog(blocks.Values, sources).ToJsonString()));
    // Vertex ships the remapped dump mod, to dump a server's modpack at runtime (no mappings or Paper jar needed there).
    File.Copy(remapped, Path.Combine(repo, "Vertex", "Mods", "vertex-moddump.jar"), overwrite: true);
    Console.WriteLine($"vanilla half matches ({vanilla.VanillaStateCount} states); {dumped.Count} modded blocks from {Path.GetFileName(fabric)}; catalog now {blocks.Count} blocks: {output} ({new FileInfo(output).Length:N0} bytes)");
    return 0;
}
finally
{
    try { Directory.Delete(work, recursive: true); } catch (IOException) { }
}

static async Task<string> MojangServerMappingsAsync(string version)
{
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vertex.Tools");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, $"server-mappings-{version}.txt");
    using var http = new HttpClient();
    var manifest = JsonNode.Parse(await http.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"))!;
    var url = manifest["versions"]!.AsArray().Single(v => v!["id"]!.GetValue<string>() == version)!["url"]!.GetValue<string>();
    var download = JsonNode.Parse(await http.GetStringAsync(url))!["downloads"]!["server_mappings"]!;
    var sha1 = download["sha1"]!.GetValue<string>();
    if (!File.Exists(path) || Sha1(path) != sha1)
    {
        await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(download["url"]!.GetValue<string>()));
        if (Sha1(path) != sha1) throw new InvalidDataException("server mappings SHA-1 mismatch");
    }
    return path;
}

static string Sha1(string path)
{
    using var s = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA1.HashData(s));
}

static JsonNode ReadBrotliJson(string path)
{
    using var s = new BrotliStream(File.OpenRead(path), CompressionMode.Decompress);
    return JsonNode.Parse(s)!;
}

static string Run(string exe, string[] arguments, string cwd, TimeSpan? timeout = null)
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
    if (!p.WaitForExit(timeout ?? TimeSpan.FromMinutes(5)))
    {
        p.Kill(entireProcessTree: true); // our own process, by handle
        throw new TimeoutException($"{Path.GetFileName(exe)} did not finish\n{stdout.Result}");
    }
    if (p.ExitCode != 0)
        throw new InvalidOperationException($"{Path.GetFileName(exe)} exited {p.ExitCode}\n{stdout.Result}\n{stderr.Result}");
    return stdout.Result + stderr.Result;
}

static void CopyDir(string from, string to)
{
    if (!Directory.Exists(from)) return;
    Directory.CreateDirectory(to);
    foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
    foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
}

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir != null && !File.Exists(Path.Combine(dir, "Vertex.slnx"))) dir = Path.GetDirectoryName(dir);
    return dir ?? throw new InvalidOperationException("Vertex.slnx not found above " + AppContext.BaseDirectory);
}
