using SpawnDev.AI;
using SpawnDev.AI.Server;
using SpawnDev.ILGPU.ML.Pipelines;
using Vertex.People;

namespace Vertex.Tests;

/// <summary>
/// Faces and people, with the registry as the real one and the embeddings synthetic (a person is a random unit vector plus a little noise; two people are two vectors):
/// who is in view, who has walked in after a long absence, faces learned from an introduction, and the people list that voices and faces share.
/// </summary>
public class PeopleTests
{
    private const int Dim = 128;
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static float[] Person(int seed, int variation = 0)
    {
        var rnd = new Random(seed);
        var v = new float[Dim];
        for (var i = 0; i < Dim; i++) v[i] = (float)(rnd.NextDouble() * 2 - 1);
        if (variation != 0)
        {
            var noise = new Random(seed * 1000 + variation);
            for (var i = 0; i < Dim; i++) v[i] += (float)(noise.NextDouble() * 2 - 1) * 0.25f;
        }
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        for (var i = 0; i < Dim; i++) v[i] /= norm;
        return v;
    }

    private static SpeakerRegistry NewFaces() => AiFaceEngine.NewRegistry();

    /// <summary>What the engine does for one face: the registry files it (a name, or a guest), and the result carries both.</summary>
    private static AiFaceResult Look(SpeakerRegistry faces, float[] embedding, float width = 120)
    {
        var match = faces.Observe(embedding, 1, out var guest);
        return new AiFaceResult(new FaceDetection(100, 80, width, width * 1.3f, 0.95f, new float[10]), match, guest, embedding);
    }

    private static IReadOnlyDictionary<string, long> Seen(SpeakerRegistry faces) => faces.Profiles.Where(p => !p.IsGuest).ToDictionary(p => p.Name, p => p.LastHeardTicks);

    private static void Enrolled(SpeakerRegistry faces, string name, int seed, DateTime lastSeen)
    {
        for (var v = 0; v < 3; v++) faces.Enroll(name, Person(seed, v), 1);
        faces.Profiles.First(p => p.Name == name).LastHeardTicks = lastSeen.Ticks;
    }

    private static void Run(FaceWatcher watcher, SpeakerRegistry faces, int seed, int looks, DateTime now, long tick, int variation = 1, long stepMs = 1000)
    {
        for (var i = 0; i < looks; i++)
        {
            var before = Seen(faces);
            var result = Look(faces, Person(seed, variation + i));
            watcher.Observe([result], before, tick + i * stepMs, now.AddMilliseconds(i * stepMs));
        }
    }

    private static (FaceWatcher Watcher, List<Arrival> Arrivals) Watch(SpeakerRegistry faces)
    {
        var watcher = new FaceWatcher(faces);
        var arrivals = new List<Arrival>();
        watcher.Arrived += arrivals.Add;
        return (watcher, arrivals);
    }

    [Test]
    public void APersonWhoReturnsAfterHoursIsGreeted_OnTheSecondLook()
    {
        var faces = NewFaces();
        Enrolled(faces, "Nikki", 11, Now.AddHours(-3));
        var watcher = new FaceWatcher(faces);
        var arrivals = new List<Arrival>();
        watcher.Arrived += arrivals.Add;

        var before = Seen(faces);
        watcher.Observe([Look(faces, Person(11, 1))], before, 1000, Now);
        Assert.That(arrivals, Is.Empty, "one look is not enough: a single misread frame must not say hi to the wrong person");
        watcher.Observe([Look(faces, Person(11, 2))], Seen(faces), 2000, Now.AddSeconds(1));   // the registry already says "seen a moment ago"
        Assert.That(arrivals, Has.Count.EqualTo(1));
        Assert.That(arrivals[0].Name, Is.EqualTo("Nikki"));
        Assert.That(arrivals[0].Absence.TotalHours, Is.EqualTo(3).Within(0.01), "the absence is measured at the FIRST look of the run");
    }

    [Test]
    public void SomeoneSeenTenMinutesAgoIsNotGreeted()
    {
        var faces = NewFaces();
        Enrolled(faces, "Nikki", 11, Now.AddMinutes(-10));
        var (watcher, arrivals) = Watch(faces);
        Run(watcher, faces, 11, looks: 4, Now, 1000);
        Assert.That(arrivals, Is.Empty);
    }

    [Test]
    public void AStrangerNobodyIntroducedIsNeverGreeted()
    {
        var faces = NewFaces();
        var (watcher, arrivals) = Watch(faces);
        Run(watcher, faces, 99, looks: 5, Now, 1000);
        Assert.That(arrivals, Is.Empty);
        Assert.That(faces.Profiles.Count(p => p.IsGuest), Is.EqualTo(1), "the stranger is filed as ONE guest, not one per look");
    }

