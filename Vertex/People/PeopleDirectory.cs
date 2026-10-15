using SpawnDev.AI;

namespace Vertex.People;

/// <summary>One person as the people page shows them: what Vertex holds about them, and when it will let go of it.</summary>
/// <param name="Name">Their name, or "Guest N" for a stranger nobody introduced.</param>
/// <param name="IsGuest">True for a stranger: forgotten automatically after the guest lifetime.</param>
/// <param name="HasVoice">Whether a voiceprint is held.</param>
/// <param name="HasFace">Whether a face fingerprint is held.</param>
/// <param name="FirstSeenUtc">When they were first filed (guests only; null for an introduced person).</param>
/// <param name="LastSeenUtc">The last time they were seen or heard.</param>
/// <param name="ForgottenAtUtc">When a guest will be forgotten (null for an introduced person: kept until removed).</param>
public sealed record PersonRecord(string Name, bool IsGuest, bool HasVoice, bool HasFace, DateTime? FirstSeenUtc, DateTime? LastSeenUtc, DateTime? ForgottenAtUtc);

/// <summary>
/// The people Vertex knows, voice and face together, and the one place they are removed from. A person is the same NAME in the voice registry and the face registry (introducing
/// someone names both), so the list merges the two and removing a person removes both. Everything here is local to this PC.
/// </summary>
/// <remarks>
/// The rule for who is kept: a person who has been introduced is kept until someone removes them; a stranger nobody introduced is a guest and is forgotten after
/// <see cref="GuestLifetime"/> (counted from the first time they were noticed).
/// </remarks>
public sealed class PeopleDirectory
{
    private readonly SpeakerRegistry _voices, _faces;
    private readonly Action _saveVoices, _saveFaces;

    /// <summary>How long a stranger nobody introduces is remembered (default 24 hours).</summary>
    public TimeSpan GuestLifetime
    {
        get => _voices.GuestLifetime ?? TimeSpan.FromHours(24);
        set { _voices.GuestLifetime = value; _faces.GuestLifetime = value; }
    }

    public PeopleDirectory(SpeakerRegistry voices, SpeakerRegistry faces, Action saveVoices, Action saveFaces, TimeSpan? guestLifetime = null)
    {
        _voices = voices; _faces = faces; _saveVoices = saveVoices; _saveFaces = saveFaces;
        GuestLifetime = guestLifetime ?? TimeSpan.FromHours(24);
    }

    /// <summary>Everyone held, introduced people first (by name), then guests (newest first).</summary>
    public IReadOnlyList<PersonRecord> List()
    {
        var byName = new Dictionary<string, (SpeakerProfile? Voice, SpeakerProfile? Face)>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in _voices.Profiles) byName[v.Name] = (v, byName.GetValueOrDefault(v.Name).Face);
        foreach (var f in _faces.Profiles) byName[f.Name] = (byName.GetValueOrDefault(f.Name).Voice, f);

        var people = new List<PersonRecord>();
        foreach (var (name, (voice, face)) in byName)
        {
            var guest = (voice?.IsGuest ?? true) && (face?.IsGuest ?? true);
            var firstTicks = new[] { voice?.FirstSeenTicks ?? 0, face?.FirstSeenTicks ?? 0 }.Where(t => t != 0).DefaultIfEmpty(0).Min();
            var lastTicks = Math.Max(voice?.LastHeardTicks ?? 0, face?.LastHeardTicks ?? 0);
            DateTime? first = firstTicks == 0 ? null : new DateTime(firstTicks, DateTimeKind.Utc);
            DateTime? last = lastTicks == 0 ? null : new DateTime(lastTicks, DateTimeKind.Utc);
            DateTime? forgotten = guest ? (first ?? last) + GuestLifetime : null;
            people.Add(new PersonRecord(voice?.Name ?? face!.Name, guest, voice != null, face != null, guest ? first : null, last, forgotten));
        }
        return people.OrderBy(p => p.IsGuest).ThenBy(p => p.IsGuest ? -(p.LastSeenUtc?.Ticks ?? 0) : 0).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Removes one person: their voiceprint and their face fingerprint. False when nobody has that name.</summary>
    public bool Forget(string name)
    {
        var voice = _voices.Forget(name);
        var face = _faces.Forget(name);
        if (voice) _saveVoices();
        if (face) _saveFaces();
        return voice || face;
    }

    /// <summary>Removes every guest (strangers nobody introduced). Returns how many people were removed.</summary>
    public int ForgetGuests()
    {
        var names = List().Where(p => p.IsGuest).Select(p => p.Name).ToList();
        foreach (var n in names) Forget(n);
        return names.Count;
    }

    /// <summary>Removes everyone: every voiceprint and every face fingerprint. Returns how many people were removed.</summary>
    public int ForgetAll()
    {
        var names = List().Select(p => p.Name).ToList();
        foreach (var n in names) Forget(n);
        return names.Count;
    }

    /// <summary>Forgets the guests that have been remembered longer than <see cref="GuestLifetime"/>, in both registries, and saves. Returns who was forgotten.</summary>
    public IReadOnlyList<string> ExpireGuests(DateTime? utcNow = null)
    {
        var gone = _voices.ExpireGuests(utcNow);
        var goneFaces = _faces.ExpireGuests(utcNow);
        if (gone.Count > 0) _saveVoices();
        if (goneFaces.Count > 0) _saveFaces();
        return gone.Concat(goneFaces).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
