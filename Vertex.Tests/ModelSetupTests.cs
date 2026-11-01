using SpawnDev.AI.Server;
using Vertex.AI;
using Vertex.Host;

namespace Vertex.Tests;

/// <summary>
/// Vertex installs its own language model (no Ollama): ModelSetup over SpawnDev.AI.Server's OllamaModelInstaller, the dashboard host's Install flow, and the real thing end to end.
/// Everything here talks to the real services (models.spawndev.com, registry.ollama.ai) and writes into a throwaway Ollama-layout cache, never the user's. The small tests use
/// all-minilm:22m (about 46 MB): models.spawndev.com does not host it, so they also prove the fall-back from the first source to registry.ollama.ai. The 4.9 GB default model
/// is the Explicit test at the bottom.
/// </summary>
[Category("Models")]
[NonParallelizable]
public class ModelSetupTests
{
    private const string Small = "all-minilm:22m";
    private string _root = "";

    [SetUp]
    public void Root() => _root = Path.Combine(Path.GetTempPath(), "vertex-modelsetup-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void Clean() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { } }

    [Test]
    public void Describe_SaysWhatIsHappening_InPlainWords()
    {
        var down = new OllamaInstallProgress("llama3.1:8b", "layer", "application/vnd.ollama.image.model", "models.spawndev.com", 1_200_000_000, 4_920_738_944, 1_200_000_000, 4_920_753_328, 35_000_000);
        Assert.That(ModelSetup.Describe(down), Is.EqualTo("Downloading llama3.1:8b: 1.2 GB of 4.9 GB (35 MB/s) from models.spawndev.com"));
        Assert.That(ModelSetup.Fraction(down), Is.EqualTo(1.2e9 / 4920753328d).Within(1e-9));
        Assert.That(ModelSetup.Describe(down with { Stage = "manifest" }), Is.EqualTo("Looking up llama3.1:8b..."));
        Assert.That(ModelSetup.Describe(down with { Stage = "done" }), Is.EqualTo("llama3.1:8b is installed."));
        Assert.That(ModelSetup.Fraction(down with { TotalBytes = 0 }), Is.EqualTo(0), "no total yet is 0, not a divide by zero");
        Assert.That(ModelSetup.Size(812_000), Is.EqualTo("812 KB"));
    }

    [Test]
    public async Task ASmallRealModel_IsNotInstalled_ThenInstallsFromTheRealSources_AndIsInstalled()
    {
        Assert.That(ModelSetup.IsInstalled(Small, _root), Is.False, "an empty cache has nothing");
        var seen = new List<OllamaInstallProgress>();
        var model = await ModelSetup.InstallAsync(Small, seen.Add, modelsRoot: _root);

        Assert.That(model.Name, Is.EqualTo(Small));
        Assert.That(ModelSetup.IsInstalled(Small, _root), Is.True);
        Assert.That(ModelSetup.Installed(_root), Does.Contain(Small));
        Assert.That(new FileInfo(model.GgufPath).Length, Is.EqualTo(model.GgufSize).And.GreaterThan(10_000_000), "the real model file is on disk, whole");
        Assert.That(seen.Select(p => p.Stage), Does.Contain("manifest").And.Contain("layer").And.Contain("done"));
        var done = seen.Select(p => p.DoneBytes).ToList();
        Assert.That(done, Is.Ordered, "the bytes done never go backwards");
        Assert.That(seen.Last().DoneBytes, Is.EqualTo(seen.Last().TotalBytes).And.GreaterThan(0));
        TestContext.Out.WriteLine($"installed {model.Name}: {ModelSetup.Size(seen.Last().TotalBytes)} from {seen.First(p => p.Source != null).Source}");

        // Already complete: nothing is downloaded again.
        var again = new List<OllamaInstallProgress>();
        await ModelSetup.InstallAsync(Small, again.Add, modelsRoot: _root);
        Assert.That(again.Where(p => p.Stage == "layer"), Is.Empty, "a complete model downloads nothing");
    }

    [Test]
    public async Task StoppingKeepsWhatWasDownloaded_ASecondInstallFinishes_AndAHalfDoneModelIsNotListed()
    {
        using var stop = new CancellationTokenSource();
        var cancelled = false;
        var ex = Assert.CatchAsync<OperationCanceledException>(async () => await ModelSetup.InstallAsync(Small, p =>
        {
            if (p.Stage == "layer" && p.LayerBytes > 0 && !cancelled) { cancelled = true; stop.Cancel(); }
        }, stop.Token, _root));
        Assert.That(ex, Is.Not.Null);
        Assert.That(ModelSetup.IsInstalled(Small, _root), Is.False, "the manifest is written last: a stopped install is invisible, not broken");
        Assert.That(ModelSetup.Installed(_root), Is.Empty);
        Assert.That(Directory.EnumerateFiles(Path.Combine(_root, "blobs"), "*.partial"), Is.Not.Empty, "the downloaded part is kept");

        await ModelSetup.InstallAsync(Small, modelsRoot: _root);
        Assert.That(ModelSetup.IsInstalled(Small, _root), Is.True, "the next install carries on and completes");
        Assert.That(Directory.EnumerateFiles(Path.Combine(_root, "blobs"), "*.partial"), Is.Empty, "no partial is left behind");
    }

    [Test]
    public async Task TheDashboardHost_SaysTheModelIsMissing_InstallsIt_ShowsProgress_AndStopsNeedingOne()
    {
        var data = Path.Combine(_root, "data");
        await using var host = new VertexHost(data) { ModelsRoot = Path.Combine(_root, "models") };
        host.RefreshModels();
        Assert.That(host.NeedsModel, Is.True, "no model in this cache: the first thing to do is install one");
        Assert.That(host.Models, Is.Empty);
        Assert.That(host.ModelInstall, Is.Null);

        var states = new List<ModelInstallStatus>();
        host.Changed += () => { if (host.ModelInstall is { } s) lock (states) states.Add(s); };
        await host.InstallModelAsync(Small);

        Assert.That(host.NeedsModel, Is.False);
        Assert.That(host.Models, Does.Contain(Small));
        Assert.That(host.Settings.Model, Is.EqualTo(Small), "the only model there is becomes the chosen one");
        Assert.That(host.ModelInstall, Is.Not.Null.And.Property("Running").False.And.Property("Error").Null);
        lock (states)
        {
            Assert.That(states.Any(s => s.Running && s.Fraction > 0 && s.Fraction < 1), Is.True, "the page saw it running part of the way");
            Assert.That(states.Where(s => s.Running).Select(s => s.Fraction), Is.Ordered, "the progress bar only moves forward");
            Assert.That(states.First(s => s.Running).Text, Is.Not.Empty);
        }
    }

    [Test]
    public async Task TheDashboardHost_SaysWhyAnInstallFailed_AndStaysUsable()
    {
        await using var host = new VertexHost(Path.Combine(_root, "data")) { ModelsRoot = Path.Combine(_root, "models") };
        host.RefreshModels();
        await host.InstallModelAsync("vertex-no-such-model:1b");

        Assert.That(host.ModelInstall, Is.Not.Null);
        Assert.That(host.ModelInstall!.Running, Is.False, "it is not left looking busy");
        Assert.That(host.ModelInstall.Error, Does.Contain("vertex-no-such-model"), "the page can say what failed");
        Assert.That(host.NeedsModel, Is.True);

        await host.InstallModelAsync(Small);
        Assert.That(host.ModelInstall!.Error, Is.Null, "a later install is not blocked by the earlier failure");
        Assert.That(host.NeedsModel, Is.False);
    }

    /// <summary>
    /// The real default model, 4.9 GB, from models.spawndev.com ALONE (registry.ollama.ai is not a source here, so a pass proves the host serves everything the install needs): the
    /// manifest and every layer, each checked against its digest by the installer. Explicit: it downloads 4.9 GB and needs about 6 GB free in the temp folder.
    /// </summary>
    [Test, Explicit("downloads 4.9 GB")]
    public async Task TheDefaultModel_InstallsFromModelsSpawndevCom_Alone()
    {
        var seen = new List<OllamaInstallProgress>();
        var started = DateTime.UtcNow;
        var model = await ModelSetup.InstallAsync(ModelSetup.DefaultModel, seen.Add, modelsRoot: _root, sources: [OllamaModelSource.SpawnDevModels]);

        Assert.That(model.Name, Is.EqualTo(ModelSetup.DefaultModel));
        Assert.That(model.GgufSize, Is.EqualTo(4_920_738_944));
        Assert.That(seen.Where(p => p.Source != null).Select(p => p.Source).Distinct(), Is.EqualTo(new[] { "models.spawndev.com" }));
        Assert.That(ModelSetup.IsInstalled(ModelSetup.DefaultModel, _root), Is.True);
        TestContext.Out.WriteLine($"installed {model.Name} ({ModelSetup.Size(seen.Last().TotalBytes)}) in {(DateTime.UtcNow - started).TotalSeconds:0} s");
    }
}
