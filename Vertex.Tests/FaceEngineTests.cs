using ILGPU.Runtime;
using SpawnDev.AI.Server;
using SpawnDev.ILGPU.ML;
using SpawnDev.ILGPU.ML.Hub;
using Vertex.People;

namespace Vertex.Tests;

/// <summary>
/// The face engine and the face watcher together, on real pictures (public-domain official portraits, Fixtures/faces/PROVENANCE.md): a stranger is filed as a visitor, an introduction
/// teaches that face a name, a DIFFERENT photograph of the same person is then recognised by it, and a different person is not. The two small networks run on the CPU accelerator
/// (a look takes a second or two there), so this never touches a GPU somebody else is using; the models (about 38 MB) come from the opencv Hugging Face repos once and are cached.
/// </summary>
[Category("Models")]
public class FaceEngineTests
{
    private static (byte[] Bgra, int Width, int Height) Picture(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "faces", name + ".bgra"));
        return (bytes, 360, bytes.Length / (360 * 4));
    }

    [Test]
    public async Task AnIntroducedFaceIsRecognisedInAnotherPhoto_AndAnotherPersonIsNot()
    {
        using var context = MLContext.Create().ToContext();
        using var accelerator = context.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(context);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var engine = new AiFaceEngine(new HttpClientModelSource(http, FileModelStore.Default()), accelerator);
        var watcher = new FaceWatcher(engine.Registry, log: m => TestContext.Out.WriteLine("[faces] " + m));

        var (a, aw, ah) = Picture("trump_2017");
        var first = await engine.LookAsync(a, aw, ah);
        Assert.That(first, Has.Count.EqualTo(1));
        Assert.That(first[0].Guest?.Name, Is.EqualTo("Visitor 1"), "a stranger is a visitor, not a guest: a voice's strangers are Guest N");
        watcher.Observe(first, new Dictionary<string, long>(), 1000);

        // "Vertex, I'm Ada": the next few looks at the face in front of the robot are Ada's.
        watcher.BeginIntroduction("Ada", forNewFace: false, nowTick: 1500);
        for (var i = 0; i < 4; i++)
            watcher.Observe(await engine.LookAsync(a, aw, ah), new Dictionary<string, long>(), 3000 + i * 2000L);
        var ada = engine.Registry.Profiles.Single();
        Assert.That(ada.Name, Is.EqualTo("Ada"));
        Assert.That(ada.IsGuest, Is.False);

        // A different photograph of the same person, eight years later.
        var (b, bw, bh) = Picture("trump_2025");
        var again = await engine.LookAsync(b, bw, bh, observe: false);
        TestContext.Out.WriteLine($"other photo: {again[0].Match!.Name ?? "nobody"} at {again[0].Match!.Score:0.000}");
        Assert.That(again[0].Match!.Known, Is.True, "the same person in a different photograph is recognised");
        Assert.That(again[0].Match!.Name, Is.EqualTo("Ada"));

        // Somebody else is not Ada.
        var (c, cw, ch) = Picture("biden_2013");
        var other = await engine.LookAsync(c, cw, ch, observe: false);
        TestContext.Out.WriteLine($"another person: {other[0].Match!.Name ?? "nobody"} at {other[0].Match!.Score:0.000}");
        Assert.That(other[0].Match!.Known, Is.False, "a different person is not recognised as Ada");
    }
}
