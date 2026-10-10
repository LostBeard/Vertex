namespace Vertex.Tests.Live;

/// <summary>
/// AubsCraft's "spooky" Fabric 1.21.5 server, as the AubsCraft provisioning tests installed it (Fabric loader,
/// Fabric API and the Halloween content mods: Mutant Monsters, From The Fog, Spooky Doors...), cached under
/// %LOCALAPPDATA%\AubsCraft.Tests\prov-servers\spooky. Copied fresh for each run without the proxy-only mods
/// (FabricProxy-Lite would refuse a direct connection) so Vertex can join it directly in offline mode.
/// </summary>
public static class SpookyServer
{
    public static string Template { get; } = Path.Combine(PaperServer.CacheRoot, "prov-servers", "spooky");

    public static bool Available => File.Exists(Path.Combine(Template, "fabric-server-launch.jar"));

    public static Task<PaperServer> StartAsync(string name, int port, IDictionary<string, string>? properties = null) =>
        PaperServer.StartAsync(name, port, TimeSpan.FromMinutes(6), properties, dir =>
        {
            foreach (var sub in new[] { "libraries", "versions", "mods", "config", "defaultconfigs" })
                CopyDir(Path.Combine(Template, sub), Path.Combine(dir, sub));
            foreach (var f in new[] { "server.jar", "fabric-server-launch.jar", "fabric-server-launcher.properties" })
                File.Copy(Path.Combine(Template, f), Path.Combine(dir, f), overwrite: true);
            foreach (var proxyOnly in Directory.GetFiles(Path.Combine(dir, "mods"), "*.jar")
                .Where(f => Path.GetFileName(f).StartsWith("FabricProxy", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("Floodgate", StringComparison.OrdinalIgnoreCase)))
                File.Delete(proxyOnly);
            return Task.CompletedTask;
        }, launchJar: "fabric-server-launch.jar");

    private static void CopyDir(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