    [Test]
    public void ThePersonIsGreetedOnceAVisit_AndAgainOnlyAfterTheCooldown()
    {
        var faces = NewFaces();
        Enrolled(faces, "Nikki", 11, Now.AddHours(-5));
        var (watcher, arrivals) = Watch(faces);
        Run(watcher, faces, 11, looks: 6, Now, 1000);
        Assert.That(arrivals, Has.Count.EqualTo(1), "six looks of one visit: one greeting");

        // She leaves, and 30 minutes later is seen again (pretend the registry says she had been away 3 hours): inside the cooldown, so no second greeting.
        watcher.Observe([], Seen(faces), 10_000, Now.AddSeconds(10));
        faces.Profiles.First(p => p.Name == "Nikki").LastHeardTicks = Now.AddHours(-3).Ticks;
        Run(watcher, faces, 11, looks: 3, Now.AddMinutes(30), 30 * 60_000L);
        Assert.That(arrivals, Has.Count.EqualTo(1), "inside the cooldown there is no second greeting");

        // Three hours on, away again: greeted again.
        watcher.Observe([], Seen(faces), 3 * 3_600_000L - 5000, Now.AddHours(3).AddSeconds(-5));
        faces.Profiles.First(p => p.Name == "Nikki").LastHeardTicks = Now.AddHours(1).Ticks;
        Run(watcher, faces, 11, looks: 3, Now.AddHours(3), 3 * 3_600_000L);
        Assert.That(arrivals, Has.Count.EqualTo(2), "after the cooldown she is greeted again");
    }

    [Test]
    public void OneMisreadLook_BetweenTwoOthers_DoesNotGreet()
    {
        var faces = NewFaces();
        Enrolled(faces, "Nikki", 11, Now.AddHours(-4));
        Enrolled(faces, "Todd", 12, Now.AddMinutes(-1));
        var watcher = new FaceWatcher(faces);
        var arrivals = new List<Arrival>();
        watcher.Arrived += arrivals.Add;
        // Todd is in view; one frame reads as Nikki, then Todd again.
        watcher.Observe([Look(faces, Person(12, 1))], Seen(faces), 1000, Now);
        watcher.Observe([Look(faces, Person(11, 1))], Seen(faces), 2000, Now.AddSeconds(1));
        watcher.Observe([Look(faces, Person(12, 2))], Seen(faces), 3000, Now.AddSeconds(2));
        Assert.That(arrivals, Is.Empty);
    }

    [Test]
    public void AnIntroductionTeachesTheFaceInFront_AndTheGuestBecomesThePerson()
    {
        var faces = NewFaces();
        var changes = 0;
        var watcher = new FaceWatcher(faces, () => changes++);
        // A stranger is in view: filed as a guest.
        watcher.Observe([Look(faces, Person(21, 1))], Seen(faces), 1000, Now);
        Assert.That(faces.Profiles.Single().IsGuest, Is.True);

        watcher.BeginIntroduction("Nikki", forNewFace: false, nowTick: 1500);
        for (var i = 0; i < 6; i++) watcher.Observe([Look(faces, Person(21, 2 + i))], Seen(faces), 2000 + i * 2000L, Now.AddSeconds(i * 2));
        var profile = faces.Profiles.Single();
        Assert.That(profile.Name, Is.EqualTo("Nikki"));
        Assert.That(profile.IsGuest, Is.False, "an introduced person is kept, not a guest");
        Assert.That(profile.Embeddings.Count, Is.GreaterThanOrEqualTo(4));
        Assert.That(changes, Is.GreaterThan(0), "the host is told to save");
        Assert.That(watcher.IsLearning, Is.False, "the introduction closes after enough looks");
    }

    [Test]
    public void AnIntroductionNeverTakesOverAFaceKnownUnderAnotherName()
    {
        var faces = NewFaces();
        Enrolled(faces, "Todd", 12, Now.AddMinutes(-1));
        var watcher = new FaceWatcher(faces);
        watcher.BeginIntroduction("Nikki", forNewFace: false, nowTick: 0);
        for (var i = 0; i < 4; i++) watcher.Observe([Look(faces, Person(12, 5 + i))], Seen(faces), 1000 + i * 2000L, Now.AddSeconds(i * 2));
        Assert.That(faces.Profiles.Select(p => p.Name), Is.EqualTo(new[] { "Todd" }), "Todd's face stays Todd's");
    }

