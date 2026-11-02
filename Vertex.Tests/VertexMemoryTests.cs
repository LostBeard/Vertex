using SpawnDev.AI;
using Vertex.AI;
using Vertex.Memory;

namespace Vertex.Tests;

/// <summary>
/// Vertex's long-term memory (Data's AiMemoryStore + AiMemoryEngine, opened on files in Vertex's folder): what is recorded comes back by meaning, in the right world, from the files after a
/// restart, and what is deleted or not allowed to be learned is really gone. Real embedding model, real files. The CPU variants run anywhere (and while Vertex holds the card); the CUDA
/// ones are the real configuration.
/// </summary>
public class VertexMemoryTests
{
    private const string Home = "mc.spawndev.com:25565", Other = "localhost:25565";
    private readonly List<string> _folders = [];

    [OneTimeTearDown]
    public void Cleanup() { foreach (var f in _folders) { try { if (Directory.Exists(f)) Directory.Delete(f, true); } catch (IOException) { } } }

    private string NewFolder()
    {
        var f = Path.Combine(Path.GetTempPath(), $"vertex-tests-memory-{Guid.NewGuid():N}");
        _folders.Add(f);
        return f;
    }

    private static AiMemoryEntry Episode(string text, string scope, float importance = 0.5f, params string[] subjects)
        => new() { Kind = AiMemoryKind.Episode, Text = text, Scope = scope, Source = "minecraft", Importance = importance, Subjects = [.. subjects], Tags = ["task"] };

    private static async Task SeedAsync(VertexMemory memory)
    {
        await memory.RecordAsync(Episode("I built a 5 by 5 wooden house for Aubs near the river at 191 63 -629.", Home, 0.6f, "player:aubs"));
        await memory.RecordAsync(Episode("I mined 12 cobblestone in the cave north of the village.", Home));
        await memory.RecordAsync(Episode("I went fishing at the lake and caught 3 salmon.", Home));
        await memory.RecordAsync(Episode("I explored 40 blocks east and found a village around 310 -602.", Home, 0.7f));
        await memory.RecordAsync(Episode("I planted wheat seeds in a small field behind the house.", Home));
        await memory.RecordAsync(Episode("I built a stone tower on the test server.", Other));
        await memory.RecordAsync(Episode("I made a pen for the cows and sheep out of oak fence.", Home, 0.5f, "player:friend"));
    }

    [TestCase(true)]
    [TestCase(false, Category = "GPU")]
    public async Task WhatIsRecorded_ComesBackByMeaning_InItsOwnWorldOnly(bool cpu)
    {
        await using var ai = TestAi.Create();
        var folder = NewFolder();
        using var memory = await ai.OpenMemoryAsync(folder, cpu);
        await SeedAsync(memory);
        Assert.That(memory.Store.Count, Is.EqualTo(7));

        var house = await memory.RecallAsync("where did we build the house?", Home, 3);
        TestContext.Out.WriteLine("house: " + string.Join(" | ", house.Select(h => $"{h.Score:0.00} {h.Entry.Text}")));
        Assert.That(house[0].Entry.Text, Does.Contain("wooden house"), "the house comes first");

        var village = await memory.RecallAsync("did you ever find a village?", Home, 3);
        Assert.That(village[0].Entry.Text, Does.Contain("found a village"));

        var tower = await memory.RecallAsync("what did you build on the test server?", Home, 7);
        Assert.That(tower.Select(h => h.Entry.Text), Has.None.Contain("stone tower"), "another world's memory never answers here");
        var there = await memory.RecallAsync("what did you build on the test server?", Other, 3);
        Assert.That(there[0].Entry.Text, Does.Contain("stone tower"));
    }

    [TestCase(true)]
    [TestCase(false, Category = "GPU")]
    public async Task TheMemory_SurvivesARestart_AndForgettingAPersonReallyForgets(bool cpu)
    {
        await using var ai = TestAi.Create();
        var folder = NewFolder();
        using (var first = await ai.OpenMemoryAsync(folder, cpu)) await SeedAsync(first);
        Assert.That(File.Exists(Path.Combine(folder, "memory.json")) && File.Exists(Path.Combine(folder, "memory.vectors")), "saved on its own after each record");

        using var memory = await ai.OpenMemoryAsync(folder, cpu);
        Assert.That(memory.Store.Count, Is.EqualTo(7), "everything is back from the files");
        var pen = await memory.RecallAsync("did you build something for the animals?", Home, 2);
        Assert.That(pen[0].Entry.Text, Does.Contain("pen for the cows"));

        // A parent removes a friend: what is about them is gone from the store, from searches and from the files.
        var removed = memory.Store.ForgetSubject("player:friend");
        Assert.That(removed, Has.Count.EqualTo(1));
        await memory.SaveAsync();
        var after = await memory.RecallAsync("did you build something for the animals?", Home, 7);
        Assert.That(after.Select(h => h.Entry.Text), Has.None.Contain("pen for the cows"));
        using var reopened = await ai.OpenMemoryAsync(folder, cpu);
        Assert.That(reopened.Store.Count, Is.EqualTo(6));
        Assert.That(reopened.Store.List().Select(e => e.Text), Has.None.Contain("pen for the cows"), "not in the files either");
    }

    [Test]
    public async Task WhenLearningIsPaused_OrAPersonIsBlocked_NothingIsStored()
    {
        await using var ai = TestAi.Create();
        using var memory = await ai.OpenMemoryAsync(NewFolder(), cpu: true);
        memory.Store.LearningPaused = true;
        Assert.That(await memory.RecordAsync(Episode("I built a house.", Home)), Is.Null);
        memory.Store.LearningPaused = false;
        memory.Store.SetBlocked("player:guest", true);
        Assert.That(await memory.RecordAsync(Episode("The guest asked me to follow.", Home, 0.5f, "player:guest")), Is.Null);
        Assert.That(await memory.RecordAsync(Episode("I built a house.", Home)), Is.Not.Null);
        Assert.That(memory.Store.Count, Is.EqualTo(1));
    }
}
