namespace Vertex.AI;

/// <summary>What a stretch of audio measured: its length, level, loudest sample and how much of its energy is rumble.</summary>
/// <param name="Seconds">Length.</param>
/// <param name="RmsDb">Average level, dB below full scale.</param>
/// <param name="PeakDb">Loudest sample, dB below full scale (0 = clipping).</param>
/// <param name="LowFraction">Share of the energy below 300 Hz (a voice has harmonics above that; a thump or a hum does not).</param>
/// <param name="Flatness">Spectral flatness (geometric over arithmetic mean of the power spectrum): near 0 for a tone or a voice, toward 1 for noise. Measured only for sounds under a second.</param>
public readonly record struct AudioMeasure(double Seconds, double RmsDb, double PeakDb, double LowFraction, double Flatness = 0);

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

    /// <summary>
    /// A short sound this noise-like is not a voice. MEASURED on the live session's saved clips: the phantoms "Yeah." (0.46 s) 0.28, "Yeah." (0.65 s) 0.27, "Ha ha." 0.19,
    /// "Yeah." 0.14, "Yeah, yeah, but" 0.13 and "Okay." 0.12; every real short utterance ("Vertex." x5, "Okay.", "Yeah, love you.", "Which you could.") 0.002 to 0.055.
    /// </summary>
    public const double NoiseFlatness = 0.1;

    /// <summary>Spectral flatness of up to the first 16384 samples (a Hann-windowed, zero-padded power-of-two FFT).</summary>
    public static double SpectralFlatness(float[] pcm)
    {
        var n = Math.Min(pcm.Length, 16384);
        if (n < 256) return 0;
        var size = 1; while (size < n) size <<= 1;
        var re = new double[size]; var im = new double[size];
        for (var i = 0; i < n; i++) re[i] = pcm[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        for (int i = 1, j = 0; i < size; i++)           // bit reversal
        {
            var bit = size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); }
        }
        for (var len = 2; len <= size; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < size; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k; var b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci; var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    var nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
        double logSum = 0, sum = 0; var bins = size / 2 + 1;
        for (var i = 0; i < bins; i++) { var p = re[i] * re[i] + im[i] * im[i] + 1e-12; logSum += Math.Log(p); sum += p; }
        return Math.Exp(logSum / bins) / (sum / bins);
    }

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
        var seconds = pcm.Length / fs;
        return new AudioMeasure(seconds, 10 * Math.Log10(sum / pcm.Length + 1e-12), 20 * Math.Log10(peak + 1e-9), sum > 0 ? Math.Min(1, low / sum) : 0,
            seconds < 1.0 ? SpectralFlatness(pcm) : 0);
    }

    /// <summary>Why this is not a voice worth transcribing, or null when it might be.</summary>
    /// <param name="expectingAnswer">The robot has just asked something: a one-word answer is real.</param>
    public static string? Reject(AudioMeasure m, bool expectingAnswer)
    {
        if (m.Seconds < 1.0 && m.PeakDb > ClickPeakDb) return "a click";
        if (m.RmsDb < MinRmsDb) return "too quiet to be a voice";
        if (m.Seconds < 1.0 && m.LowFraction > RumbleFraction) return "a thump";
        if (m.Seconds < 1.0 && m.Flatness > NoiseFlatness) return "noise, not a voice";
        if (m.Seconds < MinSeconds && !expectingAnswer) return "too short to be said to the robot";
        return null;
    }

    public static string? Reject(float[] pcm, bool expectingAnswer) => Reject(Measure(pcm), expectingAnswer);

    /// <summary>
    /// A sound the gate refuses that could still be someone CALLING the robot: the length of a spoken name, loud enough to be a voice and not rumble. Live, TJ's loud,
    /// close "Vertex." (0.81 s) peaked at full scale and was thrown out as "a click", so nothing ever opened the conversation. Such a sound is transcribed anyway
    /// and kept only if the words contain the name; "Yeah." and "Okay." never do.
    /// </summary>
    public static bool MayBeTheName(AudioMeasure m) =>
        m.Seconds is >= 0.4 and <= 1.3 && m.RmsDb >= MinRmsDb && m.LowFraction <= RumbleFraction;
}
