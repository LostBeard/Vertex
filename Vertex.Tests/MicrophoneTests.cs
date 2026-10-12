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
        while (!ears.IsIdle) await Task.Delay(50);
        heard.Clear();
        ears.Muted = true;
        FeedLikeAMicrophone(ears, To16k(speech));
        await Task.Delay(4000);
        Assert.That(heard, Is.Empty, "heard while muted");
    }
}
