using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;

namespace Vertex.Tests.Live;

/// <summary>
/// The Javas the live tests run servers with: Temurin 25.0.4.1 - the same Java the production VM runs (OpenJDK
/// 25.0.4.1; Velocity 4.x needs 25, class file 69) - and Temurin 21 for Forge (the VM has OpenJDK 21 next to 25).
/// Unzipped once under %LOCALAPPDATA%\AubsCraft.Tests (nothing installed, no PATH change), SHA-256 checked.
/// </summary>
public static class Jdk
{
    private static readonly Lazy<Task<string>> JavaExe = new(() => EnsureAsync("jdk-25.0.4.1+1"));
    private static readonly Lazy<Task<string>> Java21Exe = new(() => EnsureAsync("jdk-21.0.12.1+1"));

    public static Task<string> JavaAsync() => JavaExe.Value;

    /// <summary>Java 21 (Forge's runtime).</summary>
    public static Task<string> Java21Async() => Java21Exe.Value;

    private static async Task<string> EnsureAsync(string release)
    {
        var root = PaperServer.CacheRoot;
        var java = Path.Combine(root, release, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (File.Exists(java)) return java;

        using var http = new HttpClient();
        using var meta = JsonDocument.Parse(await http.GetStringAsync(
            $"https://api.adoptium.net/v3/assets/release_name/eclipse/{release}?architecture=x64&image_type=jdk&os=windows"));
        var package = meta.RootElement.GetProperty("binaries")[0].GetProperty("package");
        var zip = Path.Combine(root, release + ".zip");
        await using (var src = await http.GetStreamAsync(package.GetProperty("link").GetString()))
        await using (var dst = File.Create(zip))
            await src.CopyToAsync(dst);
        using (var s = File.OpenRead(zip))
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(s));
            if (sha != package.GetProperty("checksum").GetString())
                throw new InvalidDataException("JDK zip SHA-256 mismatch: " + sha);
        }
        ZipFile.ExtractToDirectory(zip, root, overwriteFiles: true);
        File.Delete(zip);
        return java;
    }
}
