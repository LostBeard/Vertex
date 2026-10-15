using System.Collections.Concurrent;
using System.Diagnostics;
using SpawnDev.Reachy;
using SpawnDev.RTC;
using SpawnDev.RTC.Audio;

namespace Vertex.Tests;

/// <summary>
/// Live speech to the robot: audio goes out in real time, one 20 ms frame every 20 ms, with silence between clips so the stream never stops. A recording stand-in for the peer
/// connection's audio sender, so no robot is needed.
/// </summary>
public class RobotSpeechStreamerTests
{
    private sealed class RecordingSender : IRTCAudioSender
    {
        public readonly ConcurrentQueue<(long Ms, bool Speech, short First)> Frames = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public IRTCMediaStreamTrack Track => throw new NotSupportedException();
        public void PushPcm(AudioPcmFrame frame)
        {
            Assert.That(frame.SampleRate, Is.EqualTo(48000));
            Assert.That(frame.Channels, Is.EqualTo(2), "WebRTC's Opus is stereo");
            Assert.That(frame.Pcm.Length, Is.EqualTo(960 * 2), "a frame is 20 ms of two interleaved channels");
            Frames.Enqueue((_clock.ElapsedMilliseconds, frame.Pcm.Any(s => s != 0), frame.Pcm[0]));
        }
        public void Dispose() { }
    }

