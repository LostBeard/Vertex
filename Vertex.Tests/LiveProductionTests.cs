using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// The real thing: AubsCraft production (mc.spawndev.com, Velocity proxy -> Paper) with the signed-in account.
/// Explicit: only run on purpose (TJ allowed Vertex on AubsCraft and Spooky). Joins quietly, sends no chat,
/// changes nothing, leaves.
/// </summary>
[Explicit("joins the production AubsCraft server with the signed-in account")]
[Category("Production")]
[NonParallelizable]
public class LiveProductionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Test]
    public async Task AubsCraft_JoinLoadsTheWorldAndLeaves()
    {
        var store = DpapiAccountStore.ForAccount("Vertex", "default");
        if (!File.Exists(store.Path)) Assert.Ignore("no account cached (run `vertex login`)");
        var profile = await new MicrosoftAuth(store, _ => throw new AssertionException("needs a device code")).SignInAsync();

        var log = new List<string>();
        await using var client = new MinecraftClient(profile, new ClientOptions { Log = m => { lock (log) log.Add(m); TestContext.Out.WriteLine(m); } });
        string? disconnect = null;
        client.Disconnected += r => disconnect = r;
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("mc.spawndev.com", 25565, cts.Token);

        await Wait.UntilAsync(() => client.World.LoadedChunks >= 50 || disconnect != null, Timeout, () => $"{client.World.LoadedChunks} chunks");
        Assert.That(disconnect, Is.Null);
        // Stand a while: keep-alives, entity traffic, the chat session confirmation.
        await client.WaitTicksAsync(200);
        Assert.That(disconnect, Is.Null, "kicked");
        Assert.That(client.State, Is.EqualTo(ConnectionState.Play));

        var (x, y, z) = client.Position;
        TestContext.Out.WriteLine($"at {x:0.##} {y:0.##} {z:0.##} in {client.World.Dimension}, {client.World.LoadedChunks} chunks, " +
            $"{client.Entities.Count} entities, {client.Players.Count} players listed, health {client.Health}, food {client.Food}");
        TestContext.Out.WriteLine("inventory: " + client.Inventory);
        TestContext.Out.WriteLine("standing on: " + client.World.GetBlock((int)Math.Floor(x), (int)Math.Floor(y) - 1, (int)Math.Floor(z)));
        TestContext.Out.WriteLine("registries: " + string.Join(", ", client.Registries.Names));
        Assert.That(client.Players.ContainsKey(profile.Id));
        Assert.That(client.Players[profile.Id].ChatSessionId, Is.Not.Null, "secure chat session accepted by the proxy/server");
        Assert.That(client.TeleportCount, Is.EqualTo(1), "only the spawn teleport: standing still must not be corrected");
    }

    [Test]
    public async Task AubsCraft_VoiceChatConnectsThroughTheProxy()
    {
        var store = DpapiAccountStore.ForAccount("Vertex", "default");
        if (!File.Exists(store.Path)) Assert.Ignore("no account cached (run `vertex login`)");
        var profile = await new MicrosoftAuth(store, _ => throw new AssertionException("needs a device code")).SignInAsync();

        await using var client = new MinecraftClient(profile);
        await using var voice = new SpawnDev.Minecraft.VoiceChat.SimpleVoiceChat(client, "mc.spawndev.com");
        voice.Log += m => TestContext.Out.WriteLine("[voice] " + m);
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("mc.spawndev.com", 25565, cts.Token);
        await voice.WaitConnectedAsync(cts.Token);
        Assert.That(voice.IsConnected);
        TestContext.Out.WriteLine($"codec {voice.Codec}, distance {voice.VoiceDistance}, groups {voice.GroupsEnabled}, {voice.PlayerStates.Count} voice states");
        await client.WaitTicksAsync(100); // keep-alives over the real network
        Assert.That(voice.IsConnected, "voice stayed up");
    }
}
