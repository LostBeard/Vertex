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

    /// <summary>
    /// How long recording and recalling take, cold (the first of each, which compiles kernels) and warm (the second batch in the same process), on the CPU accelerator and on the card.
    /// Printed, and asserted only for the one thing known to be true: warm is not slower than cold. Numbers go to the library's author when they look wrong.
    /// </summary>
    [TestCase(true)]
    [TestCase(false, Category = "GPU")]
    public async Task Timings_ColdAndWarm(bool cpu)
    {
        await using var ai = TestAi.Create();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var memory = await ai.OpenMemoryAsync(NewFolder(), cpu);
        var open = clock.ElapsedMilliseconds;

        async Task<(long Record, long Recall)> RoundAsync(string[] texts, string query)
        {
            clock.Restart();
            foreach (var t in texts) await memory.RecordAsync(Episode(t, Home));
            var record = clock.ElapsedMilliseconds;
            clock.Restart();
            await memory.RecallAsync(query, Home, 3);
            return (record, clock.ElapsedMilliseconds);
        }

        var cold = await RoundAsync(["I built a house near the river.", "I mined cobblestone in a cave.", "I went fishing at the lake.", "I found a village to the east.", "I planted wheat behind the house.", "I made a pen for the cows.", "I cut ten oak logs."], "where did we build the house?");
        var warm = await RoundAsync(["I built a tower on the hill.", "I mined coal under the mountain.", "I caught salmon in the river.", "I found a temple in the desert.", "I planted carrots by the barn.", "I made a fence around the sheep.", "I cut twelve birch logs."], "did you build a tower?");
        // One more recall alone, the number a player would feel.
        clock.Restart();
        await memory.RecallAsync("what did you fish for?", Home, 3);
        var recallAlone = clock.ElapsedMilliseconds;
        TestContext.Out.WriteLine($"TIMING {(cpu ? "CPU" : "CUDA")}: open {open} ms; cold: 7 records {cold.Record} ms ({cold.Record / 7.0:0} ms each), first recall {cold.Recall} ms; warm: 7 records {warm.Record} ms ({warm.Record / 7.0:0} ms each), recall {warm.Recall} ms; one more recall {recallAlone} ms");
        Assert.That(warm.Record, Is.LessThanOrEqualTo(cold.Record), "the second batch is not slower than the first");
    }

    /// <summary>The cut-off for what is shown to the language model is MEASURED here, not guessed: what people would ask about what is in the memory must score above it, small talk below it.</summary>
    [Test]
    public async Task TheRecallCutoff_SeparatesRelatedQuestions_FromSmallTalk()
    {
        await using var ai = TestAi.Create();
        using var memory = await ai.OpenMemoryAsync(NewFolder(), cpu: true);
        await SeedAsync(memory);
        string[] related =
        [
            "where did we build the house?", "what did you build for me?", "did you ever find a village?", "do you remember the cow pen?", "did you catch any fish?",
            "what did you plant behind the house?", "have you been mining lately?", "what did you do yesterday?",
        ];
        string[] smallTalk = ["hello there!", "what is your favourite colour?", "come here please", "how are you today?", "tell me a joke", "stop", "what time is it?", "can you jump?"];
        var rel = new List<float>(); var small = new List<float>();
        foreach (var q in related) { var h = await memory.RecallAsync(q, Home, 1); rel.Add(h[0].Similarity); TestContext.Out.WriteLine($"related   {h[0].Similarity:0.000}  {q}  -> {h[0].Entry.Text}"); }
        foreach (var q in smallTalk) { var h = await memory.RecallAsync(q, Home, 1); small.Add(h[0].Similarity); TestContext.Out.WriteLine($"small     {h[0].Similarity:0.000}  {q}  -> {h[0].Entry.Text}"); }
        TestContext.Out.WriteLine($"related min {rel.Min():0.000}, small talk max {small.Max():0.000}");
        Assert.That(rel.Min(), Is.GreaterThan(small.Max()), "the two groups do not overlap");
        Assert.That(Vertex.Brain.VertexBrain.MinRecallSimilarity, Is.LessThanOrEqualTo(rel.Min()).And.GreaterThan(small.Max()), "the cut-off sits between them");
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

    /// <summary>The panel's list and its two delete buttons: what is forgotten is gone from the list, from searches AND from the files on disk (read as text, not through the store).</summary>
    [Test]
    public async Task ThePanelCanListAndForget_OneMemory_OrAll_AndTheFilesAreScrubbed()
    {
        await using var ai = TestAi.Create();
        var folder = NewFolder();
        using var memory = await ai.OpenMemoryAsync(folder, cpu: true);
        await memory.RecordAsync(Episode("I built a purple glass lighthouse for Aubs by the river.", Home, 0.5f, "mc:11111111-1111-1111-1111-111111111111"));
        await memory.RecordAsync(Episode("I mined twelve cobblestone in the cave.", Home));
        await memory.RecordAsync(Episode("I went fishing and caught three salmon.", Home));

        var rows = memory.Rows();
        Assert.That(rows, Has.Count.EqualTo(3));
        Assert.That(rows[0].WhenLocal, Is.GreaterThanOrEqualTo(rows[2].WhenLocal), "newest first");
        var lighthouse = rows.Single(r => r.Text.Contains("lighthouse"));
        Assert.That(lighthouse.AboutAPerson, Is.True, "it says it is about a person, without showing who by name");
        Assert.That(rows.Single(r => r.Text.Contains("cobblestone")).AboutAPerson, Is.False);
        var meta = Path.Combine(folder, "memory.json");
        Assert.That(File.ReadAllText(meta), Does.Contain("lighthouse"), "on disk before");

        Assert.That(await memory.ForgetAsync([lighthouse.Id]), Is.EqualTo(1));
        Assert.That(memory.Rows().Select(r => r.Text), Has.None.Contain("lighthouse"));
        Assert.That(File.ReadAllText(meta), Does.Not.Contain("lighthouse"), "scrubbed from the file, not just hidden");
        Assert.That((await memory.RecallAsync("what did you build for Aubs?", Home, 3)).Select(h => h.Entry.Text), Has.None.Contain("lighthouse"), "and not found by a search");
        using (var reopened = await ai.OpenMemoryAsync(folder, cpu: true)) Assert.That(reopened.Store.Count, Is.EqualTo(2), "still two after a restart");

        Assert.That(await memory.ForgetAllAsync(), Is.EqualTo(2));
        Assert.That(memory.Rows(), Is.Empty);
        Assert.That(File.ReadAllText(meta), Does.Not.Contain("cobblestone").And.Not.Contain("salmon"));
        using (var again = await ai.OpenMemoryAsync(folder, cpu: true)) Assert.That(again.Store.Count, Is.EqualTo(0));
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