    private static short[] Tone(int ms, short marker)
    {
        var pcm = new short[48 * ms];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)(marker + (i % 50));      // never zero
        return pcm;
    }

    /// <summary>The real Opus encoder behind the sender, as the connection has it: every frame it makes says how much time it holds.</summary>
    private sealed class EncodingSender : IRTCAudioSender
    {
        private readonly SpawnDev.RTC.Desktop.MultiMediaAudioSource _source = new();
        public long RtpUnits; public int Frames;
        public EncodingSender()
        {
            _source.OnAudioSourceEncodedSample += (duration, sample) => { Interlocked.Add(ref RtpUnits, duration); Interlocked.Increment(ref Frames); };
        }
        public IRTCMediaStreamTrack Track => throw new NotSupportedException();
        public void PushPcm(AudioPcmFrame frame) => _source.PushPcm(frame.Pcm);
        public void Dispose() => _source.Dispose();
    }

    /// <summary>
    /// The chipmunk (live 2026-10-15): the negotiated Opus format is 48 kHz STEREO and the encoder takes exactly 20 ms x 2 channels per frame, so mono frames made it hold only half as much
    /// time per frame and the robot played everything at twice the speed, an octave up. With the REAL encoder: the time the encoded frames hold must equal the real time that passed.
    /// </summary>
    [Test]
    public async Task TheEncodedStreamHoldsAsMuchTimeAsPassed_SoSpeechIsNotSpedUp()
    {
        using var sender = new EncodingSender();
        using var streamer = new RobotSpeechStreamer(sender) { PlayoutLatency = TimeSpan.Zero };
        var clock = Stopwatch.StartNew();
        await streamer.SpeakAsync(Tone(1000, 1000));
        await Task.Delay(100);
        var elapsedSeconds = clock.Elapsed.TotalSeconds;
        var heldSeconds = Interlocked.Read(ref sender.RtpUnits) / 48000.0;
        Assert.That(heldSeconds, Is.EqualTo(elapsedSeconds).Within(elapsedSeconds * 0.12), $"{sender.Frames} encoded frames hold {heldSeconds:0.00} s but {elapsedSeconds:0.00} s passed: not 2x");
        Assert.That(sender.Frames, Is.InRange((int)(elapsedSeconds * 50 * 0.88), (int)(elapsedSeconds * 50 * 1.12) + 1), "50 frames of 20 ms a second");
    }

    [Test]
    public async Task AudioGoesOutInRealTime_NotAllAtOnce()
    {
        var sender = new RecordingSender();
        using var streamer = new RobotSpeechStreamer(sender) { PlayoutLatency = TimeSpan.Zero };
        var clock = Stopwatch.StartNew();
        await streamer.SpeakAsync(Tone(600, 1000));                  // 600 ms of speech = 30 frames
        var elapsed = clock.ElapsedMilliseconds;
        Assert.That(elapsed, Is.InRange(540, 800), $"600 ms of speech took {elapsed} ms to send: it must be paced, not dumped");
        var speech = sender.Frames.Where(f => f.Speech).ToList();
        Assert.That(speech.Count, Is.EqualTo(30));
        var gaps = speech.Zip(speech.Skip(1), (a, b) => b.Ms - a.Ms).ToList();
        Assert.That(gaps.Average(), Is.InRange(17, 23), "20 ms between frames on average");
        Assert.That(gaps.Max(), Is.LessThan(60), "no long stall inside a clip");
    }

    [Test]
    public async Task BetweenClipsTheStreamKeepsGoingWithSilence_AndTwoClipsFollowEachOtherDirectly()
    {
        var sender = new RecordingSender();
        using var streamer = new RobotSpeechStreamer(sender) { PlayoutLatency = TimeSpan.Zero };
        var first = streamer.SpeakAsync(Tone(200, 2000));
        var second = streamer.SpeakAsync(Tone(200, 3000));          // queued behind the first
        await Task.WhenAll(first, second);
        await Task.Delay(300);                                       // quiet: only silence should be sent now
        var frames = sender.Frames.ToList();
        var order = frames.Select(f => !f.Speech ? 'S' : f.First < 2900 ? '1' : '2').ToArray();
        var text = new string(order);
        Assert.That(text.Replace("S", ""), Does.Contain("1111111111" + "2222222222"), "both clips went out in order");
        Assert.That(text.IndexOf('2') - text.LastIndexOf('1'), Is.EqualTo(1), "the second clip starts the frame after the first ends: no gap");
        Assert.That(text.TrimEnd('S').Length, Is.LessThan(text.Length - 8), "silence frames kept flowing after the speech (the stream never stops)");
        var silentTail = frames.Where(f => !f.Speech).Select(f => f.Ms).ToList();
        Assert.That(silentTail.Count, Is.GreaterThan(8));
    }

    [Test]
    public async Task CancellingStopsTheSpeechAtOnce_AndTheStreamReturnsToSilence()
    {
        var sender = new RecordingSender();
        using var streamer = new RobotSpeechStreamer(sender) { PlayoutLatency = TimeSpan.Zero };
        using var cts = new CancellationTokenSource();
        var speaking = streamer.SpeakAsync(Tone(3000, 4000), cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await speaking);
        var cut = sender.Frames.Count(f => f.Speech);
        await Task.Delay(300);
        Assert.That(sender.Frames.Count(f => f.Speech), Is.EqualTo(cut), "no speech frames after the cancel");
        Assert.That(cut, Is.InRange(5, 20), "it spoke for about 200 ms, not 3 s");
        Assert.That(sender.Frames.Last().Speech, Is.False, "silence again");
    }

    [Test]
    public async Task FlushDropsEverythingQueued()
    {
        var sender = new RecordingSender();
        using var streamer = new RobotSpeechStreamer(sender) { PlayoutLatency = TimeSpan.Zero };
        var a = streamer.SpeakAsync(Tone(2000, 5000));
        var b = streamer.SpeakAsync(Tone(2000, 6000));
        await Task.Delay(100);
        streamer.Flush();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await a);
        Assert.ThrowsAsync<TaskCanceledException>(async () => await b);
        await Task.Delay(200);
        Assert.That(sender.Frames.Count(f => f.Speech), Is.LessThan(12));
    }

    [Test]
    public void Upsample2x_DoublesTheRate_KeepingTheSamplesAndInterpolatingBetween()
    {
        var up = Vertex.Reachy.ReachyChannel.Upsample2x([100, 200, 400]);
        Assert.That(up, Is.EqualTo(new short[] { 100, 150, 200, 300, 400, 400 }));
    }
}
