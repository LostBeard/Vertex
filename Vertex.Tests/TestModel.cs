namespace Vertex.Tests;

/// <summary>
/// Which language model a test runs: VERTEX_TEST_MODEL (a prefix of an Ollama-cache model name) or llama3.1, the model the tests were calibrated on. One place, so comparing another
/// model (TJ is open to trying them) is one environment variable for every suite, not a code edit in seven fixtures.
/// </summary>
internal static class TestModel
{
    public const string Default = "llama3.1";

    /// <summary>The first model in <paramref name="models"/> whose name starts with VERTEX_TEST_MODEL (default llama3.1); fails the test saying what the cache holds when there is none.</summary>
    public static string Pick(IReadOnlyList<string> models)
    {
        var want = Environment.GetEnvironmentVariable("VERTEX_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(want)) want = Default;
        return models.FirstOrDefault(m => m.StartsWith(want, StringComparison.OrdinalIgnoreCase))
            ?? throw new AssertionException($"the model '{want}' (VERTEX_TEST_MODEL, default {Default}) is not in the Ollama cache: {string.Join(", ", models)}. Install it with `vertex install-models {want}`.");
    }
}
