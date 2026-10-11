using System.Collections.Concurrent;
using SpawnDev.Minecraft;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// M1 against a REAL Paper 1.21.5 server (offline mode, 127.0.0.1): server list ping, login through
/// configuration into play, chat both ways, console messages, keep-alive over time, and kicks. Paper's default
/// compression threshold (256) is on, so the compressed path carries every chunk the server sends.
/// </summary>
[NonParallelizable]
public class LiveConnectTests
{
    private const int Port = 25920;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await PaperServer.StartAsync("connect", Port, TimeSpan.FromMinutes(4));
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    private static MinecraftClient NewClient(string name, List<string>? log = null) =>
        new(GameProfile.Offline(name), new ClientOptions { Log = log == null ? null : m => { lock (log) log.Add(m); } });

    [Test]
    public async Task Ping_ReportsProtocol770()
    {
        var status = await ServerStatus.PingAsync("127.0.0.1", Port);
        Assert.That(status.Protocol, Is.EqualTo(MinecraftClient.ProtocolVersion));
        Assert.That(status.VersionName, Does.Contain("1.21.5"));
        Assert.That(status.PlayersMax, Is.EqualTo(10));
    }

    [Test]
    public async Task Login_SpawnsInTheWorld()
    {
        var log = new List<string>();
        await using var client = NewClient("VertexLogin", log);
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);

        Assert.That(client.State, Is.EqualTo(ConnectionState.Play));
        Assert.That(client.IsSpawned);
        // The server computes the offline UUID itself and sends it back in login_finished.
        Assert.That(client.Profile.Id, Is.EqualTo(GameProfile.OfflineUuid("VertexLogin")));
        Assert.That(client.WorldName, Is.EqualTo("minecraft:overworld"));
        Assert.That(client.GameMode, Is.EqualTo(GameMode.Survival));
        Assert.That(client.Registries.Names, Does.Contain("minecraft:dimension_type").And.Contain("minecraft:chat_type"));
        var overworld = client.Registries.Get("minecraft:dimension_type", "minecraft:overworld");
        Assert.That((overworld?.Value as SpawnDev.Minecraft.Nbt.NbtCompound)?.GetInt("min_y"), Is.EqualTo(-64));
        var (_, y, _) = client.Position;
        Assert.That(y, Is.InRange(-64, 320));
        lock (log) Assert.That(log, Has.Some.EqualTo("compression threshold 256"));

