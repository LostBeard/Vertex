using System.Diagnostics;
using Vertex.Host;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// "Off" has to be off on a PC that plays games too. Measured 2026-11-01 on an RTX 2060 12 GB: after Stop the card still held 11.3 GB, until the app was quit. Now Stop keeps the
/// models loaded for a short grace period (a Stop and a quick Start stays fast) and then gives the GPU back; the next Start loads them again. This reads the card's own memory
/// counter (nvidia-smi), so it proves the memory really went back, not that a flag changed. Real models on the real GPU; settings live in a temp folder.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class GpuReleaseTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(5);

    /// <summary>Memory in use on GPU 0 in MiB, as the driver reports it.</summary>
    private static int UsedMiB()
    {
        using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return int.Parse(text.Split('\n')[0].Trim());
    }

    [Test]
    public async Task StopGivesTheGpuBack_AfterTheGracePeriod_AndAStartLoadsItAgain()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vertex-gpurelease-" + Guid.NewGuid().ToString("N"));
        await using var host = new VertexHost(folder) { UnloadAfterStop = TimeSpan.FromSeconds(6) };
        try
        {
            host.Settings.Minecraft = false;
            host.Settings.Reachy = false;
            Assert.That(host.NeedsModel, Is.False, "this test needs a language model in the Ollama cache");

            var idle = UsedMiB();
            await host.StartAsync().WaitAsync(Slow);
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);
            var loaded = UsedMiB();
            TestContext.Out.WriteLine($"GPU idle {idle} MiB, Vertex on {loaded} MiB");
            Assert.That(loaded, Is.GreaterThan(idle + 5000), "the models are on the card while Vertex is on");

            await host.StopAsync();
            Assert.That(host.State, Is.EqualTo(HostState.Idle));
            Assert.That(UsedMiB(), Is.GreaterThan(idle + 5000), "right after Stop the models are still loaded: a quick Start stays quick");

            await Wait.UntilAsync(() => UsedMiB() < idle + 1500, TimeSpan.FromSeconds(90), () => $"still {UsedMiB()} MiB (idle was {idle})");
            await Wait.UntilAsync(() => host.State == HostState.Idle, TimeSpan.FromSeconds(30), () => host.Status);
            var released = UsedMiB();
            TestContext.Out.WriteLine($"after the grace period: {released} MiB");
            Assert.That(released, Is.LessThan(idle + 1500), "the GPU went back to (about) what it was before Vertex");
            Assert.That(host.Lines.Any(l => l.Text.Contains("gave the GPU back")), "the log says so");

            // Off, then on again: the models load once more and Vertex is back.
            await host.StartAsync().WaitAsync(Slow);
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);
            Assert.That(UsedMiB(), Is.GreaterThan(idle + 5000), "a Start after the release loads the models again");
            await host.StopAsync();
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    [Test]
    public async Task AStartWithinTheGracePeriod_KeepsTheModelsLoaded_AndTheGpuIsNotReleasedUnderIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vertex-gpurelease-" + Guid.NewGuid().ToString("N"));
        await using var host = new VertexHost(folder) { UnloadAfterStop = TimeSpan.FromSeconds(20) };
        try
        {
            host.Settings.Minecraft = false;
            host.Settings.Reachy = false;
            var idle = UsedMiB();
            await host.StartAsync().WaitAsync(Slow);
            await host.StopAsync();
            var started = DateTime.UtcNow;
            await host.StartAsync().WaitAsync(Slow);
            var quick = DateTime.UtcNow - started;
            TestContext.Out.WriteLine($"start within the grace period took {quick.TotalSeconds:0.0} s");
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);

            // Well past the 20 s grace period of the FIRST stop: the pending release must have been cancelled by the Start.
            await Task.Delay(TimeSpan.FromSeconds(30));
            Assert.That(host.State, Is.EqualTo(HostState.Playing), "still on: the release did not fire under a running Vertex");
            Assert.That(UsedMiB(), Is.GreaterThan(idle + 5000), "the models are still on the card");
            await host.StopAsync();
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