    [Test]
    public void AfterTheFirstLook_AnotherPersonWalkingIntoTheFrameIsNotMixedIn()
    {
        var faces = NewFaces();
        var watcher = new FaceWatcher(faces);
        watcher.BeginIntroduction("Nikki", forNewFace: false, nowTick: 0);
        watcher.Observe([Look(faces, Person(21, 1))], Seen(faces), 1000, Now);                                  // Nikki's face, learned
        watcher.Observe([Look(faces, Person(22, 1))], Seen(faces), 3000, Now.AddSeconds(2));                    // somebody else, alone in view
        var nikki = faces.Profiles.Single(p => p.Name == "Nikki");
        Assert.That(nikki.Embeddings.Count, Is.EqualTo(1), "the stranger's face was not added to Nikki");
        Assert.That(faces.Profiles.Any(p => p.IsGuest), Is.True, "they are a guest instead");
    }

    [Test]
    public void AnIntroductionOfSomeoneNotThereYetWaitsForTheNextNewFace()
    {
        var faces = NewFaces();
        Enrolled(faces, "Todd", 12, Now.AddMinutes(-1));
        var watcher = new FaceWatcher(faces);
        watcher.BeginIntroduction("Aubri", forNewFace: true, nowTick: 0);
        // Todd (known) is in view: not Aubri. Then a new face appears next to him: that is Aubri.
        watcher.Observe([Look(faces, Person(12, 1))], Seen(faces), 1000, Now);
        Assert.That(faces.Profiles.Select(p => p.Name), Is.EqualTo(new[] { "Todd" }));
        watcher.Observe([Look(faces, Person(12, 2)), Look(faces, Person(31, 1))], Seen(faces), 3000, Now.AddSeconds(2));
        Assert.That(faces.Profiles.Select(p => p.Name).OrderBy(n => n), Is.EqualTo(new[] { "Aubri", "Todd" }));
    }

    [Test]
    public void ANamedPersonOutranksANearDuplicateGuest_SoTheyAreNotCalledAVisitor()
    {
        // Seen live: the same face was filed as a guest, later enrolled under a name, and the two profiles then scored almost alike - "not sure" - so the person stayed a visitor.
        var faces = NewFaces();
        faces.Observe(Person(41, 1), 1, out var guest);                 // a stranger: filed as a visitor
        faces.Enroll("Todd", Person(41, 2), 1);                         // the same person, enrolled from another look
        var look = Person(41, 3);
        // The precondition that makes this the live case: guest and named score within the margin of each other, so the OLD rule (best vs runner-up over everyone) said "not sure".
        var scores = faces.Profiles.Select(p => SpeakerRegistry.Cosine(look, p.Centroid!)).OrderByDescending(x => x).ToArray();
        Assert.That(scores[0] - scores[1], Is.LessThan(faces.MinMargin), "the guest and the named profile are too close for the margin rule");
        Assert.That(scores[0], Is.GreaterThan(faces.AcceptThreshold));
        var match = faces.Identify(look);
        Assert.That(match.Known, Is.True, "the named profile wins over the guest that looks the same");
        Assert.That(match.Name, Is.EqualTo("Todd"));
        Assert.That(guest!.IsGuest, Is.True);
    }

    [Test]
    public void ADuplicateGuestIsMergedIntoTheNamedPerson_AnUnrelatedGuestIsNot()
    {
        var faces = NewFaces();
        faces.Observe(Person(41, 1), 1, out var duplicate);
        faces.Enroll("Todd", Person(41, 2), 1);
        faces.Observe(Person(77, 1), 1, out var stranger);
        var merged = faces.AbsorbDuplicateGuests();
        Assert.That(merged, Is.EqualTo(new[] { duplicate!.Name }));
        Assert.That(faces.Profiles.Select(p => p.Name).OrderBy(n => n), Is.EqualTo(new[] { "Todd", stranger!.Name }.OrderBy(n => n)));
        Assert.That(faces.Profiles.Single(p => p.Name == "Todd").Embeddings.Count, Is.EqualTo(2), "the guest's look joined Todd's face");
    }

    [Test]
    public void AConfidentNewLookRefinesTheNamedFace_ACopyOfWhatIsHeldDoesNot()
    {
        var faces = NewFaces();
        Enrolled(faces, "Todd", 51, Now.AddMinutes(-1));
        var watcher = new FaceWatcher(faces);
        var before = faces.Profiles.Single().Embeddings.Count;
        // The very same embedding as one already held: nothing new to learn.
        watcher.Observe([Look(faces, Person(51, 1))], Seen(faces), 1000, Now);
        Assert.That(faces.Profiles.Single().Embeddings.Count, Is.EqualTo(before), "an exact copy is not added");
        // The same person from a noticeably different angle (a larger variation of the same face): worth keeping.
        var different = Person(51);
        var rnd = new Random(5);
        for (var i = 0; i < Dim; i++) different[i] += (float)(rnd.NextDouble() * 2 - 1) * 0.12f;
        var norm = MathF.Sqrt(different.Sum(x => x * x)); for (var i = 0; i < Dim; i++) different[i] /= norm;
        var result = Look(faces, different);
        Assert.That(result.Match!.Known, Is.True);
        watcher.Observe([result], Seen(faces), 5000, Now.AddSeconds(4));
        Assert.That(faces.Profiles.Single().Embeddings.Count, Is.GreaterThan(before), "a confident look from another angle joins the face");
    }

