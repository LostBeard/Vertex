using SpawnDev.Minecraft.Auth;

namespace Vertex.Tests;

/// <summary>
/// The dashboard said "Microsoft account (signed in)" on a PC that had never signed in (seen 2026-11-01 on Aubs' PC). It now asks the account store, which has to tell a saved sign-in this
/// Windows user can open from a file it cannot (another user's or another PC's, which DPAPI will not decrypt). Real DPAPI, a throwaway folder.
/// </summary>
public class AccountStoreTests
{
    private string _dir = "";

    [SetUp] public void Dir() => _dir = Path.Combine(Path.GetTempPath(), "vertex-accounts-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Clean() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { } }

    [Test]
    public async Task CanRead_IsFalseWithNoFile_TrueAfterASave_AndFalseAgainAfterASignOut()
    {
        var store = new DpapiAccountStore(Path.Combine(_dir, "accounts", "default.bin"));
        Assert.That(store.CanRead(), Is.False, "a PC that never signed in");
        await store.SaveAsync("{\"refresh\":\"not a real token\"}");
        Assert.That(store.CanRead(), Is.True, "a saved sign-in this Windows user can open");
        await store.DeleteAsync();
        Assert.That(store.CanRead(), Is.False, "signed out");
    }

    [Test]
    public void CanRead_IsFalseForAFileThisUserCannotDecrypt_LikeOneCopiedFromAnotherPc()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "accounts"));
        var path = Path.Combine(_dir, "accounts", "default.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray());
        var store = new DpapiAccountStore(path);
        Assert.That(File.Exists(path), Is.True);
        Assert.That(store.CanRead(), Is.False, "the file is there, but it is not a sign-in this account can use");
    }
}
