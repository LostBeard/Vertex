using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.Minecraft.Auth;

/// <summary>Where a signed-in account's tokens are kept between runs. The content is secret: never log it.</summary>
public interface IAccountStore
{
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string json, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps the account file encrypted with Windows DPAPI for the current user: only this Windows account on this
/// machine can read it back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiAccountStore(string path) : IAccountStore
{
    private static readonly byte[] Entropy = "SpawnDev.Minecraft account v1"u8.ToArray();

    public string Path { get; } = path;

    /// <summary>%LOCALAPPDATA%\{app}\accounts\{name}.bin</summary>
    public static DpapiAccountStore ForAccount(string app, string name) => new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), app, "accounts", name + ".bin"));

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path)) return null;
        var bytes = await File.ReadAllBytesAsync(Path, cancellationToken);
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null; // written by another Windows user or machine: sign in again
        }
    }

    public async Task SaveAsync(string json, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
        var tmp = Path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, cancellationToken);
        File.Move(tmp, Path, overwrite: true);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(Path)) File.Delete(Path);
        return Task.CompletedTask;
    }
}
