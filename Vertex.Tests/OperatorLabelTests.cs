using Vertex.Host;

namespace Vertex.Tests;

/// <summary>
/// What is typed on the dashboard (a PC, or a child's tablet) is said to Vertex under a name. It was a hard-coded person's name, so Aubs's tablet got "Of course, TJ!"
/// (seen 2026-11-01 on her PC, the first time the installed app answered).
/// </summary>
public class OperatorLabelTests
{
    [Test]
    public void TheTypingNameWins_ThenTheRobotsUsualSpeaker_ThenSomeone_NeverAnInventedName()
    {
        Assert.That(VertexHost.OperatorLabel(new VertexSettings { OperatorName = "  Aubri ", ReachySpeaker = "Nikki" }), Is.EqualTo("Aubri"), "the typist's own name, trimmed");
        Assert.That(VertexHost.OperatorLabel(new VertexSettings { OperatorName = "", ReachySpeaker = "Nikki" }), Is.EqualTo("Nikki"), "no typing name: the person who usually talks to the robot");
        Assert.That(VertexHost.OperatorLabel(new VertexSettings()), Is.EqualTo("Someone"), "nothing set: nobody's name is invented");
        Assert.That(VertexHost.OperatorLabel(new VertexSettings { OperatorName = "   ", ReachySpeaker = "  " }), Is.EqualTo("Someone"), "blanks are blank");
    }

    [Test]
    public void TheTypingName_IsKept_WhenSettingsAreSavedAndLoadedAgain()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vertex-oplabel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = VertexSettings.Load(folder);
            Assert.That(first.OperatorName, Is.Empty, "a new install starts with no name");
            first.OperatorName = "Aubri";
            first.Save();
            Assert.That(VertexSettings.Load(folder).OperatorName, Is.EqualTo("Aubri"));
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
