using System.Security.Cryptography;

namespace SpawnDev.Minecraft.Auth;

/// <summary>
/// The account's Mojang-issued chat key pair (api.minecraftservices.com/player/certificates). Servers with
/// enforce-secure-profile=true (Paper's default) drop chat that is not signed with it.
/// </summary>
public sealed record ProfileKeys(byte[] PrivateKeyPkcs8, byte[] PublicKeyDer, byte[] PublicKeySignatureV2, DateTimeOffset ExpiresAt, DateTimeOffset RefreshAfter)
{
    /// <summary>
    /// Mojang labels the PEM blocks "RSA PRIVATE KEY"/"RSA PUBLIC KEY", but the content is PKCS#8 and X.509
    /// SubjectPublicKeyInfo; only the base64 between the armor lines is used.
    /// </summary>
    public static byte[] PemBody(string pem) => Convert.FromBase64String(string.Concat(
        pem.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("-----", StringComparison.Ordinal))));

    public RSA CreatePrivateKey()
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(PrivateKeyPkcs8, out _);
        return rsa;
    }
}
