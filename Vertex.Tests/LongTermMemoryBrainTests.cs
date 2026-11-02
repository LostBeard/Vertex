using System.Collections.Concurrent;
using SpawnDev.Minecraft;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Memory;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>The words a memory line is made of, and how long ago is said. Plain.</summary>
public class MemoryTextTests
{
    [Test]
    public void AnEpisode_SaysWhatHappened_TheResult_AndWhere()
    {
        var ok = VertexBrain.EpisodeText(new TaskOutcome("building a house", true, "I built a 5 by 5 wooden house."), (191.4, 63, -628.6));
        Assert.That(ok, Is.EqualTo("I finished building a house: I built a 5 by 5 wooden house. (at 191 63 -629)"));
        var bad = VertexBrain.EpisodeText(new TaskOutcome("planting a field of carrots", false, "I don't have any carrot to plant."), (0, 64, 0));
        Assert.That(bad, Does.StartWith("I tried planting a field of carrots, but it did not work: I don't have any carrot to plant."));
    }

    [TestCase(20, "just now")]
    [TestCase(20 * 60, "20 minutes ago")]
    [TestCase(3600, "1 hour ago")]
    [TestCase(3 * 3600, "3 hours ago")]
    [TestCase(86400, "1 day ago")]
    [TestCase(2.5 * 86400, "2 days ago")]
    public void HowLongAgo_IsSaidPlainly(double seconds, string expected) => Assert.That(VertexBrain.Ago(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
}

/// <summary>
/// The real thing: a job ends and is written to his long-term memory; the memory is closed and opened again from its files (a restart); a friend asks what he built, and the real model
/// answers from the memory. The detail is one he cannot guess ("a purple glass lighthouse"); the control has no memory and must not know it. Real Paper server, real model, real files.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class LongTermMemoryBrainTests
{
    private const int Port = 25990;
    private const string Scope = "127.0.0.1:25990";
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(4);
    private PaperServer _server = null!;
    private VertexAi _ai = null!;
    private string _model = "";
    private readonly List<string> _folders = [];

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("longterm", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        _ai = TestAi.Create();
        _model = TestModel.Pick(await _ai.ModelNamesAsync());
        await _ai.EnsureChatReadyAsync(_model);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_ai != null) await _ai.DisposeAsync();
        if (_server != null) await _server.DisposeAsync();
        foreach (var f in _folders) { try { if (Directory.Exists(f)) Directory.Delete(f, true); } catch (IOException) { } }
    }

    private static string ReadShared(string path)
    {
        try { using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var r = new StreamReader(s); return r.ReadToEnd(); }
        catch (IOException) { return ""; }
    }

    private string NewFolder() { var f = Path.Combine(Path.GetTempPath(), $"vertex-tests-longterm-{Guid.NewGuid():N}"); _folders.Add(f); return f; }

    /// <summary>One "session": Vertex joins as <paramref name="name"/> with the given memory, a friend beside him; returns what Vertex said to a question once asked.</summary>
    private async Task<string> AskAsync(string name, string root, VertexMemory? memory, string question)
    {
        var client = new MinecraftClient(GameProfile.Offline(name));
        using var cts = new CancellationTokenSource(Slow);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        await using var brain = new VertexBrain(client, null, _ai, new CharacterStore(Path.Combine(root, "characters")), Character.BuiltIn[0], _model) { LongTerm = memory, MemoryScope = Scope };
        await using var _c = client;
        await using var friend = new MinecraftClient(GameProfile.Offline("Pal" + name[^5..])); // a Minecraft name is at most 16 characters
        await friend.ConnectAsync("127.0.0.1", Port, cts.Token);
        var heard = new ConcurrentQueue<ChatMessage>();
        friend.ChatReceived += heard.Enqueue;
        var log = new ConcurrentQueue<string>();
        brain.Log += m => { log.Enqueue(m); TestContext.Out.WriteLine(m); };
        brain.Start();
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("ready:")), Slow, () => "never ready; log: " + string.Join(" | ", log.TakeLast(5)));
        await Task.Delay(TimeSpan.FromSeconds(3));
        await friend.SendChatAsync(question);
        await Wait.UntilAsync(() => heard.Any(m => m.SenderId == client.Profile.Id && m.Content.Length > 3), Slow, () => "he never answered; log: " + string.Join(" | ", log.TakeLast(6)));
        await Task.Delay(TimeSpan.FromSeconds(4)); // a long answer arrives in chunks
        var said = string.Join(" ", heard.Where(m => m.SenderId == client.Profile.Id).Select(m => m.Content));
        TestContext.Out.WriteLine("HE SAID: " + said);
        return said;
    }

    [Test]
    public async Task WhatHeBuilt_IsRecorded_SurvivesARestart_AndHeAnswersFromIt([Values(1, 2, 3, 4, 5)] int attempt)
    {
        var root = NewFolder();
        var memoryFolder = Path.Combine(root, "memory");

        // Session one: a job ends. (The job is a stand-in with a result nobody could guess; the recording is the brain's own.)
        using (var first = await _ai.OpenMemoryAsync(memoryFolder))
        {
            var client = new MinecraftClient(GameProfile.Offline("VertexMemOne" + attempt));
            using var cts = new CancellationTokenSource(Slow);
            await client.ConnectAsync("127.0.0.1", Port, cts.Token);
            await using var brain = new VertexBrain(client, null, _ai, new CharacterStore(Path.Combine(root, "characters")), Character.BuiltIn[0], _model) { LongTerm = first, MemoryScope = Scope };
            await using var _c = client;
            brain.Start();
            brain.Tasks.Start("building a lighthouse", _ => Task.FromResult((true, "I built a purple glass lighthouse for Aubs by the river")));
            await Wait.UntilAsync(() => first.Store.Count == 1, Slow, () => "nothing was written to the memory");
            Assert.That(first.Store.List()[0].Text, Does.Contain("purple glass lighthouse").And.Contain("building a lighthouse"));
            // The entry is in the store a moment before the brain's background save has put it on disk; closing and reopening in between loses it (seen 2026-11-02: reopened with 0).
            // A restart is a real thing, so what is waited for is the file.
            var metaFile = Path.Combine(memoryFolder, "memory.json");
            await Wait.UntilAsync(() => File.Exists(metaFile) && File.Exists(Path.Combine(memoryFolder, "memory.vectors")) && ReadShared(metaFile).Contains("lighthouse"), TimeSpan.FromSeconds(30), () => "the memory never reached the disk");
        }

        // Session two: reopened from the files, a friend asks.
        using var second = await _ai.OpenMemoryAsync(memoryFolder);
        Assert.That(second.Store.Count, Is.EqualTo(1), "the memory came back from its files");
        var said = await AskAsync("VertexMemTwo" + attempt, root, second, "Vertex, what did you build for Aubs earlier?");
        Assert.That(said.ToLowerInvariant(), Does.Contain("lighthouse"), "he answered from his memory: " + said);
        // And as himself: "You remember that you built a lighthouse" (seen on the first try) puts his own deed on the friend.
        Assert.That(said.ToLowerInvariant(), Does.Not.Contain("you built").And.Not.Contain("you remember").And.Not.Contain("you are still"), "he spoke of it as his own: " + said);
    }

    [Test]
    public async Task WithoutTheMemory_HeCannotKnowWhatHeBuilt()
    {
        var said = await AskAsync("VertexMemNone", NewFolder(), null, "Vertex, what did you build for Aubs earlier?");
        Assert.That(said.ToLowerInvariant(), Does.Not.Contain("lighthouse"), "the word comes from the memory, nowhere else: " + said);
    }
}
