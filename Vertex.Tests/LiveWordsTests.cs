using System.Collections.Concurrent;
using System.Diagnostics;
using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// The words of a sentence appear WHILE it is being said (voice typing, like a phone), then the finished sentence is transcribed by the accurate recogniser as before.
/// A spoken line arrives the way the robot's microphone delivers it - 16 kHz in 20 ms pieces, paced in real time - and the first words must be out before the audio is.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class LiveWordsTests
{
    private static short[] To16k(float[] pcm24k)
    {
        var f = Audio.Downsample48To16(Audio.Upsample24To48(pcm24k));
        var s = new short[f.Length];
        for (var i = 0; i < f.Length; i++) s[i] = (short)Math.Clamp(f[i] * 32767f, short.MinValue, short.MaxValue);
        return s;
    }

    [Test]
    public async Task WordsAppearWhileTheSentenceIsStillBeingSaid_AndTheFinishedOneIsStillTranscribed()
    {
        await using var ai = TestAi.Create();
        await ai.WarmUpAsync("kokoro:af_heart");
        await using var ears = await MicrophoneEars.CreateAsync(ai, liveWords: true);
        ears.Log += m => TestContext.Out.WriteLine(m);
        var partials = new ConcurrentQueue<(string Text, bool Final, long AtMs)>();
        var heard = new ConcurrentQueue<(string Text, long AtMs)>();
        var clock = Stopwatch.StartNew();
        ears.Hearing += (text, final) => partials.Enqueue((text, final, clock.ElapsedMilliseconds));
        ears.Heard += h => heard.Enqueue((h.Text, clock.ElapsedMilliseconds));

        var (speech, _) = await ai.SpeakAsync("Vertex, can you build us a house by the river? I would like it to have a red roof.", "kokoro:am_michael");
        var line = new short[8000].Concat(To16k(speech)).Concat(new short[24000]).ToArray();   // 0.5 s before, 1.5 s after
        var speechEndsMs = (long)((8000 + To16k(speech).Length) / 16.0);
        clock.Restart();
        for (var i = 0; i < line.Length; i += 320)
        {
            ears.Feed(line[i..Math.Min(line.Length, i + 320)]);
            // Real time: a piece of 20 ms of audio every 20 ms of the clock.
            var due = (i + 320) / 16;
            var wait = due - clock.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay((int)wait);
        }
        var fedMs = clock.ElapsedMilliseconds;

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while ((heard.IsEmpty || !partials.Any(p => p.Final)) && DateTime.UtcNow < deadline) await Task.Delay(100);
        foreach (var p in partials) TestContext.Out.WriteLine($"  {p.AtMs,6} ms {(p.Final ? "FINAL" : "     ")} \"{p.Text}\"");
        foreach (var h in heard) TestContext.Out.WriteLine($"  {h.AtMs,6} ms HEARD \"{h.Text}\"");

        Assert.That(partials, Is.Not.Empty, "no live words at all");
        var first = partials.First();
        TestContext.Out.WriteLine($"first words after {first.AtMs} ms of a {fedMs} ms recording (speech ends at {speechEndsMs} ms)");
        Assert.That(first.AtMs, Is.LessThan(speechEndsMs - 1000), "the first words came out only after the speaker had nearly finished: that is not live");
        Assert.That(first.Final, Is.False, "the first report was already the final one");
        Assert.That(partials.Any(p => p.Final), "the utterance was never reported over");
        var all = string.Join(" ", partials.Select(p => p.Text)).ToLowerInvariant();
        Assert.That(all, Does.Contain("house"));
        Assert.That(partials.Where(p => !p.Final).Select(p => p.Text.Length).Max(), Is.GreaterThan(20), "the live text never grew into a sentence");

        Assert.That(heard, Is.Not.Empty, "the finished sentence was not transcribed");
        Assert.That(heard.First().Text.ToLowerInvariant(), Does.Contain("house").And.Contain("river"));
    }

    [Test]
    public async Task ASoundThatNeverBecomesASentence_DoesNotLeaveAStreamOpen()
    {
        await using var ai = TestAi.Create();
        await ai.WarmUpAsync("kokoro:af_heart");
        await using var live = new LiveWords(ai);
        live.Log += m => TestContext.Out.WriteLine(m);
        var reports = new ConcurrentQueue<(string, bool)>();
        live.Words += (t, f) => reports.Enqueue((t, f));
        await ai.WarmLiveWordsAsync();

        // The detector opens on something (active = true for a moment), then 4 s of silence: the idle rule must close the stream by itself, or the next utterance would wait for it forever.
        var piece = new float[320];
        for (var i = 0; i < 5; i++) live.Feed(piece, active: true);
        for (var i = 0; i < 200; i++) live.Feed(piece, active: false);   // 4 s
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (live.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.That(live.IsBusy, Is.False, "the worker is still busy: the idle stream was not closed");

        // The next utterance can begin (BeginAsync would block forever if the previous stream were still open).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var next = await ai.Streaming.BeginAsync(cts.Token);
        Assert.That(next.Text, Is.Empty);
    }
}
