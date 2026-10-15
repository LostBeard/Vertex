using SpawnDev.AI;
using SpawnDev.AI.Server;

namespace Vertex.People;

/// <summary>Someone who walked in (or came back) after long enough away to be worth a "hi".</summary>
/// <param name="Name">Who, as the face registry knows them (an introduced person, never a guest).</param>
/// <param name="Absence">How long it had been since they were last seen.</param>
public sealed record Arrival(string Name, TimeSpan Absence);

/// <summary>A person in view right now.</summary>
/// <param name="Name">Their name, or the guest name ("Guest 2") when nobody has introduced them; null for a face too small to read.</param>
/// <param name="IsGuest">True for a stranger nobody named.</param>
/// <param name="Left">Box left edge in the picture (pixels), so a caller can tell who is on which side.</param>
/// <param name="Width">Box width.</param>
/// <param name="FrameWidth">Width of the picture the box is in (pixels), so the box's place can be turned into an angle.</param>
public sealed record VisiblePerson(string? Name, bool IsGuest, float Left, float Width, long LastSeenTick, int FrameWidth = 0);

/// <summary>
/// What the camera shows, turned into events a companion can use: who is in view, who has just walked in after a long absence (so the robot can say hi), and
/// faces being LEARNED when someone is introduced ("this is Nikki": the face in front of the robot becomes Nikki's).
/// </summary>
/// <remarks>
/// <para>
/// A face is only a label made from how a face looks: it is wrong sometimes, and a robot that greets the wrong person by name is worse than one that says nothing. So a greeting
/// needs the same named person in <see cref="SightingsToGreet"/> consecutive looks, never fires for a guest, and never fires twice within <see cref="GreetCooldown"/>.
/// </para>
/// <para>
/// <b>Learning mirrors the voice.</b> An introduction opens a short window (<see cref="LearnWindow"/>): the next few sightings (at least <see cref="LearnSpacing"/> apart, so they are different
/// moments, not one frame four times) join that name's face. A face already enrolled under a DIFFERENT name is never taken over, and after the first sighting a later one must still look like the
/// face being learned, so a second person walking into the frame is not mixed in.
/// </para>
/// </remarks>
public sealed class FaceWatcher
{
    private readonly SpeakerRegistry _faces;
    private readonly Action? _changed;
    private readonly Action<string>? _log;
    private readonly object _lock = new();

    private readonly Dictionary<string, int> _streak = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _greetedTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeSpan?> _away = new(StringComparer.OrdinalIgnoreCase);
    private List<VisiblePerson> _visible = new();

    private sealed class Learning
    {
        public required string Name;
        public required bool NeedNewFace;
        public required long ExpiresTick;
        public int Remaining;
        public bool Started;
        public long LastTick = long.MinValue / 2;
    }
    private Learning? _learning;

    /// <summary>A person returned after at least this long unseen: worth a greeting.</summary>
    public TimeSpan Absence { get; set; } = TimeSpan.FromHours(2);

    /// <summary>The same person is not greeted again within this long.</summary>
    public TimeSpan GreetCooldown { get; set; } = TimeSpan.FromHours(2);

    /// <summary>The same named person must be recognised in this many consecutive looks before a greeting (one misread frame says hi to the wrong person).</summary>
    public int SightingsToGreet { get; set; } = 2;

