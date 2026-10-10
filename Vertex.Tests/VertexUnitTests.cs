using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Data;
using Vertex.AI;
using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>Vertex's own pieces that need no server or GPU.</summary>
public class VertexUnitTests
{
    private static double Power(float[] pcm, double hz, int rate)
    {
        var k = 2 * Math.Cos(2 * Math.PI * hz / rate);
        double s1 = 0, s2 = 0;
        foreach (var x in pcm)
        {
            var s0 = x + k * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - k * s1 * s2;
    }

    [Test]
    public void Downsample_KeepsSpeechBand_AndRemovesWhatWouldAlias()
    {
        short[] Tone(double hz) => Enumerable.Range(0, 48000).Select(i => (short)(Math.Sin(2 * Math.PI * hz * i / 48000) * 16000)).ToArray();
        var speech = Audio.Downsample48To16(Tone(440));
        Assert.That(speech.Length, Is.EqualTo(16000));
        var rms = Math.Sqrt(speech.Skip(100).Take(15800).Average(x => x * (double)x));
        Assert.That(rms, Is.EqualTo(16000 / 32768.0 / Math.Sqrt(2)).Within(0.01), "440 Hz passes at full level");

        // 10 kHz is above the 8 kHz Nyquist of 16 kHz: unfiltered it would fold down to 6 kHz.
        var high = Audio.Downsample48To16(Tone(10000));
        Assert.That(Power(high, 6000, 16000), Is.LessThan(Power(speech, 440, 16000) * 1e-3), "10 kHz does not alias to 6 kHz");
    }

    [Test]
    public void Upsample_DoublesTheRate()
    {
        var input = Enumerable.Range(0, 2400).Select(i => (float)Math.Sin(2 * Math.PI * 300 * i / 24000) * 0.5f).ToArray();
        var output = Audio.Upsample24To48(input);
        Assert.That(output.Length, Is.EqualTo(4800));
        Assert.That(output[200], Is.EqualTo((short)(input[100] * 32767)));
        Assert.That(output.Max(), Is.InRange((short)16000, (short)16400));
    }

    [Test]
    public void Mouth_SplitsSentencesAndChatLines()
    {
        Assert.That(Mouth.Sentences("Hi Aubri! Want to build a castle? I have 3.5 stacks of stone."),
            Is.EqualTo(new[] { "Hi Aubri!", "Want to build a castle?", "I have 3.5 stacks of stone." }));
        Assert.That(Mouth.Sentences("no punctuation here"), Is.EqualTo(new[] { "no punctuation here" }));
        var chunks = Mouth.ChatChunks(new string('a', 100) + " " + new string('b', 200), 250).ToList();
        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.That(chunks.All(c => c.Length <= 250));
    }

    [Test]
    public void Skills_ResolveWhatPlayersCallBlocks()
    {
        var skills = new Skills(new MinecraftClient(GameProfile.Offline("t")));
        var d = GameData.Vanilla;
        Assert.That(skills.ResolveBlocks("wood"), Does.Contain(d.Block("oak_log")!.Id).And.Contain(d.Block("spruce_log")!.Id));
        Assert.That(skills.ResolveBlocks("diamonds"), Does.Contain(d.Block("diamond_ore")!.Id).And.Contain(d.Block("deepslate_diamond_ore")!.Id));
        Assert.That(skills.ResolveBlocks("stone"), Does.Contain(d.Block("stone")!.Id));
        Assert.That(skills.ResolveBlocks("dirt"), Does.Contain(d.Block("dirt")!.Id));
        Assert.That(skills.ResolveBlocks("oak logs"), Does.Contain(d.Block("oak_log")!.Id));
        Assert.That(skills.ResolveBlocks("unobtainium"), Is.Empty);
    }

    [Test]
    public void Characters_AreSeededAndEditable()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vertex-unit-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new CharacterStore(folder);
            Assert.That(store.All().Select(c => c.Name), Is.SupersetOf(new[] { "Vertex", "Pixel", "Captain", "Bolt" }));
            var pixel = store.Find("pixel")!;
            pixel.Style.Bravery = 0.1;
            store.Save(pixel);
            Assert.That(new CharacterStore(folder).Find("Pixel")!.Style.Bravery, Is.EqualTo(0.1));
            Assert.That(VertexAi.Voices, Does.Contain(store.Find("Captain")!.Voice), "every built-in voice exists");
            foreach (var c in Character.BuiltIn) Assert.That(VertexAi.Voices, Does.Contain(c.Voice), c.Name);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