    // ── the people list ──

    private static (SpeakerRegistry Voices, SpeakerRegistry Faces, PeopleDirectory People, List<string> Saves) Directory()
    {
        var voices = new SpeakerRegistry();
        var faces = NewFaces();
        var saves = new List<string>();
        return (voices, faces, new PeopleDirectory(voices, faces, () => saves.Add("voices"), () => saves.Add("faces"), TimeSpan.FromHours(24)), saves);
    }

    [Test]
    public void ThePeopleListMergesVoiceAndFaceByName_AndShowsWhenAGuestWillBeForgotten()
    {
        var (voices, faces, people, _) = Directory();
        voices.Enroll("Nikki", Person(1), 5);
        faces.Enroll("Nikki", Person(2), 1);
        voices.Enroll("Todd", Person(3), 5);                    // voice only
        faces.Observe(Person(4), 1, out var guest);             // a stranger's face
        var list = people.List();
        Assert.That(list.Select(p => p.Name), Is.EqualTo(new[] { "Nikki", "Todd", guest!.Name }));
        var nikki = list.Single(p => p.Name == "Nikki");
        Assert.That((nikki.HasVoice, nikki.HasFace, nikki.IsGuest), Is.EqualTo((true, true, false)));
        Assert.That(nikki.ForgottenAtUtc, Is.Null, "an introduced person is kept until removed");
        var todd = list.Single(p => p.Name == "Todd");
        Assert.That((todd.HasVoice, todd.HasFace), Is.EqualTo((true, false)));
        var stranger = list.Single(p => p.IsGuest);
        Assert.That(stranger.HasFace && !stranger.HasVoice, Is.True);
        Assert.That(stranger.ForgottenAtUtc!.Value, Is.EqualTo(stranger.FirstSeenUtc!.Value.AddHours(24)).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void ForgettingAPersonRemovesTheirVoiceAndTheirFace_AndSavesBoth()
    {
        var (voices, faces, people, saves) = Directory();
        voices.Enroll("Nikki", Person(1), 5);
        faces.Enroll("Nikki", Person(2), 1);
        voices.Enroll("Todd", Person(3), 5);
        Assert.That(people.Forget("nikki"), Is.True, "names are not case sensitive");
        Assert.That(people.List().Select(p => p.Name), Is.EqualTo(new[] { "Todd" }));
        Assert.That(saves, Is.EquivalentTo(new[] { "voices", "faces" }));
        Assert.That(people.Forget("Nobody"), Is.False);
    }

    [Test]
    public void ForgettingGuestsKeepsTheIntroduced_AndForgettingAllKeepsNobody()
    {
        var (voices, faces, people, _) = Directory();
        voices.Enroll("Nikki", Person(1), 5);
        faces.Observe(Person(4), 1, out _);
        voices.Observe(Person(5), 5, out _);
        Assert.That(people.List().Count, Is.EqualTo(3));
        Assert.That(people.ForgetGuests(), Is.EqualTo(2));
        Assert.That(people.List().Select(p => p.Name), Is.EqualTo(new[] { "Nikki" }));
        Assert.That(people.ForgetAll(), Is.EqualTo(1));
        Assert.That(people.List(), Is.Empty);
    }

    [Test]
    public void GuestsExpireAfterTwentyFourHours_InBothRegistries_ButNotTheIntroduced()
    {
        var (voices, faces, people, saves) = Directory();
        voices.Enroll("Nikki", Person(1), 5);
        faces.Enroll("Nikki", Person(2), 1);
        voices.Observe(Person(5), 5, out var voiceGuest);
        faces.Observe(Person(4), 1, out var faceGuest);
        Assert.That(people.ExpireGuests(DateTime.UtcNow.AddHours(23)), Is.Empty);
        var gone = people.ExpireGuests(DateTime.UtcNow.AddHours(25));
        Assert.That(gone, Is.EquivalentTo(new[] { voiceGuest!.Name, faceGuest!.Name }.Distinct()));
        Assert.That(people.List().Select(p => p.Name), Is.EqualTo(new[] { "Nikki" }));
        Assert.That(saves, Is.EquivalentTo(new[] { "voices", "faces" }));
    }
}
