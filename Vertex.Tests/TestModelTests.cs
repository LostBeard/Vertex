namespace Vertex.Tests;

/// <summary>Which model a test uses: VERTEX_TEST_MODEL (a prefix) or llama3.1. The seven fixtures that hard-coded llama3.1 could not be pointed at another model; this is the one rule they share now.</summary>
[NonParallelizable]
public class TestModelTests
{
    private static readonly string[] Cache = ["gemma4:latest", "llama3.1:8b", "qwen3.5:9b"];
    private string? _saved;

    [SetUp] public void Save() => _saved = Environment.GetEnvironmentVariable("VERTEX_TEST_MODEL");
    [TearDown] public void Restore() => Environment.SetEnvironmentVariable("VERTEX_TEST_MODEL", _saved);

    [Test]
    public void WithNothingSet_ItIsLlama31()
    {
        Environment.SetEnvironmentVariable("VERTEX_TEST_MODEL", null);
        Assert.That(TestModel.Pick(Cache), Is.EqualTo("llama3.1:8b"));
        Environment.SetEnvironmentVariable("VERTEX_TEST_MODEL", "  ");
        Assert.That(TestModel.Pick(Cache), Is.EqualTo("llama3.1:8b"), "blank is not set");
    }

    [Test]
    public void AnEnvironmentPrefix_PicksThatModel()
    {
        Environment.SetEnvironmentVariable("VERTEX_TEST_MODEL", "qwen3");
        Assert.That(TestModel.Pick(Cache), Is.EqualTo("qwen3.5:9b"));
        Environment.SetEnvironmentVariable("VERTEX_TEST_MODEL", "GEMMA4");
        Assert.That(TestModel.Pick(Cache), Is.EqualTo("gemma4:latest"), "a prefix, any case");
    }

    [Test]
    public void AModelThatIsNotThere_FailsSayingWhatIsAndHowToInstallIt()
    {
        Environment.SetEnvironmentVariable("VERTEX_TEST_MODEL", "mistral");
        var ex = Assert.Throws<AssertionException>(() => TestModel.Pick(Cache))!;
        Assert.That(ex.Message, Does.Contain("mistral").And.Contain("gemma4:latest").And.Contain("vertex install-models mistral"));
    }
}
