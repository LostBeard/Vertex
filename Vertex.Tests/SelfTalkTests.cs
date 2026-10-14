using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>
/// The model writing the friend's next line. The first case is the exact text from the live session of 2026-10-14: after "what's the best thing to build
/// first?" the robot's reply ended with a line nobody said, in the format the prompt uses for the friend.
/// </summary>
public class SelfTalkTests
{
    [Test]
    public void AFriendsLineTheModelMadeUp_IsCutOffTheReply()
    {
        const string live = "Todd says (out loud): Yeah, I'm not really sure... Can you build a house that's like, a castle?";
        Assert.That(SelfTalk.CutFriendsLine(live), Is.EqualTo(""), "a reply that IS the made-up line is nothing at all");
        Assert.That(SelfTalk.CutFriendsLine("A crafting table is a good start. " + live), Is.EqualTo("A crafting table is a good start."));
        Assert.That(SelfTalk.CutFriendsLine("Sounds fun!\nAubri says (in chat): can we go now"), Is.EqualTo("Sounds fun!"));
        Assert.That(SelfTalk.CutFriendsLine("Okay. Someone says (out loud, through the robot): hi"), Is.EqualTo("Okay."));
    }

    [TestCase("The capital of France is Paris!")]
    [TestCase("*tilts head* I think a crafting table is best, Todd.")]
    [TestCase("People say that diamonds are deep underground.")]
    [TestCase("Steve says hello to everyone, I heard.")]          // no parenthesis and colon: not the prompt's format
    [TestCase("")]
    public void OrdinaryReplies_AreLeftAlone(string reply)
    {
        Assert.That(SelfTalk.IndexOfFriendsLine(reply), Is.Null);
        Assert.That(SelfTalk.CutFriendsLine(reply), Is.EqualTo(reply));
    }

    [Test]
    public void AStreamedReply_IsCutAtTheSamePlaceWhateverTheChunking()
    {
        const string full = "Let's find some trees first. Todd says (out loud): and then a castle?";
        for (var chunk = 1; chunk <= 12; chunk++)
        {
            var seen = "";
            int? at = null;
            for (var i = 0; i < full.Length && at == null; i += chunk)
            {
                seen = full[..Math.Min(full.Length, i + chunk)];
                at = SelfTalk.IndexOfFriendsLine(seen);
            }
            Assert.That(at, Is.Not.Null, $"chunk {chunk}: never noticed");
            Assert.That(seen[..at!.Value].TrimEnd(), Is.EqualTo("Let's find some trees first."), $"chunk {chunk}");
        }
    }
}