        await _server.WaitForLineAsync(l => l.Contains("VertexLogin joined the game"), Timeout, includeHistory: true);
    }

    [Test]
    public async Task Chat_ReachesTheServerAndOtherPlayers()
    {
        await using var speaker = NewClient("VertexA");
        await using var listener = NewClient("VertexB");
        var heard = new ConcurrentQueue<ChatMessage>();
        listener.ChatReceived += heard.Enqueue;
        using var cts = new CancellationTokenSource(Timeout);
        await speaker.ConnectAsync("127.0.0.1", Port, cts.Token);
        await listener.ConnectAsync("127.0.0.1", Port, cts.Token);

        var text = "hello from vertex " + Guid.NewGuid().ToString("N")[..8];
        var console = _server.WaitForLineAsync(l => l.Contains($"<VertexA> {text}"), Timeout);
        await speaker.SendChatAsync(text);
        await console;

        var message = await WaitForAsync(heard, m => m.Content == text, Timeout);
        // Both players are in each other's player list (player_info_update add + game mode + listed).
        Assert.That(listener.Players.Values.Select(p => p.Name), Does.Contain("VertexA").And.Contain("VertexB"));
        Assert.That(listener.Players[GameProfile.OfflineUuid("VertexA")].GameMode, Is.EqualTo(GameMode.Survival));
        Assert.That(message.Kind, Is.EqualTo(ChatKind.Player));
        Assert.That(message.SenderName, Is.EqualTo("VertexA"));
        Assert.That(message.SenderId, Is.EqualTo(GameProfile.OfflineUuid("VertexA")));
        Assert.That(message.Text, Is.EqualTo($"<VertexA> {text}"));
    }

    [Test]
    public async Task ConsoleSay_IsReceived()
    {
        await using var client = NewClient("VertexSay");
        var heard = new ConcurrentQueue<ChatMessage>();
        client.ChatReceived += heard.Enqueue;
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);

        var text = "console says " + Guid.NewGuid().ToString("N")[..8];
        await _server.CommandAsync("say " + text);
        var message = await WaitForAsync(heard, m => m.Text.Contains(text), Timeout);
        Assert.That(message.Text, Is.EqualTo($"[Server] {text}"));
    }

    [Test]
    public async Task Command_RunsOnTheServer()
    {
        await using var client = NewClient("VertexCmd");
        var heard = new ConcurrentQueue<ChatMessage>();
        client.ChatReceived += heard.Enqueue;
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);

        // /me is available to every player and comes back as chat with the emote decoration.
        var text = "waves " + Guid.NewGuid().ToString("N")[..8];
        await client.SendChatAsync("/me " + text);
        var message = await WaitForAsync(heard, m => m.Text.Contains(text), Timeout);
        Assert.That(message.Text, Is.EqualTo($"* VertexCmd {text}"));
    }

    [Test]
    public async Task KeepAlive_StaysConnectedPastTheTimeout()
    {
        // Paper sends the first keep-alive after 15 s and drops a client that has not answered 30 s later, so a
        // silent client is kicked at ~45 s (red-checked: "lost connection: Timed out" at 45 s). Stay 60 s.
        await using var client = NewClient("VertexStay");
        string? disconnect = null;
        client.Disconnected += r => disconnect = r;
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(60));
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, _server.OutputTail(10)));
        Assert.That(disconnect, Is.Null);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Play));
        await _server.CommandAsync("list");
        await _server.WaitForLineAsync(l => l.Contains("players online") || l.Contains("VertexStay"), Timeout);
        var line = await _server.WaitForLineAsync(l => l.Contains("VertexStay"), Timeout, includeHistory: true);
        Assert.That(line, Does.Not.Contain("left the game"));
    }

    [Test]
    public async Task Kick_RaisesDisconnectedWithTheReason()
    {
        await using var client = NewClient("VertexKick");
        var reason = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += r => reason.TrySetResult(r);
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);

        await _server.CommandAsync("kick VertexKick Go play outside");
        var done = await Task.WhenAny(reason.Task, Task.Delay(Timeout));
        Assert.That(done, Is.SameAs(reason.Task), "no disconnect after the kick");
        Assert.That(await reason.Task, Is.EqualTo("Go play outside"));
        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }

    /// <summary>
    /// The server's real command tree (opped, so it holds /say, /execute, /tp...): finds exactly the arguments a
    /// secure-profile server wants signed, through redirects (/tell -> /msg, but not past /execute ... run, where
    /// the server stops looking). Commands without one must come back empty.
    /// </summary>
    [Test]
    public async Task CommandTree_FindsTheSignableArguments()
    {
        await using var client = NewClient("VertexCmds");
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        await _server.CommandAsync("op VertexCmds");
        // Opping resends the tree with the operator commands in it.
        await Wait.UntilAsync(() => client.Commands?.CommandNames.Contains("say") == true, Timeout,
            () => "no /say in the tree: " + string.Join(" ", client.Commands?.CommandNames ?? []));
        var tree = client.Commands!;
        Assert.That(client.PacketErrors, Is.Zero);

        Assert.That(tree.SignableArguments("msg VertexCmds hello there"), Is.EqualTo(new[] { ("message", "hello there") }));
        Assert.That(tree.SignableArguments("tell VertexCmds hi"), Is.EqualTo(new[] { ("message", "hi") }));
        Assert.That(tree.SignableArguments("w @p psst"), Is.EqualTo(new[] { ("message", "psst") }));
        Assert.That(tree.SignableArguments("me waves at Aubs"), Is.EqualTo(new[] { ("action", "waves at Aubs") }));
        Assert.That(tree.SignableArguments("say hello"), Is.EqualTo(new[] { ("message", "hello") }));
        Assert.That(tree.SignableArguments("teammsg on my way"), Is.EqualTo(new[] { ("message", "on my way") }));
        // The server stops collecting at a redirect back to the root ("run"), so nothing after it is signed...
        Assert.That(tree.SignableArguments("execute as @a[name=VertexCmds, limit=1] at @s run say hi there"), Is.Empty);
        // A selector with a space inside its brackets is one argument.
        Assert.That(tree.SignableArguments("msg @a[name=VertexCmds, limit=1] hi there"), Is.EqualTo(new[] { ("message", "hi there") }));

        Assert.That(tree.SignableArguments("tp 1 2 3"), Is.Empty);
        Assert.That(tree.SignableArguments("tp VertexCmds ~ ~1 ~"), Is.Empty);
        Assert.That(tree.SignableArguments("gamemode creative"), Is.Empty);
        Assert.That(tree.SignableArguments("give VertexCmds minecraft:stone 3"), Is.Empty);
        Assert.That(tree.SignableArguments("time set day"), Is.Empty);
        Assert.That(tree.SignableArguments("nosuchcommand hello"), Is.Empty);
        Assert.That(tree.SignableArguments("say"), Is.Empty, "no message given");
    }

    private static async Task<ChatMessage> WaitForAsync(ConcurrentQueue<ChatMessage> queue, Func<ChatMessage, bool> match, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        var seen = new List<ChatMessage>();
        while (DateTime.UtcNow < until)
        {
            while (queue.TryDequeue(out var m))
            {
                seen.Add(m);
                if (match(m)) return m;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("no matching chat. Received:\n" + string.Join('\n', seen.Select(m => $"{m.Kind}: {m.Text}")));
    }
}
