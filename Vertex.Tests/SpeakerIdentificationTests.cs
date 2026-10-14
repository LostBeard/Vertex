using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpawnDev.AI.Server;
using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// The whole "who said what" path on REAL speech: LibriSpeech clips of three speakers arrive the way the robot's microphone
/// delivers them (16 kHz, 20 ms chunks, room silence around each), are endpointed by Silero VAD, transcribed by Parakeet
/// (what Vertex now listens with), matched to a voice by ReDimNet2, and resolved to a name by <see cref="SpeakerTracker"/>.
/// </summary>
/// <remarks>
/// Nothing is staged except the order: every speaker is first a stranger (a provisional guest), is introduced the way a person
/// would ("I'm Alice"), and is then recognised on clips that were never used to learn them. The voices live in a temp file.
/// Fixtures: Fixtures/voices (LibriSpeech test-clean, CC BY 4.0; speakers 2300, 3570, 8224).
/// </remarks>
[Category("GPU")]
[NonParallelizable]
public class SpeakerIdentificationTests
{
    private sealed record Fixture(string File, string Speaker, double Seconds, string Text);

    private static float[] ReadWav16k(string path)
    {
        var b = File.ReadAllBytes(path);
        int pos = 12, channels = 1, rate = 16000, bits = 16;
        while (pos + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4), body = pos + 8;
            if (id == "fmt ") { channels = BitConverter.ToInt16(b, body + 2); rate = BitConverter.ToInt32(b, body + 4); bits = BitConverter.ToInt16(b, body + 14); }
            else if (id == "data")
            {
                Assert.That((channels, rate, bits), Is.EqualTo((1, 16000, 16)), "fixtures are 16 kHz mono 16-bit");
                var n = Math.Min(size, b.Length - body) / 2;
                var f = new float[n];
                for (var i = 0; i < n; i++) f[i] = BitConverter.ToInt16(b, body + i * 2) / 32768f;
                return f;
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException(path);
    }

    private static void FeedLikeAMicrophone(MicrophoneEars ears, float[] speech)
    {
        var s = new short[speech.Length];
        for (var i = 0; i < s.Length; i++) s[i] = (short)Math.Clamp(speech[i] * 32767f, short.MinValue, short.MaxValue);
        var all = new short[8000].Concat(s).Concat(new short[24000]).ToArray();     // 0.5 s before, 1.5 s after
        for (var i = 0; i < all.Length; i += 320) ears.Feed(all[i..Math.Min(all.Length, i + 320)]);
    }

    private static double WordOverlap(string reference, string heard)
    {
        static string[] W(string t) => Regex.Replace(t.ToLowerInvariant(), "[^a-z0-9' ]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var r = W(reference); var h = W(heard).ToHashSet();
        return r.Count(h.Contains) / (double)r.Length;
    }

    /// <summary>
    /// "Keep what the robot hears" writes each utterance as a WAV with the words and who it sounded like, so the household's own
    /// voices become a test set (run tools/stt-bench on the folder after correcting the .txt files). Off unless a folder is set.
    /// </summary>
    [Test]
    public async Task WhatIsHeard_IsKeptOnlyWhenAFolderIsSet_AsAudioWordsAndWho()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "voices");
        var manifest = JsonSerializer.Deserialize<List<Fixture>>(File.ReadAllText(Path.Combine(dir, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var clip = manifest.Single(f => f.File.StartsWith("a2-"));
        var speech = ReadWav16k(Path.Combine(dir, clip.File));
        var keep = Path.Combine(Path.GetTempPath(), "vertex-tests", Guid.NewGuid().ToString("N"), "heard");

        await using var ai = TestAi.Create();
        await ai.WarmUpAsync("kokoro:af_heart");
        await using var ears = await MicrophoneEars.CreateAsync(ai);
        var heard = new ConcurrentQueue<HeardUtterance>();
        ears.Heard += heard.Enqueue;

        async Task Say()
        {
            while (!ears.IsIdle) await Task.Delay(50);
            heard.Clear();
            FeedLikeAMicrophone(ears, speech);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (heard.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.That(heard, Is.Not.Empty, "never heard");
            while (!ears.IsIdle) await Task.Delay(50);
            await Task.Delay(500);      // the files are written off the listening path
        }

        await Say();                                                   // no folder set: nothing kept anywhere
        Assert.That(Directory.Exists(keep), Is.False);

        ears.SaveFolder = keep;
        await Say();
        var wavs = Directory.GetFiles(keep, "*.wav");
        Assert.That(wavs, Has.Length.EqualTo(1));
        var stem = Path.ChangeExtension(wavs[0], null);
        var kept = ReadWav16k(wavs[0]);
        Assert.That(kept.Length / 16000.0, Is.EqualTo(clip.Seconds).Within(1.5), "the kept audio is the utterance (plus the VAD's padding), not a fragment");
        Assert.That(File.ReadAllText(stem + ".txt").Trim(), Is.EqualTo(heard.Last().Text), "the words are what Vertex heard");
        using var meta = JsonDocument.Parse(File.ReadAllText(stem + ".json"));
        Assert.That(meta.RootElement.GetProperty("seconds").GetDouble(), Is.GreaterThan(2));
        Assert.That(meta.RootElement.GetProperty("speaker").GetString(), Does.StartWith("Guest"), "who it sounded like (a stranger here)");
    }

    [Test]
    public async Task ThreeVoices_AreToldApart_IntroducedAndRecognisedOnNewSpeech()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "voices");
        var manifest = JsonSerializer.Deserialize<List<Fixture>>(File.ReadAllText(Path.Combine(dir, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Fixture Clip(string prefix) => manifest.Single(f => f.File.StartsWith(prefix + "-"));

        await using var ai = TestAi.Create();
        await ai.WarmUpAsync("kokoro:af_heart");
        await using var ears = await MicrophoneEars.CreateAsync(ai);
        ears.Log += m => TestContext.Out.WriteLine(m);
        var tracker = new SpeakerTracker(ai.Speakers.Registry, ai.SaveVoices, m => TestContext.Out.WriteLine("[voice] " + m));
        var heard = new ConcurrentQueue<(HeardUtterance U, SpeakerTracker.Who Who)>();
        ears.Heard += h => heard.Enqueue((h, tracker.Resolve(h.Speaker, h.EndedTick)));

        async Task<(HeardUtterance U, SpeakerTracker.Who Who, Fixture Clip)> Hear(string prefix)
        {
            var clip = Clip(prefix);
            while (!ears.IsIdle) await Task.Delay(50);
            heard.Clear();
            FeedLikeAMicrophone(ears, ReadWav16k(Path.Combine(dir, clip.File)));
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (heard.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.That(heard, Is.Not.Empty, $"{clip.File} was never closed and transcribed");
            while (!ears.IsIdle) await Task.Delay(50);
            var (u, who) = heard.Last();   // a clip is one utterance; if the VAD split it, the last piece decides
            var overlap = WordOverlap(clip.Text, string.Join(" ", heard.Select(x => x.U.Text)));
            TestContext.Out.WriteLine($"{clip.File}: \"{u.Text}\" -> {who.Name} (identified {who.Identified}, score {u.Speaker?.Match.Score:0.00}, words {overlap:P0}, voice {u.Speaker?.EmbedMs:0} ms)");
            Assert.That(overlap, Is.GreaterThan(0.6), $"{clip.File}: Parakeet heard \"{u.Text}\" for \"{clip.Text}\"");
            return (u, who, clip);
        }

        // 1. Nobody is known: each new voice is a provisional guest, and the same voice is the same guest.
        var a1 = await Hear("a1");
        Assert.That(a1.Who.Name, Does.StartWith("Guest"), "a stranger is a guest, not 'someone in the room'");
        Assert.That(a1.U.Speaker!.Embedding, Has.Length.EqualTo(192));

        // 2. "I'm Alice": her next few utterances are learned; she is Alice from then on.
        Assert.That(tracker.BeginIntroduction("Alice", forNextVoice: false, Environment.TickCount64), Does.Contain("Alice"));
        foreach (var p in new[] { "a2", "a3" })
            Assert.That((await Hear(p)).Who.Name, Is.EqualTo("Alice"), p);

        // 3. A different voice is not Alice; introduce him the same way.
        var b1 = await Hear("b1");
        // (The name "Guest 1" is free again once Alice - who was Guest 1 - has her own, so the check is that he is not Alice.)
        Assert.That(b1.Who.Name, Does.StartWith("Guest").And.Not.EqualTo("Alice"), "the second stranger is not Alice");
        Assert.That(tracker.BeginIntroduction("Bob", forNextVoice: false, Environment.TickCount64), Does.Contain("Bob"));
        foreach (var p in new[] { "b2", "b3" })
            Assert.That((await Hear(p)).Who.Name, Is.EqualTo("Bob"), p);

        // 4. Speech never used to learn them: both are recognised, whichever speaks.
        Assert.That((await Hear("a4")).Who.Name, Is.EqualTo("Alice"));
        Assert.That((await Hear("b4")).Who.Name, Is.EqualTo("Bob"));
        Assert.That((await Hear("a5")).Who.Name, Is.EqualTo("Alice"));
        Assert.That((await Hear("b5")).Who.Name, Is.EqualTo("Bob"));

        // 5. A third voice is neither; the same voice again is the same guest.
        var c1 = await Hear("c1");
        Assert.That(c1.Who.Name, Does.StartWith("Guest").And.Not.EqualTo("Alice").And.Not.EqualTo("Bob"));
        Assert.That((await Hear("c2")).Who.Name, Is.EqualTo(c1.Who.Name), "the same stranger is the same guest");

        // 6. What was learned is on disk, and a fresh process would know Alice and Bob.
        ai.SaveVoices();
        var restored = SpawnDev.AI.SpeakerRegistry.FromJson(File.ReadAllText(ai.VoicesFile));
        Assert.That(restored.Profiles.Select(p => p.Name), Is.SupersetOf(new[] { "Alice", "Bob" }));
    }
}
