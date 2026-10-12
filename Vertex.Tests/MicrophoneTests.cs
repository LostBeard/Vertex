using System.Collections.Concurrent;
using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// The open-microphone path the Reachy Mini uses (Silero VAD endpointing, then Whisper), without the robot: a spoken
/// line arrives the way the robot's WebRTC microphone delivers it, 16 kHz in 20 ms chunks, with room silence around
/// it. The utterance must be closed by the VAD on its own and transcribed.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class MicrophoneTests
{
    private static short[] To16k(float[] pcm24k)
    {
        var f = Audio.Downsample48To16(Audio.Upsample24To48(pcm24k));
        var s = new short[f.Length];
        for (var i = 0; i < f.Length; i++) s[i] = (short)Math.Clamp(f[i] * 32767f, short.MinValue, short.MaxValue);
        return s;
    }

    private static void FeedLikeAMicrophone(MicrophoneEars ears, short[] speech)
    {
        var all = new short[8000].Concat(speech).Concat(new short[24000]).ToArray(); // 0.5 s before, 1.5 s after
        for (var i = 0; i < all.Length; i += 320)
            ears.Feed(all[i..Math.Min(all.Length, i + 320)]);
    }

    [Test]
    public async Task AnOpenMicrophone_HearsWhereALineEnds_AndTranscribesIt()
    {
        await using var ai = new VertexAi(m => TestContext.Progress.WriteLine("[ai] " + m));
        await ai.WarmUpAsync("kokoro:af_heart");
        await using var ears = await MicrophoneEars.CreateAsync(ai);
        ears.Log += m => TestContext.Out.WriteLine(m);
        var heard = new ConcurrentQueue<(string Text, double SttMs)>();
        ears.Heard += (text, seconds, ended, sttMs) => heard.Enqueue((text, sttMs));

        var (speech, _) = await ai.SpeakAsync("Vertex, can you build us a house by the river?", "kokoro:am_michael");
        FeedLikeAMicrophone(ears, To16k(speech));
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (heard.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.That(heard, Is.Not.Empty, "the utterance was never closed and transcribed");
        var (text, stt) = heard.First();
        TestContext.Out.WriteLine($"heard \"{text}\" (speech to text {stt:0} ms)");
        Assert.That(text.ToLowerInvariant(), Does.Contain("house").And.Contain("build"));

        // Muted (Vertex talking): the same line is not heard.
        await MutedHearsNothing(ears, speech, heard);
    }

    private static async Task MutedHearsNothing(MicrophoneEars ears, float[] speech, ConcurrentQueue<(string Text, double SttMs)> heard)
    {
        while (!ears.IsIdle) await Task.Delay(50);
        heard.Clear();
        ears.Muted = true;
        FeedLikeAMicrophone(ears, To16k(speech));
        await Task.Delay(4000);
        Assert.That(heard, Is.Empty, "heard while muted");
    }

    /// <summary>
    /// Every model Vertex uses, turn after turn (the language model, Kokoro, Whisper, the open microphone's VAD), with
    /// the GPU memory budget forced so tight that every allocation first reclaims every idle pooled buffer
    /// (SpawnDev.ILGPU 5.3.6 MemoryPressure). A buffer anything still uses that a pool hands back, or a kernel writing
    /// past its buffer, is an illegal address here instead of a quiet corruption.
    /// </summary>
    /// <remarks>
    /// 2026-10-12, live on the robot: CUDA_ERROR_ILLEGAL_ADDRESS after one turn, which poisons the GPU context for every
    /// model in the process. The cause was in SpawnDev.ILGPU.ML (fixed in 5.3.7): two sentences with the same token count
    /// share a Kokoro executor, and its node fold reused the first one's audio length for the second, so the vocoder's
    /// Pad ran past its buffer. "The number 1..." and "The number 2..." below are exactly such a pair (60 tokens each).
    /// </remarks>
    [Test]
    public async Task AllModelsUnderMemoryPressure_StayHealthy()
    {
        await using var ai = new VertexAi(m => TestContext.Progress.WriteLine("[ai] " + m));
        var models = await ai.ModelNamesAsync();
        var model = models.FirstOrDefault(m => m.StartsWith("llama3.1")) ?? throw new AssertionException("llama3.1 not in the Ollama cache");
        await ai.EnsureChatReadyAsync(model);
        await ai.WarmUpAsync("kokoro:af_heart");
        await using var ears = await MicrophoneEars.CreateAsync(ai);
        var (speech, _) = await ai.SpeakAsync("Vertex, can you build us a house by the river?", "kokoro:am_michael");
        var pcm16k = Audio.Downsample48To16(Audio.Upsample24To48(speech));
        ai.Accelerator.DeviceMemoryHeadroomBytes = ai.Accelerator.MemorySize; // every allocation reclaims first
        var before = global::ILGPU.Runtime.MemoryPressure.ProactiveReclaimCount;
        for (var round = 0; round < 4; round++)
        {
            var reply = await ai.ChatAsync(new SpawnDev.AI.AiChatRequest
            {
                Model = model,
                Messages = [new("user", $"Say one short sentence about the number {round}.")],
                Options = new SpawnDev.AI.AiGenerationOptions { MaxOutputTokens = 24, Strategy = "greedy" },
            }, null);
            var (voice, _) = await ai.SpeakAsync($"The number {round} is a placeholder used to show nothing.", "kokoro:af_heart");
            Assert.That(voice.Count(float.IsNaN), Is.Zero, $"round {round}: NaN samples in the speech");
            var heard = await ai.TranscribeAsync(pcm16k);
            FeedLikeAMicrophone(ears, To16k(voice));
            TestContext.Out.WriteLine($"round {round}: \"{reply.Text.Trim()}\" / {voice.Length} samples / heard \"{heard}\"");
            Assert.That(heard.ToLowerInvariant(), Does.Contain("house"), $"round {round}");
        }
        while (!ears.IsIdle) await Task.Delay(50);
        var reclaims = global::ILGPU.Runtime.MemoryPressure.ProactiveReclaimCount - before;
        TestContext.Out.WriteLine($"proactive reclaims: {reclaims}");
        Assert.That(reclaims, Is.GreaterThan(0), "the budget really was under pressure");
    }


    /// <summary>Closing the ears while they are transcribing waits for it; the GPU is healthy afterwards.</summary>
    [Test]
    public async Task ClosingTheEarsMidTranscription_LeavesTheGpuHealthy()
    {
        await using var ai = new VertexAi(m => TestContext.Progress.WriteLine("[ai] " + m));
        await ai.WarmUpAsync("kokoro:af_heart");
        var (speech, _) = await ai.SpeakAsync("Vertex, can you build us a house by the river?", "kokoro:am_michael");
        for (var round = 0; round < 5; round++)
        {
            var ears = await MicrophoneEars.CreateAsync(ai);
            ears.Log += m => TestContext.Out.WriteLine(m);
            FeedLikeAMicrophone(ears, To16k(speech));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (ears.IsIdle && DateTime.UtcNow < deadline) await Task.Delay(5);
            await Task.Delay(round * 150); // close at different points of the endpointing and transcription
            await ears.DisposeAsync();
        }
        var pcm16k = Audio.Downsample48To16(Audio.Upsample24To48(speech));
        Assert.That((await ai.TranscribeAsync(pcm16k)).ToLowerInvariant(), Does.Contain("house"));
    }
}
