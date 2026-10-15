using ILGPU.Runtime;
using SpawnDev.AI;
using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// Vertex's whole AI in one process, used the way a conversation uses it, with no game: the language model answering,
/// Kokoro speaking, Whisper listening, the sentence encoder matching. 2026-10-12 live: after a few minutes of that
/// the 12 GB card held 14.7 GB of buffers (7.3 GB idle in buffer pools), 1.4 GB had spilled into system RAM and a
/// one-sentence reply took 5.5 minutes. The idle pools are now reclaimed BEFORE the driver spills (SpawnDev.ILGPU
/// MemoryPressure + SpawnDev.ILGPU.ML BufferPool).
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class AiSessionTests
{
    private static readonly string[] Questions =
    [
        "Hi Vertex! How are you today?",
        "Tell me three things you like about building castles, in detail.",
        "What is the best way to find diamonds?",
        "Can you describe a sunset over the ocean in a few sentences?",
        "What should we name our pet wolf?",
        "Tell me a short story about a creeper who wanted to be friends with everyone.",
    ];

    [Test]
    public async Task ALongConversation_StaysInGpuMemory_AndLateTurnsAreAsFastAsEarlyOnes()
    {
        await using var ai = TestAi.Create();
        if (!ai.Accelerator.TryGetFreeMemory(out var freeAtStart))
            Assert.Ignore($"{ai.Accelerator.AcceleratorType} cannot report free device memory");
        var models = await ai.ModelNamesAsync();
        var model = models.FirstOrDefault(m => m.StartsWith("llama3.1")) ?? throw new AssertionException("llama3.1 not in the Ollama cache");
        await ai.EnsureChatReadyAsync(model);
        await ai.LoadIntentsAsync();
        await ai.WarmUpAsync("kokoro:af_heart");
        await ai.WarmLiveWordsAsync();   // the robot's ears keep the live-words model resident too: the budget has to hold with it
        var headroom = MemoryPressure.GetHeadroomBytes(ai.Accelerator);
        TestContext.Out.WriteLine($"free at start {freeAtStart / 1048576} MiB, headroom {headroom / 1048576} MiB");

        var history = new List<AiChatMessage> { new("system", "You are Vertex, a friendly Minecraft companion. Answer in a few spoken sentences.") };
        var firstWordMs = new List<double>();
        long lowestFree = long.MaxValue;
        for (var round = 0; round < 3; round++)
            foreach (var q in Questions)
            {
                history.Add(new AiChatMessage("user", q));
                var passes = new List<string>();
                ai.Chat.PerfLog = m => { if (m.Contains("prompt=")) passes.Add(m.Trim()); };
                var reclaimsBefore = MemoryPressure.ProactiveReclaimCount;
                var reclaimedBefore = MemoryPressure.ProactiveReclaimedBytes;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double first = -1;
                var result = await ai.ChatAsync(new AiChatRequest
                {
                    Model = model,
                    Messages = history.ToList(),
                    Options = new AiGenerationOptions { MaxOutputTokens = 160, Strategy = "top_p", Temperature = 0.7f, TopP = 0.9f },
                }, _ => { if (first < 0) first = sw.Elapsed.TotalMilliseconds; return Task.CompletedTask; });
                firstWordMs.Add(first);
                history.Add(new AiChatMessage("assistant", result.TextWithoutToolCalls));
                // Trimmed the way VertexBrain trims: the older half at once, keeping the system prompt.
                if (history.Count - 1 > Vertex.Brain.VertexBrain.MaxHistory)
                    history.RemoveRange(1, history.Count - 1 - Vertex.Brain.VertexBrain.MaxHistory / 2);

                // Say the first sentence, hear it back, match a command: every model runs every turn.
                var line = Mouth.Sentences(result.TextWithoutToolCalls).FirstOrDefault() ?? "Hello.";
                var (samples, _) = await ai.SpeakAsync(line, "kokoro:af_heart");
                var pcm16k = Audio.Downsample48To16(Audio.Upsample24To48(samples));
                var heard = await ai.TranscribeAsync(pcm16k);
                await using (var live = await ai.Streaming.BeginAsync())
                    for (var at = 0; at < pcm16k.Length; at += 1600) await ai.LiveAcceptAsync(live, pcm16k[at..Math.Min(pcm16k.Length, at + 1600)]);
                await ai.Intents!.MatchAsync("Vertex, follow me", "Vertex");

                ai.Accelerator.TryGetFreeMemory(out var free);
                lowestFree = Math.Min(lowestFree, free);
                TestContext.Out.WriteLine($"turn {firstWordMs.Count}: first word {first:0} ms, free {free / 1048576} MiB, " +
                    $"reclaims {MemoryPressure.ProactiveReclaimCount - reclaimsBefore} ({(MemoryPressure.ProactiveReclaimedBytes - reclaimedBefore) / 1048576} MiB), " +
                    $"{string.Join(" ; ", passes)} | heard \"{heard}\"");
            }

        TestContext.Out.WriteLine($"proactive reclaims {MemoryPressure.ProactiveReclaimCount}, {MemoryPressure.ProactiveReclaimedBytes / 1048576} MiB");
        // Never below half the headroom: a spill shows as free memory near zero.
        Assert.That(lowestFree, Is.GreaterThan(headroom / 2), $"free device memory fell to {lowestFree / 1048576} MiB: the session spilled");
        // Late turns are not slower than early ones (beyond the noise of different prompt lengths).
        var early = firstWordMs.Skip(1).Take(5).Average();
        var late = firstWordMs.TakeLast(5).Average();
        Assert.That(late, Is.LessThan(early * 2 + 500), $"first word took {early:0} ms early and {late:0} ms late");
    }
}
