using SpawnDev.Minecraft;
using Vertex.Core;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// What Vertex SAYS must never run as a server command. Found 2026-11-01, minutes after Vertex joined mc.spawndev.com with an account that receives operator notices: the text a language
/// model writes (or a player asks it to repeat) went to MinecraftClient.SendChatAsync, which sends anything starting with '/' as a command, so a reply or a chunk of a long reply that began
/// with "/op" or "/gamemode" would have run with the account's permissions. A real Paper server, an operator account, the real MinecraftChannel.
/// </summary>
[NonParallelizable]
public class LiveSlashSafetyTests
{
    private const int Port = 25976;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer() => _server = await PaperServer.StartAsync("slash", Port, TimeSpan.FromMinutes(4));

    [OneTimeTearDown]
    public async Task StopServer() { if (_server != null) await _server.DisposeAsync(); }

    private static async Task<bool> SawAsync(PaperServer server, string text, TimeSpan wait)
    {
        try { await server.WaitForLineAsync(l => l.Contains(text), wait, includeHistory: true); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// 2026-11-03: with a model that answered 1.6 s sooner, Vertex's chat reached about a line a second and Paper kicked it ("Kicked for spamming": the server counts 20 a line against 200,
    /// sinking one a SERVER tick). Lines are now paced. A control channel with no gap sends the same lines at once, so the measure can see a burst.
    /// </summary>
    [Test]
    public async Task ChatLines_AreSentAtLeastTheGapApart_AndWithNoGapTheyComeAsABurst()
    {
        await using var vertex = new MinecraftClient(GameProfile.Offline("VertexPace"));
        await using var listener = new MinecraftClient(GameProfile.Offline("PaceListener"));
        using var cts = new CancellationTokenSource(Timeout);
        await vertex.ConnectAsync("127.0.0.1", Port, cts.Token);
        await listener.ConnectAsync("127.0.0.1", Port, cts.Token);
        var heard = new List<(string Text, long At)>();
        listener.ChatReceived += m => { lock (heard) heard.Add((m.Content, Environment.TickCount64)); };
        await using var paced = new MinecraftChannel(vertex, null, (_, _) => Task.FromResult(""));
        await using var burst = new MinecraftChannel(vertex, null, (_, _) => Task.FromResult("")) { MinChatGap = TimeSpan.Zero };

        for (var i = 1; i <= 5; i++) await paced.ShowTextAsync($"paced {i}", cts.Token);
        for (var i = 1; i <= 5; i++) await burst.ShowTextAsync($"burst {i}", cts.Token);
        await Wait.UntilAsync(() => { lock (heard) return heard.Count(h => h.Text.StartsWith("paced")) == 5 && heard.Count(h => h.Text.StartsWith("burst")) == 5; }, TimeSpan.FromSeconds(30), () => "heard " + string.Join(", ", heard.Select(h => h.Text)));

        List<long> Gaps(string prefix) { lock (heard) { var t = heard.Where(h => h.Text.StartsWith(prefix)).Select(h => h.At).OrderBy(x => x).ToList(); return t.Zip(t.Skip(1), (a, b) => b - a).ToList(); } }
        TestContext.Out.WriteLine("paced gaps (ms): " + string.Join(", ", Gaps("paced")) + "; burst gaps (ms): " + string.Join(", ", Gaps("burst")));
        Assert.That(Gaps("paced").Min(), Is.GreaterThanOrEqualTo(1300), "five lines, each at least about the gap after the one before");
        Assert.That(Gaps("burst").Max(), Is.LessThan(400), "the control: with no gap the same five lines go out together, so the pacing above is what spread them");
    }

    [Test]
    public async Task WhatVertexSays_IsChat_EvenWhenItStartsWithASlash_AndNothingRuns()
    {
        await using var client = new MinecraftClient(GameProfile.Offline("VertexSlash"));
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        await using var channel = new MinecraftChannel(client, null, (_, _) => Task.FromResult(""));
        await _server.CommandAsync("op VertexSlash");

        // CONTROL: through the command path "/me" is an emote, so this test can see a command run.
        await client.SendChatAsync("/me waves control");
        Assert.That(await SawAsync(_server, "* VertexSlash waves control", Timeout), Is.True, "control: /me through SendChatAsync ran as a command (an emote)");

        // What Vertex says: the slash is a character, the line is chat.
        await channel.ShowTextAsync("/me waves said", cts.Token);
        Assert.That(await SawAsync(_server, "<VertexSlash> /me waves said", Timeout), Is.True, "it arrived as chat, slash and all");
        Assert.That(await SawAsync(_server, "* VertexSlash waves said", TimeSpan.FromSeconds(2)), Is.False, "and it did NOT run as /me");

        // The realistic way it happens: a long reply whose SECOND chunk starts with "/" (the split falls on a space before it).
        var long_ = new string('a', 248) + " /gamemode creative";   // the last space before column 250 is the one in front of the slash
        Assert.That(Vertex.AI.Mouth.ChatChunks(long_).Last(), Does.StartWith("/"), "the setup: the second chunk really does begin with a slash");
        await channel.ShowTextAsync(long_, cts.Token);
        Assert.That(await SawAsync(_server, "<VertexSlash> /gamemode creative", Timeout), Is.True, "the second chunk arrived as chat");
        await Task.Delay(1500);
        Assert.That(client.GameMode, Is.EqualTo(GameMode.Survival), "an operator account was NOT switched to creative by something Vertex said");

        // CONTROL for the dangerous case: the same text through the command path does switch it, so the assertion above could have failed.
        await client.SendChatAsync("/gamemode creative");
        await Wait.UntilAsync(() => client.GameMode == GameMode.Creative, Timeout, () => "game mode is " + client.GameMode);
    }
}
