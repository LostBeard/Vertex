using Vertex.Host;

namespace Vertex.Tests;

/// <summary>
/// "Start Vertex when the app starts": a PC that is Vertex's own (TJ, 2026-11-01: Aubs does not use her PC; "Autostart it please") comes up running by itself after Windows logs in.
/// The setting is off by default (a started Vertex holds ~11 GB of the GPU).
/// </summary>
public class StartOnLaunchTests
{
    private string _root = "";

    [SetUp] public void Root() => _root = Path.Combine(Path.GetTempPath(), "vertex-startonlaunch-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Clean() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { } }

    [Test]
    public async Task Off_ByDefault_NothingStarts_AndNothingIsSaid()
    {
        await using var host = new VertexHost(Path.Combine(_root, "data")) { ModelsRoot = Path.Combine(_root, "models") };
        Assert.That(host.Settings.StartOnLaunch, Is.False, "off unless someone turns it on");
        await host.StartOnLaunchAsync();
        Assert.That(host.State, Is.EqualTo(HostState.Idle));
        Assert.That(host.Lines.Any(l => l.Text.Contains("by itself")), Is.False);
    }

    [Test]
    public async Task On_ButNoModelYet_SaysWhy_AndStaysOff_InsteadOfFailing()
    {
        await using var host = new VertexHost(Path.Combine(_root, "data")) { ModelsRoot = Path.Combine(_root, "models") };
        host.RefreshModels();
        host.Settings.StartOnLaunch = true;
        Assert.That(host.NeedsModel, Is.True);
        await host.StartOnLaunchAsync();
        Assert.That(host.State, Is.EqualTo(HostState.Idle), "no model: it cannot start, and does not pretend to");
        Assert.That(host.Lines.Select(l => l.Text), Has.Some.Contains("not starting by itself").And.Some.Contains("language model"));
        Assert.That(host.Lines.Any(l => l.Kind == LineKind.Error), Is.False, "not an error: a new PC has no model yet");
    }

    [Test]
    public void TheSetting_IsKept_WhenSettingsAreSavedAndLoadedAgain()
    {
        var first = VertexSettings.Load(_root);
        first.StartOnLaunch = true;
        first.Save();
        Assert.That(VertexSettings.Load(_root).StartOnLaunch, Is.True);
    }
}

/// <summary>The real thing: with the setting on and a model installed, the app comes up running with nobody pressing Start. Real models on the real GPU; settings live in a temp folder.</summary>
[Category("GPU")]
[NonParallelizable]
public class StartOnLaunchGpuTests
{
    [Test]
    public async Task On_WithAModel_VertexComesUpRunningByItself()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vertex-startonlaunch-" + Guid.NewGuid().ToString("N"));
        await using var host = new VertexHost(folder) { UnloadAfterStop = TimeSpan.FromSeconds(3) };
        try
        {
            host.Settings.StartOnLaunch = true;
            host.Settings.Minecraft = false;
            host.Settings.Reachy = false;
            Assert.That(host.NeedsModel, Is.False, "this test needs a language model in the Ollama cache");
            Assert.That(host.State, Is.EqualTo(HostState.Idle), "nobody has pressed Start");

            await host.StartOnLaunchAsync().WaitAsync(TimeSpan.FromMinutes(5));
            Assert.That(host.State, Is.EqualTo(HostState.Playing), host.Status);
            await host.StopAsync();
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