    /// <summary>A person stays "in view" this long after the last look that saw them.</summary>
    public TimeSpan VisibleHold { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>How long an introduction stays open for the face.</summary>
    public TimeSpan LearnWindow { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Sightings added to a face by one introduction.</summary>
    public int LearnSightings { get; set; } = 5;

    /// <summary>Least time between two sightings that both count toward learning.</summary>
    public TimeSpan LearnSpacing { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>A face being learned must still look this much like the face learned so far (cosine to its centre).</summary>
    public float LearnSameFaceScore { get; set; } = 0.30f;

    /// <summary>A named person is only refined by a match at least this good (cosine to the profile): a poor match must not rewrite who someone is.</summary>
    public float RefineMinScore { get; set; } = 0.5f;

    /// <summary>...and only by a look that is not nearly a copy of what the profile already holds (cosine to its centre below this): the same frame sixteen times would push every other angle out.</summary>
    public float RefineMaxSimilarity { get; set; } = 0.88f;

    /// <summary>Least time between two refinements of the same person.</summary>
    public TimeSpan RefineSpacing { get; set; } = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, long> _refinedTick = new(StringComparer.OrdinalIgnoreCase);
    private long _savedTick;
    private int _looks;

    /// <summary>Raised when a named person has come back after <see cref="Absence"/>.</summary>
    public event Action<Arrival>? Arrived;

    public FaceWatcher(SpeakerRegistry faces, Action? changed = null, Action<string>? log = null)
    {
        _faces = faces; _changed = changed; _log = log;
    }

    /// <summary>The people in view now.</summary>
    public IReadOnlyList<VisiblePerson> Visible(long nowTick)
    {
        lock (_lock) return _visible.Where(v => nowTick - v.LastSeenTick <= VisibleHold.TotalMilliseconds).ToList();
    }

    /// <summary>Whether an introduction is open for a face.</summary>
    public bool IsLearning { get { lock (_lock) return _learning != null; } }

    /// <summary>
    /// "I'm Nikki" (<paramref name="forNewFace"/> false: the face in front of the robot, the largest when several are in view) or "this is Nikki" about someone who has not been seen yet (true: the
    /// first face that is nobody enrolled). The next sightings are added to that name.
    /// </summary>
    public void BeginIntroduction(string name, bool forNewFace, long nowTick)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        lock (_lock)
        {
            _learning = new Learning { Name = name, NeedNewFace = forNewFace, Remaining = Math.Max(1, LearnSightings), ExpiresTick = nowTick + (long)LearnWindow.TotalMilliseconds };
        }
        _log?.Invoke($"learning {name}'s face from the next few looks{(forNewFace ? " (the next new face)" : "")}");
    }

    /// <summary>
    /// One look at the camera. <paramref name="previousLastSeen"/> is, for each named person, when they were last seen BEFORE this look (UTC ticks; the registry has already
    /// been updated by it), so the absence is measured against the true previous sighting.
    /// </summary>
    public void Observe(IReadOnlyList<AiFaceResult> results, IReadOnlyDictionary<string, long> previousLastSeen, long nowTick, DateTime? utcNow = null, int frameWidth = 0)
    {
        var arrivals = new List<Arrival>();
        var changed = false;
        lock (_lock)
        {
            var now = utcNow ?? DateTime.UtcNow;
            var visible = new List<VisiblePerson>();
            var seenNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_learning != null && nowTick > _learning.ExpiresTick) { _log?.Invoke($"introduction of {_learning.Name}'s face expired"); _learning = null; }
            if (_learning != null && TryLearn(results, nowTick)) changed = true;

            foreach (var r in results)
            {
                var name = r.Name;
                visible.Add(new VisiblePerson(name, name != null && IsGuest(name), r.Face.X, r.Face.Width, nowTick, frameWidth));
                if (name == null || IsGuest(name)) continue;
                seenNow.Add(name);
                var streak = _streak.GetValueOrDefault(name);
                // The absence is measured at the FIRST look of a run: by the second the registry already says "seen a moment ago".
                if (streak == 0)
                    _away[name] = previousLastSeen.TryGetValue(name, out var before) && before != 0 ? now - new DateTime(before, DateTimeKind.Utc) : null;
                _streak[name] = ++streak;
                if (streak != SightingsToGreet) continue;
                if (_greetedTick.TryGetValue(name, out var greeted) && nowTick - greeted < GreetCooldown.TotalMilliseconds) continue;
                _greetedTick[name] = nowTick;
                // Never seen before this run (just enrolled): not "back".
                if (_away.GetValueOrDefault(name) is { } away && away >= Absence) arrivals.Add(new Arrival(name, away));
            }
            foreach (var key in _streak.Keys.ToList()) if (!seenNow.Contains(key)) _streak[key] = 0;
            _visible = visible;

            // A person is seen from many angles in many lights: a confident, NEW-looking match adds to their face, so the next look from that angle is them too.
            foreach (var r in results)
            {
                if (r.Embedding == null || r.Match is not { Known: true } m || r.Match.Name == null || IsGuest(m.Name!) || m.Score < RefineMinScore || r.Face.Width < 60) continue;
                if (_refinedTick.TryGetValue(m.Name!, out var last) && nowTick - last < RefineSpacing.TotalMilliseconds) continue;
                var profile = _faces.Profiles.FirstOrDefault(p => string.Equals(p.Name, m.Name, StringComparison.OrdinalIgnoreCase));
                if (profile?.Centroid == null || SpeakerRegistry.Cosine(r.Embedding, profile.Centroid) > RefineMaxSimilarity) continue;
                _faces.Refine(m.Name!, r.Embedding, 1);
                _refinedTick[m.Name!] = nowTick;
                if (nowTick - _savedTick > 20_000) { _savedTick = nowTick; changed = true; }
            }

            // Guests that are plainly a named person under another name (an angle that failed the threshold once and was filed on its own) are merged into them.
            if (++_looks % 10 == 0)
                foreach (var gone in _faces.AbsorbDuplicateGuests())
                {
                    _log?.Invoke($"{gone} was the same person as someone already known: merged");
                    changed = true;
                }
        }
        if (changed) _changed?.Invoke();
        foreach (var a in arrivals) Arrived?.Invoke(a);
    }

    private bool IsGuest(string name) => _faces.Profiles.Any(p => p.IsGuest && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool IsEnrolledPerson(string? name) => name != null && _faces.Profiles.Any(p => !p.IsGuest && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds this look's face to the person being introduced, if one of the faces is theirs to add.</summary>
    private bool TryLearn(IReadOnlyList<AiFaceResult> results, long nowTick)
    {
        var l = _learning!;
        if (nowTick - l.LastTick < LearnSpacing.TotalMilliseconds) return false;
        var candidates = results.Where(r => r.Embedding != null).ToList();
        if (candidates.Count == 0) return false;

        AiFaceResult? pick;
        if (l.NeedNewFace && !l.Started)
            pick = candidates.Where(r => !IsEnrolledPerson(r.Name)).OrderByDescending(r => r.Face.Width).FirstOrDefault();
        else if (l.Started)
        {
            // Later looks must still be the face being learned: the one that looks like it, when several are in view.
            var profile = _faces.Profiles.FirstOrDefault(p => string.Equals(p.Name, l.Name, StringComparison.OrdinalIgnoreCase));
            pick = profile?.Centroid == null ? null : candidates
                .Select(r => (r, s: SpeakerRegistry.Cosine(r.Embedding!, profile.Centroid)))
                .Where(x => x.s >= LearnSameFaceScore).OrderByDescending(x => x.s).Select(x => x.r).FirstOrDefault();
        }
        else
            pick = candidates.OrderByDescending(r => r.Face.Width).First();     // "I'm Nikki": the face closest to the robot
        if (pick == null) return false;

        // Already enrolled under ANOTHER name: not this person's face to take over.
        if (IsEnrolledPerson(pick.Name) && !string.Equals(pick.Name, l.Name, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke($"that face is already {pick.Name}; not changing it to {l.Name}");
            return false;
        }
        // The face was filed as a provisional guest while it was unknown: the guest becomes the person (earlier sightings come along).
        if (pick.Guest != null && pick.Guest.IsGuest) _faces.Rename(pick.Guest.Name, l.Name);
        else _faces.Enroll(l.Name, pick.Embedding!, 1);
        l.Started = true; l.LastTick = nowTick;
        _log?.Invoke($"learning {l.Name}'s face ({l.Remaining - 1} more)");
        if (--l.Remaining <= 0) { _log?.Invoke($"learned {l.Name}'s face"); _learning = null; }
        return true;
    }
}
