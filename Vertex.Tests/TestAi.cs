using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// A <see cref="VertexAi"/> for a test, keeping its introduced voices in a temp file. The default is the real
/// %APPDATA%\Vertex\voices.json, and a test that speaks Kokoro voices into the microphone would file them there as guests
/// next to the family's real voiceprints.
/// </summary>
public static class TestAi
{
    public static VertexAi Create(Action<string>? log = null, string? voicesFile = null) =>
        new(log ?? (m => TestContext.Progress.WriteLine("[ai] " + m)),
            voicesFile ?? Path.Combine(Path.GetTempPath(), "vertex-tests", Guid.NewGuid().ToString("N"), "voices.json"));
}
