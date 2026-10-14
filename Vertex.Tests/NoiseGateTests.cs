using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// Sound that is not a voice never reaches the speech recogniser. The numbers are the live session of 2026-10-14 (heard_audit.py over the saved
/// clips): a silent room produced "Yeah.", "Ha ha.", "Yeah." and "No."; TJ's real utterances to the robot are the other rows.
/// </summary>
public class NoiseGateTests
{
    // (seconds, rms dB, peak dB, share below 300 Hz) of clips the robot heard in a SILENT room: nobody said these.
    [TestCase(0.49, -29.3, -13.9, 0.21, "Yeah.", TestName = "Phantom 'Yeah.' 0.49 s")]
    [TestCase(0.62, -19.6, -0.2, 0.00, "Ha ha.", TestName = "Phantom 'Ha ha.' 0.62 s, clipping")]
    [TestCase(0.36, -45.3, -32.6, 0.52, "Yeah.", TestName = "Phantom 'Yeah.' 0.36 s, near silent")]
    [TestCase(0.36, -38.8, -24.3, 0.62, "No.", TestName = "Phantom 'No.' 0.36 s")]
    [TestCase(0.33, -52.8, -37.9, 0.10, "Yeah.", TestName = "Phantom 'Yeah.' 0.33 s, hiss")]
    [TestCase(0.36, -48.8, -33.8, 0.07, "Okay.", TestName = "Phantom 'Okay.' 0.36 s, near silent")]
    [TestCase(0.40, -29.2, -20.0, 0.98, "Yeah.", TestName = "Phantom thump 'Yeah.' 98% rumble")]
    [TestCase(0.40, -26.2, -14.2, 1.00, "Mm-hmm.", TestName = "Phantom thump 'Mm-hmm.' 100% rumble")]
    [TestCase(0.85, -28.0, -12.0, 0.97, "a long bump", TestName = "A bump long enough to pass the length rule, 97% rumble")]
    public void TheSoundsOfASilentRoom_AreNotTranscribed(double seconds, double rms, double peak, double low, string wrote)
    {
        var why = NoiseGate.Reject(new AudioMeasure(seconds, rms, peak, low), expectingAnswer: false);
        Assert.That(why, Is.Not.Null, $"'{wrote}' would have been transcribed");
    }

    // The same session's real speech to the robot (and around it).
    [TestCase(1.58, -22.8, -6.2, 0.22, "Vertex. Hi, I'm Todd.")]
    [TestCase(1.77, -29.9, -10.9, 0.20, "Vertex, what is the capital of France?")]
    [TestCase(1.32, -21.8, -6.7, 0.31, "Rutax, what's the")]
    [TestCase(0.75, -35.4, -19.0, 0.16, "Per text.")]
    [TestCase(1.48, -30.1, -13.2, 0.55, "Best thing to build first.")]
    [TestCase(0.88, -33.0, -15.0, 0.30, "Vertex.")]
    [TestCase(2.57, -23.6, -4.1, 0.40, "a loud, shouted sentence")]
    [TestCase(1.04, -29.2, -11.1, 0.60, "Yeah, love you.")]
    public void RealSpeech_GoesThrough(double seconds, double rms, double peak, double low, string said) =>
        Assert.That(NoiseGate.Reject(new AudioMeasure(seconds, rms, peak, low), expectingAnswer: false), Is.Null, said);

    [Test]
    public void AOneWordAnswer_IsRealWhileTheRobotIsWaitingForOne()
    {
        var yes = new AudioMeasure(0.45, -30, -15, 0.35);
        Assert.That(NoiseGate.Reject(yes, expectingAnswer: false), Is.Not.Null);
        Assert.That(NoiseGate.Reject(yes, expectingAnswer: true), Is.Null);
        // ...but a click or hiss is never an answer.
        Assert.That(NoiseGate.Reject(new AudioMeasure(0.45, -19, -0.2, 0.0), expectingAnswer: true), Is.Not.Null);
        Assert.That(NoiseGate.Reject(new AudioMeasure(0.45, -50, -35, 0.2), expectingAnswer: true), Is.Not.Null);
    }

    /// <summary>The measurement itself, on signals whose answer is known, and on real speech (twelve LibriSpeech clips, which must all pass).</summary>
    [Test]
    public void TheMeasurement_TellsRumbleFromAVoice_AndEveryRealClipPasses()
    {
        var rate = 16000;
        float[] Tone(double hz, double seconds, double amp) => Enumerable.Range(0, (int)(rate * seconds)).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

        var rumble = NoiseGate.Measure(Tone(80, 0.8, 0.3));
        Assert.That(rumble.LowFraction, Is.GreaterThan(0.9), "an 80 Hz hum is rumble");
        var hiss = NoiseGate.Measure(Tone(3000, 0.8, 0.3));
        Assert.That(hiss.LowFraction, Is.LessThan(0.05), "a 3 kHz tone is not");
        var quiet = NoiseGate.Measure(Tone(1000, 1.0, 0.001));
        Assert.That(quiet.RmsDb, Is.LessThan(NoiseGate.MinRmsDb));
        Assert.That(NoiseGate.Reject(Tone(1000, 0.5, 1.0), false), Is.EqualTo("a click"), "a full-scale burst");
        Assert.That(NoiseGate.Reject(new float[16000], false), Is.EqualTo("too quiet to be a voice"), "digital silence");

        foreach (var (file, pcm, _) in SmartTurnTests.Clips())
        {
            var m = NoiseGate.Measure(pcm);
            TestContext.Progress.WriteLine($"[gate] {file}: {m.Seconds:0.0} s, {m.RmsDb:0.0} dB rms, {m.PeakDb:0.0} dB peak, {m.LowFraction:0.00} below 300 Hz");
            Assert.That(NoiseGate.Reject(pcm, false), Is.Null, file);
            // And its loudest half second (a short word out of a sentence, heard on its own) is only ever refused for being short, never for its level or spectrum.
            int best = 0; double bestEnergy = -1;
            for (int at = 0; at + 8000 <= pcm.Length; at += 800)
            {
                double e = 0;
                for (int i = at; i < at + 8000; i++) e += pcm[i] * pcm[i];
                if (e > bestEnergy) { bestEnergy = e; best = at; }
            }
            var word = NoiseGate.Measure(pcm[best..(best + 8000)]);
            Assert.That(NoiseGate.Reject(word, true), Is.Null, $"{file} (loudest 0.5 s: {word.RmsDb:0.0} dB, {word.LowFraction:0.00} low)");
        }
    }
}
