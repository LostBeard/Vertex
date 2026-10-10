using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using AesNi = System.Runtime.Intrinsics.X86.Aes;

namespace SpawnDev.Minecraft.Net;

/// <summary>
/// AES-128 in CFB8 mode, the stream cipher Minecraft switches to after login (key = IV = the shared secret).
/// Stateful: each call continues the stream, so it must see every byte in order exactly once, in any chunking.
/// </summary>
/// <remarks>
/// .NET's own CFB8 transform only accepts 16-byte multiples (TransformBlock throws on the 1-15 byte tails a
/// socket hands us), so CFB8 is implemented here from its definition: per byte, encrypt the 16-byte shift
/// register, XOR its first byte, shift the ciphertext byte in. With AES-NI that is 10 AESENC per byte; without it,
/// one managed ECB block per byte.
/// </remarks>
public sealed class Cfb8Cipher : IDisposable
{
    private readonly bool _encrypt;
    private readonly Vector128<byte>[]? _roundKeys;      // AES-NI path
    private readonly System.Security.Cryptography.Aes? _aes; // fallback path
    private readonly byte[] _register = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private Vector128<byte> _state;

    public Cfb8Cipher(ReadOnlySpan<byte> sharedSecret, bool encrypt) : this(sharedSecret, encrypt, useAesNi: true) { }

    /// <summary>useAesNi: false forces the managed path (tests check both against a reference).</summary>
    internal Cfb8Cipher(ReadOnlySpan<byte> sharedSecret, bool encrypt, bool useAesNi)
    {
        if (sharedSecret.Length != 16) throw new ArgumentException("the shared secret is 16 bytes", nameof(sharedSecret));
        _encrypt = encrypt;
        sharedSecret.CopyTo(_register); // IV = key
        if (useAesNi && AesNi.IsSupported && Sse2.IsSupported)
        {
            _roundKeys = ExpandKey(Vector128.Create(sharedSecret));
            _state = Vector128.Create(sharedSecret);
        }
        else
        {
            _aes = System.Security.Cryptography.Aes.Create();
            _aes.Key = sharedSecret.ToArray();
        }
    }

    /// <summary>Encrypts or decrypts in place.</summary>
    public void Transform(byte[] buffer, int offset, int count) => Transform(buffer.AsSpan(offset, count));

    /// <summary>Encrypts or decrypts in place.</summary>
    public void Transform(Span<byte> data)
    {
        if (_roundKeys != null) TransformAesNi(data);
        else TransformManaged(data);
    }

    private void TransformAesNi(Span<byte> data)
    {
        var k = _roundKeys!;
        var state = _state;
        for (var i = 0; i < data.Length; i++)
        {
            var s = Sse2.Xor(state, k[0]);
            s = AesNi.Encrypt(s, k[1]);
            s = AesNi.Encrypt(s, k[2]);
            s = AesNi.Encrypt(s, k[3]);
            s = AesNi.Encrypt(s, k[4]);
            s = AesNi.Encrypt(s, k[5]);
            s = AesNi.Encrypt(s, k[6]);
            s = AesNi.Encrypt(s, k[7]);
            s = AesNi.Encrypt(s, k[8]);
            s = AesNi.Encrypt(s, k[9]);
            s = AesNi.EncryptLast(s, k[10]);
            var input = data[i];
            var output = (byte)(input ^ s.ToScalar());
            data[i] = output;
            // Shift the register left one byte (vector lane order: byte 0 is the lowest) and append the ciphertext.
            state = Sse2.ShiftRightLogical128BitLane(state, 1).WithElement(15, _encrypt ? output : input);
        }
        _state = state;
    }

    private void TransformManaged(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            _aes!.EncryptEcb(_register, _keystream, PaddingMode.None);
            var input = data[i];
            var output = (byte)(input ^ _keystream[0]);
            data[i] = output;
            Buffer.BlockCopy(_register, 1, _register, 0, 15);
            _register[15] = _encrypt ? output : input;
        }
    }

    // AES-128 key schedule with AESKEYGENASSIST (the round constant must be an immediate, hence the unrolling).
    private static Vector128<byte>[] ExpandKey(Vector128<byte> key)
    {
        var k = new Vector128<byte>[11];
        k[0] = key;
        k[1] = Next(k[0], AesNi.KeygenAssist(k[0], 0x01));
        k[2] = Next(k[1], AesNi.KeygenAssist(k[1], 0x02));
        k[3] = Next(k[2], AesNi.KeygenAssist(k[2], 0x04));
        k[4] = Next(k[3], AesNi.KeygenAssist(k[3], 0x08));
        k[5] = Next(k[4], AesNi.KeygenAssist(k[4], 0x10));
        k[6] = Next(k[5], AesNi.KeygenAssist(k[5], 0x20));
        k[7] = Next(k[6], AesNi.KeygenAssist(k[6], 0x40));
        k[8] = Next(k[7], AesNi.KeygenAssist(k[7], 0x80));
        k[9] = Next(k[8], AesNi.KeygenAssist(k[8], 0x1B));
        k[10] = Next(k[9], AesNi.KeygenAssist(k[9], 0x36));
        return k;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Next(Vector128<byte> key, Vector128<byte> assist)
    {
        assist = Sse2.Shuffle(assist.AsInt32(), 0xFF).AsByte();
        key = Sse2.Xor(key, Sse2.ShiftLeftLogical128BitLane(key, 4));
        key = Sse2.Xor(key, Sse2.ShiftLeftLogical128BitLane(key, 4));
        key = Sse2.Xor(key, Sse2.ShiftLeftLogical128BitLane(key, 4));
        return Sse2.Xor(key, assist);
    }

    /// <summary>True when this cipher runs on AES-NI.</summary>
    public bool Accelerated => _roundKeys != null;

    public void Dispose() => _aes?.Dispose();
}
