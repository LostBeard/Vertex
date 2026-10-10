using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Online mode with a REAL Microsoft account against a LOCAL Paper server (online-mode=true): the session-server
/// join, RSA key exchange and AES/CFB8 on a live connection. Needs an account signed in once with
/// `vertex login` (cached under DPAPI for the current Windows user); explicit so it never runs by accident.
/// </summary>
[Explicit("needs a signed-in Microsoft account: run `vertex login` first")]
[Category("Account")]
[NonParallelizable]
public class LiveOnlineTests
{
    private const int Port = 25922;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static async Task<GameProfile> CachedAccountAsync()
    {
        var store = DpapiAccountStore.ForAccount("Vertex", "default");
        if (!File.Exists(store.Path)) Assert.Ignore("no account cached at " + store.Path + " (run `vertex login`)");
        // Must come from the cache or a silent refresh: a device-code prompt here means the cache is broken.
        var auth = new MicrosoftAuth(store, _ => throw new AssertionException("sign-in asked for a device code; the cached account was not used"));
        return await auth.SignInAsync();
    }

    [Test]
    public async Task OnlineMode_JoinsEncryptedAndChats()
    {
        var profile = await CachedAccountAsync();
        Assert.That(profile.AccessToken, Is.Not.Null.And.Not.Empty);

        await using var server = await PaperServer.StartAsync("online", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string>
        {
            ["online-mode"] = "true",
            // Chat signing (profile keys) is the next step; this test is about the encrypted, authenticated login.
            ["enforce-secure-profile"] = "false",
        });

        var log = new List<string>();
        await using var client = new MinecraftClient(profile, new ClientOptions { Log = m => { lock (log) log.Add(m); } });
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        lock (log) Assert.That(log, Has.Some.EqualTo("encryption enabled"));
        Assert.That(client.IsSpawned);
        Assert.That(client.Profile.Id, Is.EqualTo(profile.Id));

        // Paper prints the UUID it got from Mojang's hasJoined: proof the session join was verified.
        await server.WaitForLineAsync(l => l.Contains($"UUID of player {profile.Name} is {profile.Id}"), Timeout, includeHistory: true);

        var text = "vertex online " + Guid.NewGuid().ToString("N")[..8];
        var console = server.WaitForLineAsync(l => l.Contains($"<{profile.Name}> {text}"), Timeout);
        await client.SendChatAsync(text);
        await console;
    }

    /// <summary>
    /// Paper's default (and AubsCraft's): enforce-secure-profile=true. Chat must be signed with the account's
    /// Mojang-issued profile key, and a second (offline-key-less) message must chain correctly too.
    /// </summary>
    [Test]
    public async Task SecureProfile_SignedChatIsAccepted()
    {
        var profile = await CachedAccountAsync();
        await using var server = await PaperServer.StartAsync("secure", Port + 1, TimeSpan.FromMinutes(4), new Dictionary<string, string>
        {
            ["online-mode"] = "true",
            ["enforce-secure-profile"] = "true",
        });

        Assert.That(profile.Keys, Is.Not.Null, "the signed-in profile carries no chat key pair");
        var heard = new System.Collections.Concurrent.ConcurrentQueue<ChatMessage>();
        string? disconnect = null;
        var log = new List<string>();
        await using var client = new MinecraftClient(profile, new ClientOptions { Log = m => { lock (log) log.Add(m); } });
        client.ChatReceived += heard.Enqueue;
        client.Disconnected += r => disconnect = r;
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port + 1, cts.Token);
        // No delay on purpose: the first message goes out straight after spawning, the race the client must handle.

        for (var i = 0; i < 3; i++)
        {
            var text = $"signed {i} " + Guid.NewGuid().ToString("N")[..8];
            var console = server.WaitForLineAsync(l => l.Contains($"<{profile.Name}> {text}"), Timeout);
            await client.SendChatAsync(text);
            try { await console; }
            catch (TimeoutException)
            {
                Assert.Fail($"message {i} never reached the server. Disconnect: {disconnect ?? "(none)"}. Chat: "
                    + string.Join(" | ", heard.Select(m => m.Text)) + "\nclient: " + string.Join(" | ", log)
                    + "\n" + string.Join(Environment.NewLine, server.OutputTail(15)));
            }
            // The server echoes the signed message back to everyone, us included, as our player chat.
            var until = DateTime.UtcNow + Timeout;
            while (!heard.Any(m => m.Kind == ChatKind.Player && m.SenderId == profile.Id && m.Content == text) && DateTime.UtcNow < until)
                await Task.Delay(25);
            Assert.That(heard.Any(m => m.Kind == ChatKind.Player && m.SenderId == profile.Id && m.Content == text), $"message {i} was not echoed back");
        }
        Assert.That(disconnect, Is.Null);
        lock (log) Assert.That(log, Has.None.Contains("never confirmed"));
        Assert.That(client.Players[profile.Id].ChatSessionId, Is.Not.Null);
    }
}
