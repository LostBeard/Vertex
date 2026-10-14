using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>
/// Hearing the name, and noticing a sentence that stopped partway. The lines are what the speech recogniser really wrote in the live session of
/// 2026-10-14 (heard folder), not invented ones: "Vertex" came back as "Rutax" and "Per text", and cut-off phrases came back with no full stop.
/// </summary>
public class WakeWordTests
{
    [TestCase("Vertex, what is the capital of France?")]
    [TestCase("Hi Vertex")]
    [TestCase("Vertex.")]
    [TestCase("Rutax, what's the")]                      // MEASURED: Parakeet wrote "Vertex" this way
    [TestCase("Per text.")]                              // MEASURED
    [TestCase("hi, birdtakes")]                          // MEASURED on Whisper
    [TestCase("Virtex, are you there?")]                 // not seen yet: the consonants are the same
    [TestCase("Burtex what is that")]
    [TestCase("Hey vertix")]
    public void TheNameIsHeard_HoweverItWasWritten(string said) => Assert.That(WakeWord.Names(said, "Vertex"), Is.True, said);

    [TestCase("What is the capital of France?")]
    [TestCase("Rent has gone up.")]
    [TestCase("I think I'm not the end. Shut up, you want to cry!")]
    [TestCase("Oh the yoke out. Yeah, look at that.")]
    [TestCase("Here's a flip play. Okay.")]
    [TestCase("There are many virtues in patience")]
    [TestCase("The vertical line goes up")]
    [TestCase("Customers, customers.")]
    [TestCase("Stay away! We're under renovations where we're gonna go.")]
    [TestCase("Is that some talking?")]
    [TestCase("Best thing to build first.")]
    [TestCase("Reflect on that")]
    public void OrdinaryTalk_IsNotTakenForTheName(string said) => Assert.That(WakeWord.Names(said, "Vertex"), Is.False, said);

    [TestCase("Rutax, what's the", "Vertex, what's the")]
    [TestCase("Per text.", "Vertex.")]
    [TestCase("Hey Burtex, hi", "Hey Vertex, hi")]
    [TestCase("Vertex, hi", "Vertex, hi")]
    [TestCase("hello there", "hello there")]
    public void AMisheardName_IsPutRight_SoTheModelNeverSeesAStranger(string heard, string normalised) =>
        Assert.That(WakeWord.Normalize(heard, "Vertex"), Is.EqualTo(normalised));

    [Test]
    public void AnotherCharactersName_IsMatchedTheSameWay()
    {
        Assert.That(WakeWord.Names("hey pixel what's up", "Pixel"), Is.True);
        Assert.That(WakeWord.Names("hey bolt", "Bolt"), Is.True);
        Assert.That(WakeWord.Names("hey vertex", "Pixel"), Is.False);
    }

    [TestCase("Rutax, what's the", true)]                // MEASURED: the cut-off question
    [TestCase("Oh my god look I got through I got through the window Yeah", true)]
    [TestCase("Six seven six seven LED LET ME LEAVE", true)]
    [TestCase("What is the capital of France?", false)]
    [TestCase("Best thing to build first.", false)]
    [TestCase("Vertex.", false)]
    [TestCase("Oh", false)]                              // one word: nothing to wait for
    [TestCase("Well, I think...", true)]
    [TestCase("Okay, big way.", false)]
    [TestCase("", false)]
    public void TheTranscriptThatStopsPartway_IsSpotted(string text, bool unfinished) => Assert.That(TurnText.LooksUnfinished(text), Is.EqualTo(unfinished), text);

    [TestCase("Vertex.", true)]
    [TestCase("Hey Vertex", true)]
    [TestCase("Hi, Vertex!", true)]
    [TestCase("Rutax.", true)]                                   // a misheard name is still only the name
    [TestCase("Vertex, what is the capital of France?", false)]
    [TestCase("Vertex, hi, I'm Todd.", false)]
    [TestCase("What is the capital of France?", false)]
    [TestCase("Vertex please", false)]
    [TestCase("", false)]
    public void JustTheName_IsNotAQuestionYet(string said, bool onlyTheName) => Assert.That(WakeWord.IsOnlyTheName(said, "Vertex"), Is.EqualTo(onlyTheName), said);

    [TestCase("We're gonna play Minecraft today.", true)]
    [TestCase("Vertex, we're gonna play Minecraft today.", true)]
    [TestCase("I built a house yesterday.", true)]
    [TestCase("Hey Vertex, my dog pooped on the floor.", true)]
    [TestCase("What's the best thing to build first?", false)]
    [TestCase("Vertex, what's the best thing to build first?", false)]
    [TestCase("Can you tell me what to build", false)]
    [TestCase("Hi Vertex, tell me a story.", false)]
    [TestCase("Come here.", false)]
    [TestCase("Rutax, what is the capital of France", false)]
    [TestCase("", false)]
    public void AStatement_ExpectsMore_AQuestionOrARequest_DoesNot(string said, bool expectsMore) => Assert.That(TurnText.ExpectsMore(said, "Vertex"), Is.EqualTo(expectsMore), said);
}
