using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.Minecraft.Auth;

/// <summary>Mojang's session server: the client's half of online-mode login.</summary>
public static class SessionServer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// The server hash the client and server both send to the session server: Java's
    /// new BigInteger(sha1(serverId + sharedSecret + publicKey)).toString(16) - a SIGNED two's-complement number
    /// in hex, so it can start with '-' and has no leading zeros.
    /// </summary>
    public static string ServerHash(string serverId, ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> publicKey)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(Encoding.ASCII.GetBytes(serverId)); // vanilla uses ISO-8859-1; the server id is ASCII
        sha1.AppendData(sharedSecret);
        sha1.AppendData(publicKey);
        return JavaHexDigest(sha1.GetHashAndReset());
    }

    public static string JavaHexDigest(ReadOnlySpan<byte> digest)
    {
        var value = new BigInteger(digest, isUnsigned: false, isBigEndian: true);
        if (value.IsZero) return "0";
        var negative = value.Sign < 0;
        var hex = BigInteger.Abs(value).ToString("x").TrimStart('0');
        return negative ? "-" + hex : hex;
    }

    /// <summary>Tells the session server this account is joining the server identified by serverHash.</summary>
    public static async Task JoinAsync(string accessToken, Guid profileId, string serverHash, CancellationToken cancellationToken = default)
    {
        using var response = await Http.PostAsJsonAsync("https://sessionserver.mojang.com/session/minecraft/join", new
        {
            accessToken,
            selectedProfile = profileId.ToString("N"),
            serverId = serverHash,
        }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The body names the reason (e.g. InsufficientPrivilegesException for multiplayer disabled). It never
            // contains the token.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"session server join failed: {(int)response.StatusCode} {body}");
        }
    }
}
