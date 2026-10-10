using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.Minecraft;

/// <summary>Who the client logs in as. An access token is present only for a signed-in (online-mode) account.</summary>
public sealed record GameProfile(Guid Id, string Name, string? AccessToken = null)
{
    /// <summary>
    /// An offline-mode profile. The UUID is the one the server derives itself: Java's
    /// UUID.nameUUIDFromBytes("OfflinePlayer:" + name), an MD5 version-3 UUID.
    /// </summary>
    public static GameProfile Offline(string name) => new(OfflineUuid(name), name);

    public static Guid OfflineUuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }
}
