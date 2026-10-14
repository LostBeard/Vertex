namespace Vertex.AI;

/// <summary>What a stretch of audio measured: its length, level, loudest sample and how much of its energy is rumble.</summary>
/// <param name="Seconds">Length.</param>
/// <param name="RmsDb">Average level, dB below full scale.</param>
/// <param name="PeakDb">Loudest sample, dB below full scale (0 = clipping).</param>
/// <param name="LowFraction">Share of the energy below 300 Hz (a voice has harmonics above that; a thump or a hum does not).</param>
public readonly record struct AudioMeasure(double Seconds, double RmsDb, double PeakDb, double LowFraction);

/// <summary>
/// Is this a voice at all? The robot's microphone is open all day, and the voice detector will call a click, a bump of the desk, a servo or a breath
/// "speech"; the speech recogniser then writes the nearest word it knows ("Yeah.", "Ha ha.", "No.") for something nobody said. Live (2026-10-14, a silent room)
/// it heard "Yeah." (0.49 s), "Ha ha." (0.62 s, clipping at -0.2 dBFS), "Yeah." (0.36 s, -45 dB) and "No." (0.36 s, -39 dB). This runs BEFORE
/// transcription, on numbers only.
/// </summary>
/// <remarks>
/// The thresholds come from that session's saved clips (tools in _speech-research/heard_audit.py), and the real utterances to the robot that day
/// were: 0.75 s to 2.6 s, -22 to -35 dB RMS, peak at least 4 dB below full scale, a voice-like spectrum. A short answer to a question the robot just
/// asked ("yes", "no") is real and is let through while it is waiting for one.
/// </remarks>
public static class NoiseGate
{
    /// <summary>Shorter than this is not a sentence to the robot. 0.65 s: the longest phantom was 0.62 s, the shortest real utterance 0.75 s.</summary>
    public const double MinSeconds = 0.65;

    /// <summary>Quieter than this (RMS) is hiss, not a voice: the quietest real utterance was -35 dB, the hiss "Yeah." -45 dB.</summary>
    public const double MinRmsDb = -42;

    /// <summary>A short sound this loud is a click or a knock against the microphone (the "Ha ha." was -0.2 dBFS; the loudest voice, shouting, -4 dBFS).</summary>
    public const double ClickPeakDb = -1.0;

    /// <summary>A short sound with more than this share of its energy below 300 Hz is a thump or a hum, not a voice (the thumps were 0.91 to 1.00; voices 0.16 to 0.63).</summary>
    public const double RumbleFraction = 0.9;

    /// <summary>Measures 16 kHz mono audio in [-1, 1].</summary>
    public static AudioMeasure Measure(float[] pcm)
    {
        if (pcm.Length == 0) return new AudioMeasure(0, -120, -120, 0);
        double sum = 0, peak = 0;
        foreach (var s in pcm) { sum += s * s; var a = Math.Abs(s); if (a > peak) peak = a; }

        // A 2nd-order Butterworth low-pass at 300 Hz (bilinear transform at 16 kHz): the energy it keeps is the energy below about 300 Hz.
        const double fs = 16000, fc = 300;
        var k = Math.Tan(Math.PI * fc / fs);
        var q = Math.Sqrt(2) / 2;
        var norm = 1 / (1 + k / q + k * k);
        double b0 = k * k * norm, b1 = 2 * b0, b2 = b0;
        double a1 = 2 * (k * k - 1) * norm, a2 = (1 - k / q + k * k) * norm;
        double z1 = 0, z2 = 0, low = 0;
        foreach (var x in pcm)
        {
            var y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            low += y * y;
        }
        return new AudioMeasure(pcm.Length / fs, 10 * Math.Log10(sum / pcm.Length + 1e-12), 20 * Math.Log10(peak + 1e-9), sum > 0 ? Math.Min(1, low / sum) : 0);
    }

    /// <summary>Why this is not a voice worth transcribing, or null when it might be.</summary>
    /// <param name="expectingAnswer">The robot has just asked something: a one-word answer is real.</param>
    public static string? Reject(AudioMeasure m, bool expectingAnswer)
    {
        if (m.Seconds < 1.0 && m.PeakDb > ClickPeakDb) return "a click";
        if (m.RmsDb < MinRmsDb) return "too quiet to be a voice";
        if (m.Seconds < 1.0 && m.LowFraction > RumbleFraction) return "a thump";
        if (m.Seconds < MinSeconds && !expectingAnswer) return "too short to be said to the robot";
        return null;
    }

    public static string? Reject(float[] pcm, bool expectingAnswer) => Reject(Measure(pcm), expectingAnswer);
}
