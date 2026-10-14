using System.Text.Json;
using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// Smart Turn on REAL speech, through Vertex's own path (<see cref="VertexAi.PredictTurnEndAsync"/>): the same LibriSpeech
/// sentences as the voice tests, whole, and cut part-way at a quiet point as if the speaker had paused to think.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class SmartTurnTests
{
    private sealed record Fixture(string File, string Speaker, double Seconds, string Text);

    internal static float[] ReadWav16k(string path)
    {
        var b = File.ReadAllBytes(path);
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4), body = pos + 8;
            if (id == "data")
            {
                var n = Math.Min(size, b.Length - body) / 2;
                var f = new float[n];
                for (var i = 0; i < n; i++) f[i] = BitConverter.ToInt16(b, body + i * 2) / 32768f;
                return f;
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException(path);
    }

    internal static List<(string File, float[] Pcm, string Text)> Clips()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "voices");
        var manifest = JsonSerializer.Deserialize<List<Fixture>>(File.ReadAllText(Path.Combine(dir, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return manifest.Select(f => (f.File, ReadWav16k(Path.Combine(dir, f.File)), f.Text)).ToList();
    }

    /// <summary>The quietest 60 ms window between 40% and 70% of the clip: where a speaker would pause between words.</summary>
    internal static int QuietPoint(float[] pcm)
    {
        int win = 960, from = (int)(pcm.Length * 0.4), to = (int)(pcm.Length * 0.7);
        int best = from; double bestEnergy = double.MaxValue;
        for (int s = from; s + win < to; s += 160)
        {
            double e = 0;
            for (int i = 0; i < win; i++) e += pcm[s + i] * pcm[s + i];
            if (e < bestEnergy) { bestEnergy = e; best = s + win / 2; }
        }
        return best;
    }

    internal static float[] WithSilence(float[] pcm, int silenceSamples) => pcm.Concat(new float[silenceSamples]).ToArray();

    [Test]
    public async Task AWholeSentenceIsRecognisedAsFinished_AndOneCutAtAPauseIsNot()
    {
        await using var ai = TestAi.Create();
        var whole = new List<float>();
        var cut = new List<float>();
        foreach (var (file, pcm, _) in Clips())
        {
            // What the end-of-turn model is shown: the VAD's segment, so speech plus its 150 ms pad.
            var pWhole = await ai.PredictTurnEndAsync(WithSilence(pcm, 2400));
            var pCut = await ai.PredictTurnEndAsync(WithSilence(pcm[..QuietPoint(pcm)], 2400));
            whole.Add(pWhole); cut.Add(pCut);
            TestContext.Progress.WriteLine($"[turn] {file}: whole {pWhole:0.000}  cut at {QuietPoint(pcm) / 16000.0:0.0} s: {pCut:0.000}");
        }
        var wholeDone = whole.Count(p => p >= 0.5f); var cutHeld = cut.Count(p => p < 0.5f);
        TestContext.Progress.WriteLine($"[turn] whole sentences judged finished: {wholeDone}/{whole.Count}; cut sentences judged unfinished: {cutHeld}/{cut.Count}");
        Assert.That(wholeDone, Is.GreaterThanOrEqualTo(whole.Count * 3 / 4), "finished sentences must be judged finished");
        Assert.That(cutHeld, Is.GreaterThanOrEqualTo(cut.Count / 2), "sentences cut at a pause must mostly be judged unfinished");
    }

    [Test]
    public async Task APrediction_TakesFarLessThanTheHalfSecondItReplaces()
    {
        await using var ai = TestAi.Create();
        var (_, pcm, _) = Clips()[0];
        await ai.Turns.EnsureReadyAsync();
        var times = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await ai.PredictTurnEndAsync(WithSilence(pcm, 2400));
            times.Add(clock.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        TestContext.Progress.WriteLine($"[turn] prediction median {times[times.Count / 2]:0} ms, max {times[^1]:0} ms (idle GPU)");
        Assert.That(times[times.Count / 2], Is.LessThan(150), "the model costs more than a third of the silence it replaces");
    }

    /// <summary>Feeds audio the way a microphone delivers it: 20 ms chunks at the pace of the clock, so pauses are real time.</summary>
    private static async Task FeedInRealTime(MicrophoneEars ears, float[] speech)
    {
        var s = new short[speech.Length];
        for (var i = 0; i < s.Length; i++) s[i] = (short)Math.Clamp(speech[i] * 32767f, short.MinValue, short.MaxValue);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < s.Length; i += 320)
        {
            ears.Feed(s[i..Math.Min(s.Length, i + 320)]);
            var due = (i + 320) / 16.0;                       // ms of audio fed so far
            var wait = due - clock.Elapsed.TotalMilliseconds;
            if (wait > 1) await Task.Delay((int)wait);
        }
    }

    private static async Task<List<HeardUtterance>> HearAsync(VertexAi ai, bool smartTurn, float[] audio)
    {
        await using var ears = await MicrophoneEars.CreateAsync(ai, smartTurn: smartTurn);
        ears.IdentifySpeakers = false;
        var heard = new System.Collections.Concurrent.ConcurrentQueue<HeardUtterance>();
        ears.Heard += heard.Enqueue;
        await FeedInRealTime(ears, audio);
        var until = Environment.TickCount64 + 20_000;
        while (!ears.IsIdle && Environment.TickCount64 < until) await Task.Delay(50);
        Assert.That(ears.IsIdle, Is.True, "the ears never settled");
        return heard.ToList();
    }

    /// <summary>
    /// The point of the feature, as an A/B on one sentence a speaker pauses in for 0.7 s: the plain half-second rule hears two utterances (and
    /// would answer the first half), Smart Turn waits and hears the one sentence. (The pause is put at the clip's quietest point, which is not
    /// always a word boundary - here it falls inside "far" - but the mechanism under test does not care where the speaker stopped.)
    /// </summary>
    [Test]
    public async Task ASentenceWithAPauseInIt_IsOneUtteranceWithSmartTurn_AndTwoWithoutIt()
    {
        await using var ai = TestAi.Create();
        await ai.WarmUpAsync("kokoro:af_heart");
        var (file, pcm, text) = Clips().Single(c => c.File.StartsWith("a2-"));
        var at = QuietPoint(pcm);
        var audio = new float[8000].Concat(pcm[..at]).Concat(new float[11200]).Concat(pcm[at..]).Concat(new float[24000]).ToArray();   // 0.5 s, A, 0.7 s, B, 1.5 s

        var plain = await HearAsync(ai, smartTurn: false, audio);
        var smart = await HearAsync(ai, smartTurn: true, audio);
        TestContext.Progress.WriteLine($"[turn] {file} \"{text}\"");
        TestContext.Progress.WriteLine($"[turn] plain half-second rule: {plain.Count} utterance(s): {string.Join(" | ", plain.Select(h => h.Text))}");
        TestContext.Progress.WriteLine($"[turn] Smart Turn:             {smart.Count} utterance(s): {string.Join(" | ", smart.Select(h => h.Text))}");

        Assert.That(plain, Has.Count.EqualTo(2), "the A/B needs the plain rule to cut the sentence in two, or it shows nothing");
        Assert.That(smart, Has.Count.EqualTo(1), "Smart Turn must keep the sentence whole");
        Assert.That(smart[0].Seconds, Is.GreaterThan(plain.Sum(h => h.Seconds) + 0.4), "the one utterance holds both halves AND the pause between them");
    }

    /// <summary>
    /// The other half of the feature: a sentence that IS finished is heard sooner. Same audio, fed at the pace of the clock; the time that matters is
    /// from the last spoken sample to the transcript, which is silence-rule + speech to text.
    /// </summary>
    [Test]
    public async Task AFinishedSentence_IsHeardSoonerWithSmartTurn()
    {
        await using var ai = TestAi.Create();
        await ai.WarmUpAsync("kokoro:af_heart");
        var (file, pcm, text) = Clips().Single(c => c.File.StartsWith("a1-"));
        var audio = new float[8000].Concat(pcm).Concat(new float[24000]).ToArray();
        var speechEndsAtMs = (8000 + pcm.Length) / 16.0;

        async Task<(double Ms, string Text)> Latency(bool smartTurn)
        {
            await using var ears = await MicrophoneEars.CreateAsync(ai, smartTurn: smartTurn);
            ears.IdentifySpeakers = false;
            var heardAt = new TaskCompletionSource<(long, string)>();
            ears.Heard += h => heardAt.TrySetResult((Environment.TickCount64, h.Text));
            var started = Environment.TickCount64;
            await FeedInRealTime(ears, audio);
            var done = await Task.WhenAny(heardAt.Task, Task.Delay(15000));
            Assert.That(done, Is.SameAs(heardAt.Task), "nothing was heard");
            var (tick, heard) = heardAt.Task.Result;
            return (tick - started - speechEndsAtMs, heard);
        }

        // Alternate and repeat so a single scheduling hiccup cannot decide it.
        var plain = new List<double>(); var smart = new List<double>(); string lastText = "";
        for (int round = 0; round < 3; round++)
        {
            plain.Add((await Latency(false)).Ms);
            var s = await Latency(true); smart.Add(s.Ms); lastText = s.Text;
        }
        plain.Sort(); smart.Sort();
        TestContext.Progress.WriteLine($"[turn] {file} \"{text}\" -> \"{lastText}\"");
        TestContext.Progress.WriteLine($"[turn] last spoken sample to transcript: plain rule {string.Join(", ", plain.Select(v => v.ToString("0")))} ms (median {plain[1]:0}); Smart Turn {string.Join(", ", smart.Select(v => v.ToString("0")))} ms (median {smart[1]:0})");
        Assert.That(smart[1], Is.LessThan(plain[1] - 100), "Smart Turn must answer a finished sentence at least 100 ms sooner than the half-second rule");
    }
}
