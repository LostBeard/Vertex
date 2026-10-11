using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SpawnDev.Minecraft.Data;

namespace Vertex.Mods;

/// <summary>
/// Learns a modded server's blocks the way a player gets them: from the server's modpack. Reads a Modrinth pack
/// (.mrpack, a URL or a file: AubsCraft serves one per Minecraft version), downloads its mods (hash-checked, cached),
/// and runs the dump mod Vertex ships (tools/ModDump builds it) once in a throwaway Fabric server with them. The
/// facts come from the mods' own code; the dump's vanilla half must match our table state for state before any of
/// it is trusted. Needs Java: a Temurin JDK is downloaded once (SHA-256 checked) if there is none.
/// </summary>
public sealed class ModpackBlocks(string cacheRoot, Action<string>? log = null)
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private const string JdkRelease = "jdk-21.0.12.1+1";

    /// <summary>%LOCALAPPDATA%\Vertex: downloaded mods, the Fabric server and the JDK.</summary>
    public static string DefaultCache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vertex");

    private void Log(string m) => log?.Invoke(m);

    /// <summary>The modded blocks, items and mobs of a pack's mods (the vanilla check passed).</summary>
    public async Task<ModdedContent> DumpAsync(string pack, CancellationToken ct = default)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LostBeard-Vertex/1.0 (+https://github.com/LostBeard/Vertex)");
            var (index, source) = await ReadPackAsync(http, pack, ct);
            var minecraft = index["dependencies"]!["minecraft"]!.GetValue<string>();
            var loader = index["dependencies"]!["fabric-loader"]?.GetValue<string>()
                ?? throw new InvalidDataException("not a Fabric pack (no fabric-loader dependency)");
            if (minecraft != GameData.Vanilla.Version) throw new InvalidDataException($"the pack is for Minecraft {minecraft}; Vertex speaks {GameData.Vanilla.Version}");

            var jars = new List<string>();
            foreach (var f in index["files"]!.AsArray())
            {
                var path = f!["path"]!.GetValue<string>();
                if (!path.StartsWith("mods/") || !path.EndsWith(".jar")) continue;
                jars.Add(await ModAsync(http, Path.GetFileName(path), f["hashes"]!["sha1"]!.GetValue<string>(), f["downloads"]!.AsArray()[0]!.GetValue<string>(), ct));
            }
            Log($"{source}: Minecraft {minecraft}, Fabric loader {loader}, {jars.Count} mods");

            var java = await JavaAsync(http, ct);
            var server = await FabricServerAsync(http, minecraft, loader, ct);
            var dump = await RunDumpAsync(java, server, jars, ct);
            var content = ModDump.ReadContent(dump, GameData.Vanilla);
            Log($"dumped {content.Blocks.Count} modded blocks ({content.Blocks.Values.Sum(b => b.StateCount)} states), {content.Items.Count} items, "
                + $"{content.Entities.Count} mobs from {source}");
            return content;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>The pack's modrinth.index.json, from a URL or a file.</summary>
    private static async Task<(JsonNode Index, string Source)> ReadPackAsync(HttpClient http, string pack, CancellationToken ct)
    {
        byte[] bytes = pack.StartsWith("http://") || pack.StartsWith("https://")
            ? await http.GetByteArrayAsync(pack, ct)
            : await File.ReadAllBytesAsync(pack, ct);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("modrinth.index.json") ?? throw new InvalidDataException($"{pack} is not a Modrinth pack (no modrinth.index.json)");
        using var s = entry.Open();
        var index = await JsonNode.ParseAsync(s, cancellationToken: ct) ?? throw new InvalidDataException("empty pack index");
        return (index, index["name"]?.GetValue<string>() ?? Path.GetFileName(pack));
    }

    /// <summary>One mod jar, cached by its SHA-1 (as the pack lists it) and checked after every download.</summary>
    private async Task<string> ModAsync(HttpClient http, string fileName, string sha1, string url, CancellationToken ct)
    {
        var dir = Path.Combine(cacheRoot, "mods", sha1);
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path) && Sha1(path) == sha1) return path;
        Directory.CreateDirectory(dir);
        Log($"downloading {fileName}");
        await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(url, ct), ct);
        if (Sha1(path) != sha1)
        {
            File.Delete(path);
            throw new InvalidDataException($"{fileName}: SHA-1 does not match the pack");
        }
        return path;
    }

    private static string Sha1(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(s));
    }

    /// <summary>A Temurin JDK (Fabric 1.21.5 needs Java 21), unzipped once under the cache, SHA-256 checked.</summary>
    private async Task<string> JavaAsync(HttpClient http, CancellationToken ct)
    {
        var java = Path.Combine(cacheRoot, JdkRelease, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (File.Exists(java)) return java;
        Log($"downloading Java ({JdkRelease})");
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";
        var meta = JsonNode.Parse(await http.GetStringAsync(
            $"https://api.adoptium.net/v3/assets/release_name/eclipse/{JdkRelease}?architecture=x64&image_type=jdk&os={os}", ct))!;
        var package = meta["binaries"]![0]!["package"]!;
        var archive = Path.Combine(cacheRoot, package["name"]!.GetValue<string>());
        Directory.CreateDirectory(cacheRoot);
        await File.WriteAllBytesAsync(archive, await http.GetByteArrayAsync(package["link"]!.GetValue<string>(), ct), ct);
        string sha;
        using (var s = File.OpenRead(archive)) sha = Convert.ToHexStringLower(SHA256.HashData(s));
        if (sha != package["checksum"]!.GetValue<string>()) { File.Delete(archive); throw new InvalidDataException("JDK SHA-256 mismatch"); }
        if (archive.EndsWith(".zip")) ZipFile.ExtractToDirectory(archive, cacheRoot, overwriteFiles: true);
        else await RunAsync("tar", ["-xzf", archive, "-C", cacheRoot], cacheRoot, TimeSpan.FromMinutes(5), ct);
        File.Delete(archive);
        return java;
    }

    /// <summary>
    /// A Fabric server for this Minecraft and loader version (Fabric's own server launcher from meta.fabricmc.net,
    /// which fetches the vanilla server and libraries itself on its first run). Kept and reused: each dump only
    /// swaps the mods and starts from a fresh world.
    /// </summary>
    private async Task<string> FabricServerAsync(HttpClient http, string minecraft, string loader, CancellationToken ct)
    {
        var dir = Path.Combine(cacheRoot, "fabric-server", $"{minecraft}-{loader}");
        var launcher = Path.Combine(dir, "fabric-server-launch.jar");
        if (File.Exists(launcher)) return dir;
        Directory.CreateDirectory(dir);
        var installers = JsonNode.Parse(await http.GetStringAsync("https://meta.fabricmc.net/v2/versions/installer", ct))!.AsArray();
        var installer = installers.First(i => i!["stable"]!.GetValue<bool>())!["version"]!.GetValue<string>();
        Log($"downloading the Fabric {loader} server for {minecraft}");
        await File.WriteAllBytesAsync(launcher + ".part",
            await http.GetByteArrayAsync($"https://meta.fabricmc.net/v2/versions/loader/{minecraft}/{loader}/{installer}/server/jar", ct), ct);
        File.Move(launcher + ".part", launcher);
        return dir;
    }

    private async Task<JsonNode> RunDumpAsync(string java, string server, IReadOnlyList<string> jars, CancellationToken ct)
    {
        var mods = Path.Combine(server, "mods");
        if (Directory.Exists(mods)) Directory.Delete(mods, recursive: true);
        Directory.CreateDirectory(mods);
        foreach (var jar in jars) File.Copy(jar, Path.Combine(mods, Path.GetFileName(jar)));
        await using (var dumpMod = typeof(ModpackBlocks).Assembly.GetManifestResourceStream("Vertex.Mods.vertex-moddump.jar")
            ?? throw new InvalidOperationException("the dump mod is not embedded (run tools/ModDump)"))
        await using (var dst = File.Create(Path.Combine(mods, "vertex-moddump.jar")))
            await dumpMod.CopyToAsync(dst, ct);
        foreach (var d in new[] { "world", "logs", "crash-reports" })
            if (Directory.Exists(Path.Combine(server, d))) Directory.Delete(Path.Combine(server, d), recursive: true);
        await File.WriteAllTextAsync(Path.Combine(server, "eula.txt"), "eula=true\n", ct);
        await File.WriteAllTextAsync(Path.Combine(server, "server.properties"),
            "server-ip=127.0.0.1\nserver-port=0\nonline-mode=false\nlevel-type=minecraft\\:flat\nview-distance=2\nsimulation-distance=2\n", ct);
        var dumpFile = Path.Combine(server, "vertex-dump.json");
        File.Delete(dumpFile);
        Log("running the modpack in a throwaway Fabric server to read its blocks");
        var output = await RunAsync(java, ["-Xmx2G", "-Dvertex.moddump=" + dumpFile, "-jar", "fabric-server-launch.jar", "nogui"], server, TimeSpan.FromMinutes(15), ct);
        if (!output.Contains("VERTEX-MODDUMP-DONE 0") || !File.Exists(dumpFile))
            throw new InvalidOperationException("the modpack's server did not finish the dump:\n" + string.Join('\n', output.Split('\n').TakeLast(30)));
        return JsonNode.Parse(await File.ReadAllTextAsync(dumpFile, ct))!;
    }

    private static async Task<string> RunAsync(string exe, string[] arguments, string cwd, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(exe + " did not start");
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try { await p.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true); // our own process, by handle
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish in {timeout}");
        }
        lock (output) return output.ToString();
    }

    // ---------------------------------------------------------------- what a world has learned

    /// <summary>The modded content learned for one world (server), saved next to its world memory, or null.</summary>
    public static ModdedContent? LoadWorld(string file)
    {
        if (!File.Exists(file)) return null;
        try { return ModdedContent.Read(JsonNode.Parse(File.ReadAllText(file))!); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or NullReferenceException or InvalidOperationException) { return null; }
    }

    public static void SaveWorld(string file, ModdedContent content, string source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, content.ToCatalog([$"{DateTime.UtcNow:yyyy-MM-dd} {source}"]).ToJsonString());
        File.Move(temp, file, overwrite: true);
    }
}
