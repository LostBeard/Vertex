using System.Text.Json;
using SpawnDev.AI;
using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>The training pairs: Tuvok's format, and the promise on the privacy page (nobody's words are written).</summary>
public class TrainingLogTests
{
    private static string NewFolder() => Path.Combine(TestContext.CurrentContext.WorkDirectory, $"training-{Guid.NewGuid():N}");

    private static List<AiChatMessage> GameTurn() =>
    [
        new("system", "You are Vertex."),
        new("system", "- In the game you are at x 1 y 2 z 3."),
        new("user", "[Game update, not a friend talking: you just got a stone pickaxe.] Tell your friends in one short sentence."),
    ];

    [Test]
    public void AGameTurn_IsWritten_InTheAgreedFormat()
    {
        var folder = NewFolder();
        try
        {
            var log = new TrainingLog(folder, "session-a");
            Assert.That(log.Record(GameTurn(), ["{\"name\":\"craft\"}"], "I made a stone pickaxe!", "<|start|>system...<|end|>"), Is.True);
            var line = Directory.GetFiles(folder).Single();
            using var doc = JsonDocument.Parse(File.ReadAllLines(line).Single());
            var r = doc.RootElement;
            Assert.That(r.GetProperty("format").GetInt32(), Is.EqualTo(TrainingLog.FormatVersion));
            Assert.That(r.GetProperty("id").GetString(), Is.EqualTo("session-a-1"));
            Assert.That(r.GetProperty("source").GetString(), Is.EqualTo("session-a"));
            Assert.That(r.GetProperty("split").GetString(), Is.EqualTo(TrainingLog.SplitOf("session-a")));
            var target = r.GetProperty("target_turn").GetInt32();
            var message = r.GetProperty("messages")[target];
            Assert.That(message.GetProperty("role").GetString(), Is.EqualTo("assistant"));
            Assert.That(message.GetProperty("content").GetString(), Is.EqualTo("I made a stone pickaxe!"));
            Assert.That(r.GetProperty("truth").GetString(), Does.Contain("you just got a stone pickaxe"));
            Assert.That(r.GetProperty("rendered_prompt").GetString(), Is.EqualTo("<|start|>system...<|end|>"));
            Assert.That(r.GetProperty("rendered_target").GetString(), Is.EqualTo("I made a stone pickaxe!"));
            Assert.That(r.GetProperty("tools")[0].GetString(), Does.Contain("craft"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Test]
    public void TheIdsAreWhatTheModelSaw_TheEndThatWasKept_WhenThePromptWasCut()
    {
        Assert.That(TrainingLog.KeptIds([1, 2, 3, 4, 5], 5), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
        Assert.That(TrainingLog.KeptIds([1, 2, 3, 4, 5], 3), Is.EqualTo(new[] { 3, 4, 5 }), "the decoder drops the oldest");
        var folder = NewFolder();
        try
        {
            new TrainingLog(folder, "session-c").Record(GameTurn(), null, "ok then", "x", false, [7, 8, 9], [10, 11], [99], "v2.2", 128009);
            using var doc = JsonDocument.Parse(File.ReadAllLines(Directory.GetFiles(folder).Single()).Single());
            Assert.That(doc.RootElement.GetProperty("adapter").GetString(), Is.EqualTo("v2.2"), "a later training run must know this is the adapter's own output");
            Assert.That(doc.RootElement.GetProperty("end_token_id").GetInt32(), Is.EqualTo(128009));
            Assert.That(doc.RootElement.GetProperty("prompt_ids").GetArrayLength(), Is.EqualTo(3));
            Assert.That(doc.RootElement.GetProperty("target_ids").GetArrayLength(), Is.EqualTo(2));
            Assert.That(doc.RootElement.GetProperty("end_of_turn_ids")[0].GetInt32(), Is.EqualTo(99));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Test]
    public void WhatAPersonSaid_IsNeverWritten()
    {
        var folder = NewFolder();
        try
        {
            var log = new TrainingLog(folder, "session-b");
            Assert.That(log.Record([new("system", "You are Vertex."), new("user", "Aubri says: can you build me a castle?")], null, "Sure!", "x"), Is.False);
            // In the history of a later game turn, too.
            List<AiChatMessage> withHistory = [new("system", "You are Vertex."), new("user", "Aubri says: my birthday is in May"), new("assistant", "Nice!"), new("user", "[Game update: you got wood.]")];
            Assert.That(log.Record(withHistory, null, "I got wood.", "x"), Is.False);
            Assert.That(Directory.Exists(folder), Is.False, "nothing written at all");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Test]
    public void AFactAboutAPerson_IsNeverWritten()
    {
        List<AiChatMessage> withFact = [new("system", "- In the game you are at x 1.\n- About Aubri: friends since Oct 10, loves pink axolotls.\n"), new("user", "[Game update: you got wood.]")];
        Assert.That(TrainingLog.Allowed(withFact), Is.False);
    }

    [Test]
    public void ASession_StaysOnOneSideOfTheSplit_AndSomeAreHeldOut()
    {
        var splits = Enumerable.Range(0, 500).Select(i => TrainingLog.SplitOf($"session-{i}")).ToList();
        Assert.That(TrainingLog.SplitOf("session-7"), Is.EqualTo(TrainingLog.SplitOf("session-7")));
        Assert.That(splits.Count(s => s == "heldout"), Is.InRange(60, 140), "about one in five");
    }
}
