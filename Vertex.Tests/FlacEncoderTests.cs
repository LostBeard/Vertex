using System.Diagnostics;
using SpawnDev.Reachy;

namespace Vertex.Tests;

/// <summary>The FLAC encoder that halves the size of a clip on its way to the robot. FLAC is lossless, so the only thing worth checking is that an independent decoder (ffmpeg) gets every sample back.</summary>
public class FlacEncoderTests
{
    private static readonly string? Ffmpeg = FindFfmpeg();

    private static string? FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    private static short[] Decode(byte[] flac, int channels)
    {
        if (Ffmpeg == null) Assert.Ignore("ffmpeg is not installed: no independent decoder to check against");
        var file = Path.Combine(Path.GetTempPath(), "vertex-flac-" + Guid.NewGuid().ToString("N") + ".flac");
        File.WriteAllBytes(file, flac);
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg!, $"-v error -i \"{file}\" -f s16le -") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            var errors = process.StandardError.ReadToEndAsync();
            using var raw = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(raw);
            process.WaitForExit();
            Assert.That(errors.Result.Trim(), Is.Empty, "ffmpeg found nothing wrong with the stream (bad CRC, bad header...)");
            var bytes = raw.ToArray();
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples;
        }
        finally { File.Delete(file); }
    }

    private static short[] SpeechFixture()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir != null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "Fixtures", "speech-24k.wav");
            if (File.Exists(candidate)) return ReadWav(candidate);
        }
        Assert.Ignore("speech fixture not found");
        return [];
    }

    private static short[] ReadWav(string path)
    {
        var b = File.ReadAllBytes(path);
        var pos = 12;
        while (pos + 8 <= b.Length)
        {
            var size = BitConverter.ToInt32(b, pos + 4);
            if (b[pos] == 'd' && b[pos + 1] == 'a' && b[pos + 2] == 't' && b[pos + 3] == 'a')
            {
                var samples = new short[Math.Min(size, b.Length - pos - 8) / 2];
                Buffer.BlockCopy(b, pos + 8, samples, 0, samples.Length * 2);
                return samples;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    private static void RoundTrips(short[] pcm, int rate, int channels = 1)
    {
        var flac = FlacEncoder.Encode(pcm, rate, channels);
        Assert.That(Decode(flac, channels), Is.EqualTo(pcm), "every sample comes back");
    }

    [Test]
    public void SpeechComesBackExactly_AtAboutHalfTheSize()
    {
        var pcm = SpeechFixture();
        var flac = FlacEncoder.Encode(pcm, 24000);
        TestContext.Out.WriteLine($"{pcm.Length} samples: {pcm.Length * 2} B as PCM, {flac.Length} B as FLAC ({flac.Length * 100.0 / (pcm.Length * 2):0.0}%)");
        Assert.That(Decode(flac, 1), Is.EqualTo(pcm));
        Assert.That(flac.Length, Is.LessThan(pcm.Length * 2 * 0.65), "speech has to shrink or the upload does not get shorter");
    }

    [Test]
    public void SilenceNoiseAndOddLengthsAllRoundTrip()
    {
        RoundTrips(new short[10000], 24000);                                                       // constant subframes
        var noise = new short[9000]; new Random(5).NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(noise.AsSpan()));
        RoundTrips(noise, 24000);                                                                  // verbatim subframes
        foreach (var n in new[] { 1, 2, 5, 100, 4095, 4096, 4097, 8192, 12345 })
        {
            var tone = new short[n];
            for (var i = 0; i < n; i++) tone[i] = (short)(Math.Sin(i * 0.05) * 9000 + (i % 7) * 13);
            RoundTrips(tone, 24000);
        }
        RoundTrips(Enumerable.Range(0, 5000).Select(i => (short)(i % 2 == 0 ? short.MaxValue : short.MinValue)).ToArray(), 24000);  // full-scale square
    }

    [Test]
    public void StereoAndOtherRatesRoundTrip()
    {
        var stereo = new short[20000];
        for (var i = 0; i < stereo.Length; i += 2) { stereo[i] = (short)(Math.Sin(i * 0.01) * 8000); stereo[i + 1] = (short)(Math.Cos(i * 0.013) * 5000); }
        RoundTrips(stereo, 48000, 2);
        RoundTrips(stereo.Take(5000).ToArray(), 16000, 1);
        RoundTrips(stereo.Take(5000).ToArray(), 22050, 1);
    }
}
