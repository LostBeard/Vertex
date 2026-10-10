namespace Vertex.AI;

/// <summary>Sample-rate conversion between voice chat (48 kHz), Whisper (16 kHz) and Kokoro (24 kHz).</summary>
public static class Audio
{
    // A 63-tap windowed-sinc low-pass at 7.2 kHz (48 kHz rate): keeps speech, removes what would alias when
    // keeping every third sample for 16 kHz.
    private static readonly float[] Decimate3Taps = BuildLowPass(63, 7200.0 / 48000.0);

    private static float[] BuildLowPass(int taps, double cutoff)
    {
        var h = new float[taps];
        var m = taps - 1;
        double sum = 0;
        for (var i = 0; i < taps; i++)
        {
            var x = i - m / 2.0;
            var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / m) + 0.08 * Math.Cos(4 * Math.PI * i / m); // Blackman
            h[i] = (float)(sinc * window);
            sum += h[i];
        }
        for (var i = 0; i < taps; i++) h[i] /= (float)sum;
        return h;
    }

    /// <summary>48 kHz 16-bit PCM to 16 kHz float [-1, 1] (low-pass, then every third sample).</summary>
    public static float[] Downsample48To16(ReadOnlySpan<short> pcm48)
    {
        var taps = Decimate3Taps;
        var half = taps.Length / 2;
        var output = new float[pcm48.Length / 3];
        for (var o = 0; o < output.Length; o++)
        {
            var center = o * 3;
            float acc = 0;
            for (var k = 0; k < taps.Length; k++)
            {
                var i = center + k - half;
                if ((uint)i < (uint)pcm48.Length) acc += taps[k] * pcm48[i];
            }
            output[o] = acc / 32768f;
        }
        return output;
    }

    /// <summary>24 kHz float [-1, 1] to 48 kHz 16-bit PCM (linear interpolation; speech has nothing above 12 kHz).</summary>
    public static short[] Upsample24To48(ReadOnlySpan<float> pcm24, float gain = 1.0f)
    {
        var output = new short[pcm24.Length * 2];
        for (var i = 0; i < pcm24.Length; i++)
        {
            var a = pcm24[i];
            var b = i + 1 < pcm24.Length ? pcm24[i + 1] : a;
            output[2 * i] = ToShort(a * gain);
            output[2 * i + 1] = ToShort((a + b) * 0.5f * gain);
        }
        return output;
    }

    private static short ToShort(float v) => (short)Math.Clamp(v * 32767f, -32768f, 32767f);

    /// <summary>Root-mean-square level of 16-bit PCM (0..1).</summary>
    public static double Rms(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length == 0) return 0;
        double s = 0;
        foreach (var x in pcm) s += (double)x * x;
        return Math.Sqrt(s / pcm.Length) / 32768.0;
    }
}
