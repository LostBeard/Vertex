using System.Security.Cryptography;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using SpawnDev.Minecraft.Net;
using SpawnDev.Minecraft.Protocol;

namespace Vertex.Tests;

/// <summary>Wire-format and crypto primitives, checked against published vectors and independent implementations.</summary>
public class ProtocolTests
{
    // The protocol's documented VarInt encodings, including the negative ones (always 5 bytes).
    [TestCase(0, "00")]
    [TestCase(1, "01")]
    [TestCase(127, "7f")]
    [TestCase(128, "8001")]
    [TestCase(255, "ff01")]
    [TestCase(25565, "ddc701")]
    [TestCase(2097151, "ffff7f")]
    [TestCase(2147483647, "ffffffff07")]
    [TestCase(-1, "ffffffff0f")]
    [TestCase(-2147483648, "8080808008")]
    public void VarInt_MatchesProtocolEncoding(int value, string hex)
    {
        using var w = new PacketWriter(0);
        w.WriteVarInt(value);
        var bytes = w.Written[1..].ToArray(); // skip the packet id byte
        Assert.That(Convert.ToHexStringLower(bytes), Is.EqualTo(hex));
        var r = new PacketReader(bytes);
        Assert.That(r.ReadVarInt(), Is.EqualTo(value));
        Assert.That(MinecraftConnection.VarIntSize(value), Is.EqualTo(bytes.Length));
    }

    [Test]
    public void Position_RoundTripsSignedCoordinates()
    {
        foreach (var (x, y, z) in new[] { (0, 0, 0), (-1, -64, -1), (33554431, 2047, -33554432), (18357644, 831, -20882616), (-30000000, -2048, 29999999) })
        {
            using var w = new PacketWriter(0);
            w.WritePosition(x, y, z);
            var r = new PacketReader(w.Written[1..]);
            Assert.That(r.ReadPosition(), Is.EqualTo((x, y, z)));
        }
        // The protocol docs' worked example: 0x4607632C15B4833F = (18357644, 831, -20882616).
        var bytes = Convert.FromHexString("4607632C15B4833F");
        var reader = new PacketReader(bytes);
        Assert.That(reader.ReadPosition(), Is.EqualTo((18357644, 831, -20882616)));
    }

    // NIST SP 800-38A, F.3.7 CFB8-AES128.Encrypt (key 2b7e..., IV 0001...0f).
    [Test]
    public void Cfb8_MatchesNistVector()
    {
        var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        var iv = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        var plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d");
        var expected = "3b79424c9c0dd436bace9e0ed4586a4f32b9";
        // Minecraft uses key == IV, so the NIST vector (different IV) goes through the reference implementation;
        // the production cipher is then checked against that reference with key == IV below.
        Assert.That(Convert.ToHexStringLower(ReferenceCfb8(key, iv, plain, encrypt: true)), Is.EqualTo(expected));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Cfb8_StreamsInAnyChunking_AndMatchesReference(bool aesNi)
    {
        var secret = RandomNumberGenerator.GetBytes(16);
        var data = RandomNumberGenerator.GetBytes(100_003);
        var expected = ReferenceCfb8(secret, secret, data, encrypt: true);

        // Encrypt in uneven chunks, as socket reads/writes arrive.
        var buffer = data.ToArray();
        using (var enc = new Cfb8Cipher(secret, encrypt: true, aesNi))
        {
            Assert.That(enc.Accelerated, Is.EqualTo(aesNi && System.Runtime.Intrinsics.X86.Aes.IsSupported));
            var pos = 0;
            var sizes = new[] { 1, 2, 3, 15, 16, 17, 4096, 70000 };
            for (var i = 0; pos < buffer.Length; i++)
            {
                var n = Math.Min(sizes[i % sizes.Length], buffer.Length - pos);
                enc.Transform(buffer, pos, n);
                pos += n;
            }
        }
        Assert.That(buffer, Is.EqualTo(expected), "encrypt");

        using (var dec = new Cfb8Cipher(secret, encrypt: false, aesNi))
        {
            var pos = 0;
            var sizes = new[] { 7, 1, 33333, 5, 16 };
            for (var i = 0; pos < buffer.Length; i++)
            {
                var n = Math.Min(sizes[i % sizes.Length], buffer.Length - pos);
                dec.Transform(buffer, pos, n);
                pos += n;
            }
        }
        Assert.That(buffer, Is.EqualTo(data), "decrypt");
    }

    [Test]
    public void Cfb8_Throughput()
    {
        var secret = RandomNumberGenerator.GetBytes(16);
        var data = new byte[16 << 20];
        using var cipher = new Cfb8Cipher(secret, encrypt: false);
        cipher.Transform(data.AsSpan(0, 1 << 20)); // warm up (tiered JIT)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cipher.Transform(data);
        sw.Stop();
        TestContext.Out.WriteLine($"CFB8 ({(cipher.Accelerated ? "AES-NI" : "managed")}): {data.Length / sw.Elapsed.TotalSeconds / (1 << 20):0.0} MiB/s");
    }

    /// <summary>CFB8 from its definition: one AES block encryption of the shift register per byte.</summary>
    private static byte[] ReferenceCfb8(byte[] key, byte[] iv, byte[] input, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var register = iv.ToArray();
        var keystream = new byte[16];
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            aes.EncryptEcb(register, keystream, PaddingMode.None);
            output[i] = (byte)(input[i] ^ keystream[0]);
            var cipherByte = encrypt ? output[i] : input[i];
            Buffer.BlockCopy(register, 1, register, 0, 15);
            register[15] = cipherByte;
        }
        return output;
    }

    // The protocol docs' examples of Minecraft's signed-hex SHA-1 digest.
    [TestCase("Notch", "4ed1f46bbe04bc756bcb17c0c7ce3e4632f06a48")]
    [TestCase("jeb_", "-7c9d5b0044c130109a5d7b5fb5c317c02b4e28c1")]
    [TestCase("simon", "88e16a1019277b15d58faf0541e11910eb756f6")]
    public void ServerHash_MatchesJavaDigest(string input, string expected)
    {
        var digest = SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(input));
        Assert.That(SessionServer.JavaHexDigest(digest), Is.EqualTo(expected));
    }

    [Test]
    public void LastSeen_AcknowledgesOldestFirstInJavaBitSetOrder()
    {
        var seen = new LastSeenMessages();
        var empty = seen.TakeUpdate();
        Assert.That(empty.Offset, Is.EqualTo(0));
        Assert.That(empty.Acknowledged, Is.EqualTo(new byte[3]));

        for (var i = 0; i < 3; i++) seen.Add(new byte[256]);
        var update = seen.TakeUpdate();
        Assert.That(update.Offset, Is.EqualTo(3));
        // 20 slots, oldest first; the 3 messages are the newest, slots 17, 18, 19 -> byte 2 bits 1..3.
        Assert.That(update.Acknowledged, Is.EqualTo(new byte[] { 0, 0, 0b0000_1110 }));
        Assert.That(seen.TakeUpdate().Offset, Is.EqualTo(0));
    }
}
