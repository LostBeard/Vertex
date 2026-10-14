using ILGPU;
using ILGPU.Runtime;
using SpawnDev.ILGPU.ML.Pipelines;
using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// The voice detector must keep listening while the language model holds the GPU (a reply's prefill is ~1.5 s). MEASURED here: the cost of one 32 ms frame of Silero VAD on the CPU
/// backend and on CUDA, to see whether a detector of its own, off the GPU, keeps up with real time.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class VadSpeedTests
{
    private static byte[] SileroModel()
    {
        using var s = typeof(MicrophoneEars).Assembly.GetManifestResourceStream("Vertex.Reachy.silero_vad.onnx")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task<(double MsPerFrame, double Prob)> RunAsync(Accelerator accelerator, int frames)
    {
        var vad = SileroVad.Create(accelerator, SileroModel());
        var rnd = new Random(3);
        var frame = Enumerable.Range(0, SileroVad.WindowSize).Select(i => (float)(0.1 * Math.Sin(i * 0.3) + 0.01 * (rnd.NextDouble() - 0.5))).ToArray();
        for (var i = 0; i < 5; i++) await vad.ProcessFrameAsync(frame);       // warm
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var p = 0f;
        for (var i = 0; i < frames; i++) p = await vad.ProcessFrameAsync(frame);
        return (clock.Elapsed.TotalMilliseconds / frames, p);
    }

    [Test]
    public async Task SileroOnTheCpu_KeepsUpWithRealTime()
    {
        await using var ai = TestAi.Create();
        var cpu = ai.CpuAccelerator;
        var (cpuMs, cpuP) = await RunAsync(cpu, 150);
        var (gpuMs, gpuP) = await RunAsync(ai.Accelerator, 150);
        TestContext.Progress.WriteLine($"[vad] one 32 ms frame: CPU backend {cpuMs:0.00} ms (p {cpuP:0.000}), {ai.Accelerator.AcceleratorType} {gpuMs:0.00} ms (p {gpuP:0.000}); real time is 32 ms");
        Assert.That(cpuMs, Is.LessThan(16), "the CPU detector would not keep up with real time (needs under half a frame of headroom)");
        Assert.That(Math.Abs(cpuP - gpuP), Is.LessThan(0.01), "the CPU and GPU detectors disagree");
    }
}
