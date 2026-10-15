using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// Long sentences are spoken in pieces. Live 2026-10-15: one sentence listing three biomes was a single 14 s clip; its 4.9 s of synthesis and 6 s of transfer could not finish while the
/// clip before it played, so the speaker sat silent for 11 s. The same words as two clips of about 7 s are rendered and sent while the one before plays.
/// </summary>
public class MouthSplitTests
{
    private const string Biomes = "Some popular ones include the Desert biome, where you can find sand dunes and oases; the Forest biome, which is home to trees and wildlife; and the Tundra biome, a cold region with snow-covered mountains.";

    private static string Rejoin(IEnumerable<string> pieces) => string.Join(" ", pieces);

    [Test]
    public void ALongSentenceIsCutAtItsCommas_IntoPiecesOfAboutEqualLength_AndNoWordIsLost()
    {
        Assert.That(Biomes.Length, Is.GreaterThan(Mouth.MaxClipChars * 1.5), "the fixture really is a long one");
        var pieces = Mouth.Sentences(Biomes).ToList();
        TestContext.Out.WriteLine(string.Join(" | ", pieces));
        Assert.That(pieces.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(pieces.All(p => p.Length <= Mouth.MaxClipChars), Is.True, "no clip is longer than the cap: " + string.Join(" | ", pieces.Select(p => p.Length)));
        Assert.That(Rejoin(pieces), Is.EqualTo(Biomes), "the same words, in order");
        Assert.That(pieces.Take(pieces.Count - 1).All(p => p.EndsWith(',') || p.EndsWith(';') || p.EndsWith(':')), Is.True, "cut where a speaker would pause: " + string.Join(" | ", pieces));
        Assert.That(pieces.Min(p => p.Length), Is.GreaterThan(Mouth.MaxClipChars / 4), "no tiny stub of a clip");
    }

    [Test]
    public void WithoutPunctuationItCutsBeforeAJoiningWord_OrAtASpace_NeverInsideAWord()
    {
        var joined = "I would build a huge castle on top of the tallest mountain I can find and then dig a deep tunnel underneath it so that everybody can walk safely from the village to the castle gates";
        var pieces = Mouth.Sentences(joined).ToList();
        Assert.That(pieces.All(p => p.Length <= Mouth.MaxClipChars), Is.True);
        Assert.That(Rejoin(pieces), Is.EqualTo(joined));
        Assert.That(pieces.Skip(1).Any(p => p.StartsWith("and ") || p.StartsWith("so ")), Is.True, "cut before a joining word: " + string.Join(" | ", pieces));
        Assert.That(pieces.Take(pieces.Count - 1).Any(p => p.EndsWith(" and") || p.EndsWith(" then") || p.EndsWith(" so") || p.EndsWith(" the")), Is.False, "no clip ends on a word that wants the next one: " + string.Join(" | ", pieces));

        var plain = string.Join(" ", Enumerable.Repeat("blocks", 60));       // nothing to cut at but spaces
        var cut = Mouth.Sentences(plain).ToList();
        Assert.That(cut.All(p => p.Length <= Mouth.MaxClipChars && p.Split(' ').All(w => w == "blocks")), Is.True, "words stay whole");
        Assert.That(Rejoin(cut), Is.EqualTo(plain));
    }

    [Test]
    public void ShortSentencesAndTheCapItselfAreLeftAlone_AndVeryLongOnesAreCutRepeatedly()
    {
        var atCap = new string('a', Mouth.MaxClipChars - 1) + ".";
        Assert.That(Mouth.Sentences(atCap), Is.EqualTo(new[] { atCap }));
        Assert.That(Mouth.Sentences("Hi Aubri! Want to build a castle?"), Is.EqualTo(new[] { "Hi Aubri!", "Want to build a castle?" }));

        var huge = string.Join(", ", Enumerable.Range(1, 40).Select(i => $"item number {i}")) + ".";
        var pieces = Mouth.Sentences(huge).ToList();
        Assert.That(pieces.All(p => p.Length <= Mouth.MaxClipChars), Is.True);
        Assert.That(pieces.Count, Is.GreaterThanOrEqualTo(huge.Length / Mouth.MaxClipChars));
        Assert.That(Rejoin(pieces), Is.EqualTo(huge));
    }
}
