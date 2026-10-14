using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// System One for speech on the GPU: all-MiniLM-L6-v2 through SpawnDev.ILGPU.ML matching what players say to the
/// urgent commands, and refusing what it should leave to the language model. Category GPU: needs CUDA and the
/// model (downloaded once from the SpawnDev hub, then cached).
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class FastIntentsTests
{
    private VertexAi _ai = null!;
    private FastIntents _intents = null!;

    [OneTimeSetUp]
    public async Task Load()
    {
        _ai = TestAi.Create();
        _intents = await _ai.LoadIntentsAsync();
    }

    [OneTimeTearDown]
    public async Task Dispose()
    {
        if (_ai != null) await _ai.DisposeAsync();
    }

    [TestCase("Vertex, follow me!", "follow_player")]
    [TestCase("hey vertex come with me", "follow_player")]
    [TestCase("Vertex come over here please", "come_here")]
    [TestCase("vertex get over here", "come_here")]
    [TestCase("STOP!", "stop")]
    [TestCase("Vertex, stand still", "stop")]
    [TestCase("vertex protect me", "guard")]
    [TestCase("Vertex, keep me safe from the monsters", "guard")]
    [TestCase("vertex it's late, go to bed", "sleep")]
    [TestCase("Vertex you need to eat something", "eat")]
    public async Task Matches(string said, string tool)
    {
        var match = await _intents.MatchAsync(said, "Vertex");
        TestContext.Out.WriteLine(string.Join(", ", (await _intents.ScoresAsync(said, "Vertex")).Take(3).Select(s => $"{s.Tool} {s.Score:0.000}")));
        Assert.That(match?.Intent.Tool, Is.EqualTo(tool));
    }

    [TestCase("Vertex, don't follow me")]
    [TestCase("stop following me")]
    [TestCase("I like turtles")]
    [TestCase("Vertex what's your favourite colour?")]
    [TestCase("can you build us a house over by the river")]
    [TestCase("vertex get some wood")]
    [TestCase("how are you doing today")]
    public async Task LeavesTheRestToTheLanguageModel(string said)
    {
        var match = await _intents.MatchAsync(said, "Vertex");
        TestContext.Out.WriteLine(string.Join(", ", (await _intents.ScoresAsync(said, "Vertex")).Take(3).Select(s => $"{s.Tool} {s.Score:0.000}")));
        Assert.That(match, Is.Null, $"matched {match?.Intent.Tool} at {match?.Score:0.000}");
    }
}
