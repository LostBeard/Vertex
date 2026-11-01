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
