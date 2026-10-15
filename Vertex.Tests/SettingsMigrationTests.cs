using Vertex.Host;

namespace Vertex.Tests;

/// <summary>A changed default must reach people whose settings file already holds the old default (Vertex saves its settings on every start).</summary>
public class SettingsMigrationTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "vertex-tests", Guid.NewGuid().ToString("N"));

    [Test]
    public void ASettingThatWasOnlyTheOldDefaultIsResetOnce_AndAChoiceAfterwardsIsKept()
    {
        var folder = Folder();
        Directory.CreateDirectory(folder);
        // What a build before the migration wrote: no version, streaming on (it was the default then).
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{ \"Reachy\": true, \"ReachyStreamSpeech\": true, \"ReachyFaces\": false }");

        var settings = VertexSettings.Load(folder);
        Assert.That(settings.ReachyStreamSpeech, Is.False, "the experimental stream is off for someone who never chose it");
        Assert.That(settings.ReachyFaces, Is.False, "other settings are untouched");
        Assert.That(settings.SettingsVersion, Is.EqualTo(VertexSettings.CurrentVersion));
        settings.Save();

        // Now the person turns it on themselves: it must stay on across restarts.
        settings.ReachyStreamSpeech = true;
        settings.Save();
        var again = VertexSettings.Load(folder);
        Assert.That(again.ReachyStreamSpeech, Is.True, "a choice made after the migration is kept");
    }

    [Test]
    public void ANewFolderGetsTheDefaults_AtTheCurrentVersion()
    {
        var settings = VertexSettings.Load(Folder());
        Assert.That(settings.ReachyStreamSpeech, Is.False);
        Assert.That(settings.SettingsVersion, Is.EqualTo(VertexSettings.CurrentVersion));
    }
}
