using System.Text.Json;
using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>
/// Characters are saved to disk the first time Vertex runs, so a better built-in persona never reached anyone who had run it (live 2026-10-15: "it does not like responding with more than
/// 2 sentences", and the saved Vertex persona still said it explains things "in a few simple words"). An UNEDITED saved copy is brought up to date; someone's own text is never touched.
/// </summary>
public class CharacterMigrationTests
{
    private const string OldVertex = "You are Vertex, a friendly, curious and clever Minecraft companion. You love building, exploring caves and finding diamonds, and you are always happy to help your friends. You are encouraging and patient, and you explain what you are doing in a few simple words.";

    private static string Folder() => Path.Combine(Path.GetTempPath(), "vertex-tests", Guid.NewGuid().ToString("N"), "characters");

    private static void Write(string folder, Character c)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, c.Name + ".json"), JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public void AnUneditedOldPersonaIsBroughtUp_KeepingTheVoiceAndStyleThatWereChosen()
    {
        var folder = Folder();
        Write(folder, new Character { Name = "Vertex", Persona = OldVertex, Voice = "kokoro:am_michael", Style = new() { Chattiness = 0.9 } });

        var store = new CharacterStore(folder);
        var vertex = store.Find("Vertex")!;
        Assert.That(vertex.Persona, Is.EqualTo(Character.BuiltIn.First(c => c.Name == "Vertex").Persona));
        Assert.That(vertex.Persona, Does.Not.Contain("in a few simple words"));
        Assert.That(vertex.Voice, Is.EqualTo("kokoro:am_michael"), "the voice that was chosen stays");
        Assert.That(vertex.Style.Chattiness, Is.EqualTo(0.9), "the style that was chosen stays");
    }

    [Test]
    public void APersonaSomeoneEditedIsLeftAlone()
    {
        var folder = Folder();
        Write(folder, new Character { Name = "Vertex", Persona = OldVertex + " You always say ahoy.", Voice = "kokoro:af_heart" });
        Assert.That(new CharacterStore(folder).Find("Vertex")!.Persona, Is.EqualTo(OldVertex + " You always say ahoy."));
    }

    [Test]
    public void ANewFolderIsSeededWithTheCurrentPersonas()
    {
        var store = new CharacterStore(Folder());
        Assert.That(store.Find("Vertex")!.Persona, Is.EqualTo(Character.BuiltIn.First(c => c.Name == "Vertex").Persona));
        Assert.That(store.All().Select(c => c.Name), Is.SupersetOf(Character.BuiltIn.Select(c => c.Name)));
    }
}
