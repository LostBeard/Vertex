using System.Diagnostics;
using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// What each part of Vertex holds on the card: the parts are loaded one by one, in the order a start loads them, and the card's own counter (nvidia-smi) is read after each. A measurement,
/// not a gate: it prints the figures (and asserts only that the whole thing fits the card). Asked for by the library authors (2026-11-02): Vertex holds 11.1 GB, which is about 4 GB more
/// than the language model's weights and cache account for, and only 0.5 GB of the 12 GB card is free. The GPU must be otherwise idle (stop a running Vertex first).
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class GpuMemoryBreakdownTests
{
    private static int UsedMiB()
    {
        using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return int.Parse(text.Trim().Split('\n')[0].Trim());
    }

    private static int TotalMiB()
    {
        using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return int.Parse(text.Trim().Split('\n')[0].Trim());
    }

    [Test]
    public async Task WhatEachPartOfVertexHoldsOnTheCard()
    {
        var idle = UsedMiB();
        Assert.That(idle, Is.LessThan(2000), "the card must be otherwise idle (stop a running Vertex first): " + idle + " MiB used");
        var steps = new List<(string Name, int Used)> { ("idle card (desktop, other programs)", idle) };
        async Task Mark(string name) { await Task.Delay(1500); steps.Add((name, UsedMiB())); TestContext.Out.WriteLine($"MEM {name}: {steps[^1].Used} MiB (+{steps[^1].Used - steps[^2].Used})"); }

        await using var ai = TestAi.Create();
        await Mark("the context and the accelerator (no model yet)");
        var model = TestModel.Pick(await ai.ModelNamesAsync());
        await ai.EnsureChatReadyAsync(model);
        await Mark($"the language model {model} (weights + cache + first pass)");
        await ai.LoadIntentsAsync();
        await Mark("the command matcher (MiniLM)");
        // WarmUpAsync loads these four in this order; here one at a time so each is measured.
        await ai.Speech.EnsureReadyAsync();
        await Mark("speech to text (Parakeet)");
        await ai.Speakers.EnsureReadyAsync();
        await Mark("who is talking (speaker model)");
        await ai.Turns.EnsureReadyAsync();
        await Mark("has the speaker finished (Smart Turn)");
        await ai.Voice.SpeakWithVoiceAsync("Hello there.", "kokoro:af_heart");
        await Mark("one voice, one short sentence (Kokoro)");
        await ai.WarmLiveWordsAsync();
        await Mark("live words (streaming speech)");
        await ai.WarmFacesAsync();
        await Mark("faces");
        using var memory = await ai.OpenMemoryAsync(Path.Combine(Path.GetTempPath(), "vertex-tests-membreakdown-" + Guid.NewGuid().ToString("N")));
        await Mark("long-term memory (a second MiniLM)");

        // One real call with a prompt of about 4,000 tokens: does the cache or anything else grow?
        var filler = string.Join(" ", Enumerable.Range(0, 700).Select(i => $"Note {i}: the pen by the river has {i % 7 + 1} sheep."));
        await ai.ChatAsync(new SpawnDev.AI.AiChatRequest { Model = model, Messages = [new("system", "You answer in one short sentence."), new("user", filler + " How many notes are there? Answer with just a rough number.")] }, null);
        await Mark("after one call with a long prompt");

        var total = TotalMiB();
        TestContext.Out.WriteLine($"MEM total on the card {total} MiB; Vertex's parts hold {steps[^1].Used - idle} MiB; {total - steps[^1].Used} MiB free");
        Assert.That(steps[^1].Used, Is.LessThan(total), "it fits the card");
    }
}
