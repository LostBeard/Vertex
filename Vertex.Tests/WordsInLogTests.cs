using Vertex.AI;
using Vertex.Host;

namespace Vertex.Tests;

/// <summary>
/// The robot hears the whole room and the dashboard showing the log can be opened from any device on the network, so the words of speech nobody addressed to Vertex stay out of the log
/// unless asked for (live 2026-10-15: a household conversation about gift cards, word for word).
/// </summary>
public class WordsInLogTests
{
    [Test]
    public void TheWordsAreLeftOutByDefault_AndQuotedWhenAskedFor()
    {
        Assert.That(WordsInLog.Quote("my password is hunter2", logWords: false), Is.EqualTo("(words not logged)"));
        Assert.That(WordsInLog.Quote("my password is hunter2", logWords: false), Does.Not.Contain("hunter2"));
        Assert.That(WordsInLog.Quote("hello there", logWords: true), Is.EqualTo("\"hello there\""));
    }

    [Test]
    public void LoggingWordsIsOffInANewSettingsFileAndInOneSavedBeforeTheOptionExisted()
    {
        Assert.That(new VertexSettings().ReachyLogWords, Is.False);
        var folder = Path.Combine(Path.GetTempPath(), "vertex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{ \"Reachy\": true, \"ReachyFaces\": true }");     // a file from before the option
        Assert.That(VertexSettings.Load(folder).ReachyLogWords, Is.False, "the private choice is the default for everyone who never saw the option");
    }

    [Test]
    public void ChoosingToLogWordsIsKept()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vertex-tests", Guid.NewGuid().ToString("N"));
        var settings = VertexSettings.Load(folder);
        settings.ReachyLogWords = true;
        settings.Save();
        Assert.That(VertexSettings.Load(folder).ReachyLogWords, Is.True);
    }
}
