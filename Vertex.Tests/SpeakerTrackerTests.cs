using System.Text.Json;
using SpawnDev.AI;
using SpawnDev.AI.Server;
using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// Who is talking: the tracker's decisions - naming a voice, keeping a conversation going across a clip too short to identify,
/// and introductions - on REAL ReDimNet2 embeddings of 12 LibriSpeech speakers (Fixtures/speaker-embeddings.json, six clips
/// each, from onnxruntime). No model runs here; the vectors are the data. Each test pins one guard, so a red-check can
/// disable that guard and watch that test, and only that test, fail.
/// </summary>
public class SpeakerTrackerTests
{
    private sealed record Clip(string Speaker, string Id, float[] Embedding);

    private static List<Clip> Clips()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "speaker-embeddings.json")));
        return doc.RootElement.GetProperty("clips").EnumerateArray().Select(c => new Clip(
            c.GetProperty("speaker").GetString()!, c.GetProperty("id").GetString()!,
            c.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray())).ToList();
    }

    private sealed class Rig
    {
        public readonly SpeakerRegistry Registry = new();
        public readonly SpeakerTracker Tracker;
        public int Saves;
        public long Now = 1_000_000;
        public readonly List<Clip> Clips = SpeakerTrackerTests.Clips();
        public Rig() { Tracker = new SpeakerTracker(Registry, () => Saves++); }

        public List<Clip> Of(int speakerIndex) { var s = Clips.Select(c => c.Speaker).Distinct().ToList()[speakerIndex]; return Clips.Where(c => c.Speaker == s).ToList(); }

        /// <summary>What AiSpeakerEngine.ObserveAsync does with an embedding: a voice nobody matches becomes a guest, unless the clip is under 1.5 s.</summary>
        public AiSpeakerResult Hear(Clip clip, double seconds)
        {
            var reliable = seconds >= 2.0;
            if (seconds < 1.5) return new AiSpeakerResult(Registry.Identify(clip.Embedding), null, seconds, reliable, 0, clip.Embedding);
            var match = Registry.Observe(clip.Embedding, seconds, out var guest);
            return new AiSpeakerResult(match, guest, seconds, reliable, 0, clip.Embedding);
        }

        public SpeakerTracker.Who Say(Clip clip, double seconds, long advanceMs = 5000)
        {
            Now += advanceMs;
            return Tracker.Resolve(Hear(clip, seconds), Now);
        }
    }

    [Test]
    public void AnEnrolledPerson_IsNamed_WithAKeyThatStaysTheSame()
    {
        var r = new Rig();
        var ada = r.Of(0);
        foreach (var c in ada.Take(3)) r.Registry.Enroll("Ada", c.Embedding, 5);
        var first = r.Say(ada[3], 4);
        var second = r.Say(ada[4], 4);
        Assert.That(first, Is.EqualTo(new SpeakerTracker.Who("voice:Ada", "Ada", true)));
        Assert.That(second.Key, Is.EqualTo(first.Key), "what Vertex remembers about a person is keyed by it");
    }

    [Test]
    public void AClipTooShortToIdentify_IsAttributedToThePreviousSpeaker_OnlyWhileTheConversationLasts()
    {
        var r = new Rig();
        var ada = r.Of(0); var bo = r.Of(1); var noise = r.Of(7);
        foreach (var c in ada.Take(3)) r.Registry.Enroll("Ada", c.Embedding, 5);
        foreach (var c in bo.Take(3)) r.Registry.Enroll("Bo", c.Embedding, 5);
        r.Say(ada[3], 4);
        // "yes": 0.6 s. An embedding of a clip that short matches nobody (here: a voice nobody enrolled stands in for it),
        // but it is the same conversation.
        var shortReply = r.Say(noise[0], 0.6, advanceMs: 3000);
        Assert.That(shortReply.Name, Is.EqualTo("Ada"), "within the conversation window a short clip continues the last speaker");
        Assert.That(shortReply.Identified, Is.False, "and says it is a guess");

        var late = r.Say(noise[1], 0.6, advanceMs: (long)r.Tracker.Continuity.TotalMilliseconds + 1000);
        Assert.That(late.Key, Is.EqualTo("room"), "after the window nobody is assumed");
        Assert.That(late.Name, Is.EqualTo(r.Tracker.Unknown));
    }

    [Test]
    public void AVoiceNobodyKnows_BecomesAGuest_AndAnIntroductionNamesIt()
    {
        var r = new Rig();
        var nikki = r.Of(2);
        var first = r.Say(nikki[0], 4);
        Assert.That(first.Name, Is.EqualTo("Guest 1"), "a stranger is told apart from the room even before a name is known");

        var told = r.Tracker.BeginIntroduction("Nikki", forNextVoice: false, r.Now);
        Assert.That(told, Does.Contain("Nikki"));
        Assert.That(r.Registry.Profiles.Select(p => p.Name), Does.Contain("Nikki"));
        Assert.That(r.Registry.Profiles.Select(p => p.Name), Does.Not.Contain("Guest 1"), "the guest became the person");
        Assert.That(r.Saves, Is.GreaterThan(0), "the introduction was saved");

        var later = r.Say(nikki[1], 4);
        Assert.That(later, Is.EqualTo(new SpeakerTracker.Who("voice:Nikki", "Nikki", true)));
        // The learning window adds her next few utterances to her profile, then closes.
        r.Say(nikki[2], 4); r.Say(nikki[3], 4);
        var profile = r.Registry.Profiles.Single(p => p.Name == "Nikki");
        Assert.That(profile.Embeddings.Count, Is.GreaterThanOrEqualTo(4), "the introduction was a window of learning, not one clip");
    }

    [Test]
    public void AnIntroduction_NeverTakesOverAVoiceKnownUnderAnotherName()
    {
        var r = new Rig();
        var todd = r.Of(3);
        foreach (var c in todd.Take(3)) r.Registry.Enroll("Todd", c.Embedding, 5);
        r.Say(todd[3], 4);
        var before = r.Registry.Profiles.Single().Embeddings.Count;

        var told = r.Tracker.BeginIntroduction("Nikki", forNextVoice: false, r.Now);
        Assert.That(told, Does.Contain("already Todd"));
        r.Say(todd[4], 4);
        Assert.That(r.Registry.Profiles.Select(p => p.Name), Is.EqualTo(new[] { "Todd" }), "no Nikki was made from Todd's recording");
        Assert.That(r.Registry.Profiles.Single().Embeddings.Count, Is.EqualTo(before), "and Todd's voice was not changed");
        Assert.That(r.Tracker.IsLearning, Is.False);
    }

    [Test]
    public void AnIntroductionOfTheNextVoice_WaitsForSomeoneNobodyKnows()
    {
        var r = new Rig();
        var tj = r.Of(4); var aubs = r.Of(5);
        foreach (var c in tj.Take(3)) r.Registry.Enroll("TJ", c.Embedding, 5);
        r.Say(tj[3], 4);                                               // "Vertex, this is Aubs"
        r.Tracker.BeginIntroduction("Aubs", forNextVoice: true, r.Now);

        var tjAgain = r.Say(tj[4], 4);
        Assert.That(tjAgain.Name, Is.EqualTo("TJ"), "the speaker of the introduction is not the person introduced");
        Assert.That(r.Registry.Profiles.Select(p => p.Name), Does.Not.Contain("Aubs"));

        var hello = r.Say(aubs[0], 4);
        Assert.That(hello.Name, Is.EqualTo("Aubs"), "the first voice nobody knows is her");
        Assert.That(r.Say(aubs[1], 4).Name, Is.EqualTo("Aubs"));
    }

    [Test]
    public void WhileLearningOnePerson_AnotherVoiceIsNotAddedToThem()
    {
        var r = new Rig();
        var nikki = r.Of(2); var stranger = r.Of(6);
        r.Say(nikki[0], 4);
        r.Tracker.BeginIntroduction("Nikki", forNextVoice: false, r.Now);
        var intruder = r.Say(stranger[0], 4);
        Assert.That(intruder.Name, Is.Not.EqualTo("Nikki"), "somebody else speaking during the learning window is not Nikki");
        Assert.That(r.Registry.Profiles.Single(p => p.Name == "Nikki").Embeddings.Count, Is.LessThanOrEqualTo(2),
            "and their embedding did not go into Nikki's profile");
        Assert.That(r.Say(nikki[1], 4).Name, Is.EqualTo("Nikki"), "Nikki herself still is");
    }

    [Test]
    public void AnIntroductionThatIsNotFollowedUp_ExpiresAndLearnsNothingMore()
    {
        var r = new Rig();
        var nikki = r.Of(2);
        r.Say(nikki[0], 4);
        r.Tracker.BeginIntroduction("Nikki", forNextVoice: false, r.Now);
        var count = r.Registry.Profiles.Single(p => p.Name == "Nikki").Embeddings.Count;
        r.Say(nikki[1], 4, advanceMs: (long)r.Tracker.LearnWindow.TotalMilliseconds + 5000);
        Assert.That(r.Tracker.IsLearning, Is.False, "the window closed");
        Assert.That(r.Registry.Profiles.Single(p => p.Name == "Nikki").Embeddings.Count, Is.EqualTo(count), "nothing was learned after it closed");
    }
}
