using SpawnDev.AI;
using SpawnDev.AI.Server;

namespace Vertex.AI;

/// <summary>
/// Who is talking, as far as Vertex can tell from the voice: turns the identification of each utterance
/// (<see cref="AiSpeakerResult"/>) into a name and a stable key for the companion, keeps a conversation going across
/// utterances too short to identify, and runs introductions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Introductions are a short learning phase, not one clip.</b> "I'm Nikki" is a second of speech, and an embedding of a
/// second is a poor voiceprint (the model card says under two seconds is unreliable). So an introduction opens a window in
/// which the next few utterances of that voice (each at least <see cref="LearnMinSeconds"/> long) are added to the person's
/// profile. A voice that is already enrolled under a DIFFERENT name is never taken over: anyone can say "I'm Todd", and the
/// recording of Todd is not theirs to rename.
/// </para>
/// <para>
/// "This is Nikki" about someone who has not spoken yet is <see cref="BeginIntroduction"/> with <c>forNextVoice</c>: the
/// first stranger's utterance after it starts the learning window, instead of the speaker of the introduction.
/// </para>
/// <para>
/// A voice nobody enrolled becomes "Guest N" (the registry's provisional profile), so the prompt can still tell two strangers
/// apart. Too-short utterances (a "yes") cannot be identified at all: they are attributed to the previous speaker if they came
/// within <see cref="Continuity"/>, which is how a conversation reads.
/// </para>
/// </remarks>
public sealed class SpeakerTracker
{
    private readonly SpeakerRegistry _registry;
    private readonly Action? _changed;
    private readonly Action<string>? _log;
    private readonly object _lock = new();

    private sealed class Learning
    {
        public required string Name;
        public required bool NeedNewVoice;
        public required long ExpiresTick;
        public int Remaining;
        public bool Started;
    }

    private Learning? _learning;
    private AiSpeakerResult? _lastResult;
    private string? _lastName;
    private long _lastTick;

    /// <summary>Raised when someone is introduced ("I'm Nikki" or "this is Nikki"): the name, and whether it is about someone who has not spoken yet. The face side learns the face from it.</summary>
    public event Action<string, bool>? Introduced;

    /// <summary>What an unidentified voice is called when nothing else is known.</summary>
    public string Unknown { get; set; } = "someone in the room";

    /// <summary>Speech shorter than this is not used to learn a voice.</summary>
    public double LearnMinSeconds { get; set; } = 1.5;

    /// <summary>Utterances added to a profile by one introduction.</summary>
    public int LearnUtterances { get; set; } = 4;

