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
        await using var ai = TestAi.Create();
        await ai.WarmUpAsync("kokoro:af_heart");
        await using var ears = await MicrophoneEars.CreateAsync(ai);
        ears.Log += m => TestContext.Out.WriteLine(m);
        var heard = new ConcurrentQueue<(string Text, double SttMs)>();
        ears.Heard += h => heard.Enqueue((h.Text, h.SttMs));

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
        await using var ai = TestAi.Create();
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
        await using var ai = TestAi.Create();
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

    /// <summary>The speech without the silence the synthesiser puts round it: a spoken word as the voice detector would close it (75 ms of padding).</summary>
    private static short[] TrimToSpeech(short[] pcm)
    {
        var loud = 600;       // about -35 dBFS
        var first = Array.FindIndex(pcm, x => Math.Abs((int)x) > loud);
        var last = Array.FindLastIndex(pcm, x => Math.Abs((int)x) > loud);
        if (first < 0) return pcm;
        first = Math.Max(0, first - 1200); last = Math.Min(pcm.Length - 1, last + 1200);
        return pcm[first..(last + 1)];
    }

    /// <summary>16 kHz audio driven hard into clipping, the way a close, loud voice peaks: gain until the peak is at full scale, then a little more.</summary>
    private static short[] Clipped(short[] pcm)
    {
        var peak = Math.Max(1, pcm.Max(x => Math.Abs((int)x)));
        var gain = 32767.0 / peak * 1.3;
        return pcm.Select(x => (short)Math.Clamp(x * gain, short.MinValue, short.MaxValue)).ToArray();
    }

    /// <summary>
    /// Live (2026-10-14): TJ's loud, close "Vertex." (0.81 s) peaked at full scale and the noise gate threw it out as "a click", so the conversation never
    /// opened. A sound the gate refuses that could be the name is now transcribed and kept when the words contain it - and only then.
    /// </summary>
    [Test]
    public async Task ALoudCloseName_IsHeard_ButALoudYeahAndARealClick_AreNot()
    {
        await using var ai = TestAi.Create();
        await ai.WarmUpAsync("kokoro:af_heart");
        await using var ears = await MicrophoneEars.CreateAsync(ai);
        ears.Log += m => TestContext.Out.WriteLine(m);
        var heard = new ConcurrentQueue<string>();
        ears.Heard += h => heard.Enqueue(h.Text);
        async Task Settle() { var until = DateTime.UtcNow.AddSeconds(20); while (!ears.IsIdle && DateTime.UtcNow < until) await Task.Delay(50); await Task.Delay(1500); }

        // 1. A loud, clipped "Vertex." (the gate calls it a click: peak at full scale, under a second).
        var (name, _) = await ai.SpeakAsync("Vertex.", "kokoro:am_michael");
        var loud = Clipped(TrimToSpeech(To16k(name)));
        var measure = NoiseGate.Measure(loud.Select(x => x / 32768f).ToArray());
        TestContext.Out.WriteLine($"[gate] the loud name: {measure.Seconds:0.00} s, {measure.RmsDb:0.0} dB rms, {measure.PeakDb:0.0} dB peak");
        Assert.That(NoiseGate.Reject(measure, false), Is.Not.Null, "the premise: the gate does refuse this sound");
        Assert.That(NoiseGate.MayBeTheName(measure), Is.True);
        FeedLikeAMicrophone(ears, loud);
        await Settle();
        Assert.That(heard.Any(t => Vertex.Brain.WakeWord.Names(t, "Vertex")), Is.True, "the loud name was thrown away: heard [" + string.Join(" | ", heard) + "]");

        // 2. The same loudness on a word that is not the name: transcribed to check, then dropped.
        heard.Clear();
        var (yeah, _) = await ai.SpeakAsync("Yeah.", "kokoro:am_michael");
        FeedLikeAMicrophone(ears, Clipped(TrimToSpeech(To16k(yeah))));
        await Settle();
        Assert.That(heard, Is.Empty, "a loud 'Yeah.' reached the robot: " + string.Join(" | ", heard));

        // 3. A real click (a 40 ms burst of full-scale noise) never reaches the recogniser's output.
        heard.Clear();
        var rnd = new Random(7);
        var click = Enumerable.Range(0, 640).Select(_ => (short)(rnd.Next(2) == 0 ? 32767 : -32768)).Concat(new short[3000]).ToArray();
        FeedLikeAMicrophone(ears, click);
        await Settle();
        Assert.That(heard, Is.Empty, "a click was heard as words: " + string.Join(" | ", heard));
    }

    /// <summary>
    /// Live (2026-10-14): two sentences said in a row ("We're gonna play Minecraft today. What's the best thing to build first?"). The voice detector shares the GPU with
    /// the language model, which is prefilling the answer to the first sentence and cannot yield, so the second sentence is only CLOSED after the robot has started talking and
    /// its ears have shut. It was thrown away for that. What was recorded before the ears closed is heard, however late it is processed; what is recorded after, is not.
    /// </summary>
    [Test]
    public async Task ASentenceSaidBeforeTheEarsClosed_IsStillHeard_WhenTheDetectorOnlyGetsToItLater()
    {
        await using var ai = TestAi.Create();
        await ai.WarmUpAsync("kokoro:af_heart");
        await using var ears = await MicrophoneEars.CreateAsync(ai);
        ears.Log += m => TestContext.Out.WriteLine(m);
        var heard = new ConcurrentQueue<string>();
        ears.Heard += h => heard.Enqueue(h.Text);

        var (before, _) = await ai.SpeakAsync("What is the best thing to build first?", "kokoro:am_michael");
        var (after, _) = await ai.SpeakAsync("And what about the second thing?", "kokoro:am_michael");

        // Hold the GPU lane, as a language model prefilling a reply does: nothing the microphone feeds can be processed until it is released.
        var held = await ai.Lane.EnterAsync();
        FeedLikeAMicrophone(ears, To16k(before));      // recorded while the ears are open
        ears.Muted = true;                             // the robot starts talking: the ears close, with that sentence still unprocessed
        FeedLikeAMicrophone(ears, To16k(after));       // recorded while the ears are closed: never reaches the detector
        held.Dispose();

        var until = DateTime.UtcNow.AddSeconds(30);
        while (heard.IsEmpty && DateTime.UtcNow < until) await Task.Delay(100);
        await Task.Delay(3000);
        TestContext.Out.WriteLine("heard: " + string.Join(" | ", heard));
        Assert.That(heard.Any(t => t.ToLowerInvariant().Contains("build")), Is.True, "a sentence said before the ears closed was thrown away: [" + string.Join(" | ", heard) + "]");
        Assert.That(heard.Any(t => t.ToLowerInvariant().Contains("second")), Is.False, "a sentence recorded while the ears were closed was heard: [" + string.Join(" | ", heard) + "]");
    }
}