    /// <summary>How long an introduction stays open.</summary>
    public TimeSpan LearnWindow { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How long an utterance too short to identify is still attributed to the previous speaker.</summary>
    public TimeSpan Continuity { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>A learning voice must sound at least this much like the profile it is joining (cosine to its centre).</summary>
    public float LearnSameVoiceScore { get; set; } = 0.30f;

    /// <summary>Who someone was resolved to.</summary>
    /// <param name="Key">A stable key for per-person memory ("voice:Nikki"), or "room" when nothing is known.</param>
    /// <param name="Name">The name for the prompt.</param>
    /// <param name="Identified">True when the voice was recognised (or just learned), false for a guess or nobody.</param>
    public sealed record Who(string Key, string Name, bool Identified);

    public SpeakerTracker(SpeakerRegistry registry, Action? changed = null, Action<string>? log = null)
    {
        _registry = registry;
        _changed = changed;
        _log = log;
    }

    /// <summary>The name and key for an utterance. <paramref name="result"/> is null when identification is off or failed.</summary>
    public Who Resolve(AiSpeakerResult? result, long nowTick)
    {
        bool changed = false;
        Who who;
        lock (_lock)
        {
            who = ResolveLocked(result, nowTick, ref changed);
        }
        if (changed) _changed?.Invoke();
        return who;
    }

    private Who ResolveLocked(AiSpeakerResult? r, long now, ref bool changed)
    {
        if (r == null) return Fallback(now);
        _lastResult = r;

        // ── learning window ──
        if (_learning != null && now > _learning.ExpiresTick) { _log?.Invoke($"introduction of {_learning.Name} expired"); _learning = null; }
        if (_learning != null && r.Seconds >= LearnMinSeconds && TryLearn(r, ref changed) is { } learned)
        {
            _lastName = learned; _lastTick = now;
            return new Who("voice:" + learned, learned, true);
        }

        var name = r.Name;                              // an enrolled person or this voice's guest
        if (name != null && (r.Reliable || r.Match.Known))
        {
            _lastName = name; _lastTick = now;
            return new Who("voice:" + name, name, true);
        }
        if (name != null && r.Seconds >= LearnMinSeconds)
        {
            // A guest from a short-ish clip: named, but not enough evidence to take over the conversation.
            return new Who("voice:" + name, name, true);
        }
        return Fallback(now);
    }

    private Who Fallback(long now)
    {
        if (_lastName != null && now - _lastTick <= (long)Continuity.TotalMilliseconds)
            return new Who("voice:" + _lastName, _lastName, false);
        return new Who("room", Unknown, false);
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool IsEnrolledPerson(string? name) =>
        name != null && _registry.Profiles.Any(p => !p.IsGuest && Same(p.Name, name));

    /// <summary>Adds this utterance to the learning person's voice, or returns null when it is not theirs to add.</summary>
    private string? TryLearn(AiSpeakerResult r, ref bool changed)
    {
        var l = _learning!;
        var heardAs = r.Match.Known ? r.Match.Name : null;
        // Already enrolled under ANOTHER name: not this person's voice to take over.
        if (heardAs != null && !Same(heardAs, l.Name) && IsEnrolledPerson(heardAs)) return null;

        if (l.NeedNewVoice && !l.Started)
        {
            // "This is Nikki" about someone who has not spoken: the first voice that is NOT someone enrolled is her.
            if (IsEnrolledPerson(heardAs)) return null;
        }
        else if (l.Started)
        {
            // Later utterances must still sound like the voice being learned (another person may be talking).
            var profile = _registry.Profiles.FirstOrDefault(p => Same(p.Name, l.Name));
            if (profile?.Centroid != null && SpeakerRegistry.Cosine(r.Embedding, profile.Centroid) < LearnSameVoiceScore) return null;
        }

        // The voice was filed as a provisional guest while it was unknown: the guest becomes the person (their earlier
        // utterances come along); otherwise the embedding joins the person's profile.
        if (r.Guest != null && r.Guest.IsGuest) _registry.Rename(r.Guest.Name, l.Name);
        else _registry.Enroll(l.Name, r.Embedding, r.Seconds);
        changed = true;
        l.Started = true;
        _log?.Invoke($"learning {l.Name}'s voice ({r.Seconds:0.0} s; {l.Remaining - 1} more)");
        if (--l.Remaining <= 0) { _log?.Invoke($"learned {l.Name}"); _learning = null; }
        return l.Name;
    }

    /// <summary>
    /// "I'm Nikki" (<paramref name="forNextVoice"/> false: the voice that just spoke) or "this is Nikki" about someone who has
    /// not spoken yet (true: the next voice that is nobody enrolled). Returns what to tell the language model.
    /// </summary>
    public string BeginIntroduction(string name, bool forNextVoice, long nowTick)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "error: no name given";
        bool changed = false;
        string message;
        lock (_lock)
        {
            var learning = new Learning
            {
                Name = name, NeedNewVoice = forNextVoice, Remaining = Math.Max(1, LearnUtterances),
                ExpiresTick = nowTick + (long)LearnWindow.TotalMilliseconds,
            };
            if (!forNextVoice && _lastResult is { } last)
            {
                var heardAs = last.Match.Known ? last.Match.Name : null;
                if (heardAs != null && !Same(heardAs, name) && IsEnrolledPerson(heardAs))
                    return $"That voice is already {heardAs}. I do not change a voice I know; if you are {name}, that is a different person from the one speaking.";
                _learning = learning;
                // The introduction itself counts when it was long enough to be a voiceprint.
                if (last.Seconds >= LearnMinSeconds && TryLearn(last, ref changed) != null) { /* counted */ }
                message = $"Okay. I will learn {name}'s voice from the next few things they say.";
            }
            else
            {
                _learning = learning;
                message = forNextVoice
                    ? $"Okay. The next new voice I hear is {name}; I will learn it from the next few things they say."
                    : $"Okay. I will learn {name}'s voice from the next few things they say.";
            }
        }
        if (changed) _changed?.Invoke();
        Introduced?.Invoke(name, forNextVoice);
        return message;
    }

    /// <summary>Forgets a person's voice.</summary>
    public bool Forget(string name)
    {
        var removed = _registry.Forget(name);
        if (removed) _changed?.Invoke();
        return removed;
    }

    /// <summary>Whether an introduction is open (the voice is being learned).</summary>
    public bool IsLearning { get { lock (_lock) return _learning != null; } }
}
