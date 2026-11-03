namespace Vertex.Brain;

/// <summary>One second of his life as the recorder sees it: health, what was near, what he was doing, when and where it was.</summary>
/// <param name="Mobs">Hostile mobs within 24 blocks: what kind and how far (fliers included).</param>
public sealed record Moment(DateTime At, float Health, int Food, string? Task, long DayTime, string Weather, string Where, IReadOnlyList<(string Kind, double Distance)> Mobs, bool HasWeapon, bool HasFood);

/// <summary>
/// The start of learning from the live game (TJ, 2026-11-03, after phantoms killed him 70 seconds after god mode went off: "help him learn to learn"). It watches a stream of <see cref="Moment"/>s and
/// turns the two things that matter most into LESSONS in his own words, from facts it saw and nothing else: a death (what killed him, when, what was near, what he was doing, whether he had dug in,
/// whether he had a weapon) and a big loss of health that did not kill him (the same, plus that he survived). The brain stores them with tags (death, death:phantom, night, no-cover...) and uses them
/// before he decides (the prompt for his own projects) and to move a threshold (how early he takes cover). No game in it: <see cref="Observe"/> takes moments and returns lessons, so it is tested
/// with scripted sequences.
/// </summary>
public sealed class ExperienceLog
{
    private readonly List<Moment> _ring = [];
    private DateTime _lastBigLoss = DateTime.MinValue;
    private bool _dead;
    private float? _pendingPeak;
    private float _pendingLowest, _previousHealth = 20;
    private DateTime _lastDropAt;

    /// <summary>How many seconds of history a lesson looks back over.</summary>
    public const int Window = 60;

    /// <summary>Health points (half hearts) lost within 12 seconds that count as a big loss.</summary>
    public const float BigLossPoints = 8;

    /// <summary>
    /// The threshold of health (half hearts) at or below which he takes cover with a monster near: 10 (five hearts) to begin with, and 2 higher for each death in the last day in which he had not dug in,
    /// up to 16: he starts digging in earlier once not digging in has killed him.
    /// </summary>
    public static int CoverBelow(int deathsWithoutCoverToday) => Math.Min(16, 10 + 2 * Math.Max(0, deathsWithoutCoverToday));

    /// <summary>Takes the latest moment (once a second) and says what it taught, if anything: at most one lesson per call.</summary>
    /// <param name="deathMessage">What the server said about how he died, if it has said (for example "HereticSpawn was slain by Phantom").</param>
    public IReadOnlyList<Skills.Lesson> Observe(Moment now, string? deathMessage = null)
    {
        _ring.Add(now);
        while (_ring.Count > Window) _ring.RemoveAt(0);
        var lessons = new List<Skills.Lesson>();

        if (now.Health <= 0)
        {
            _pendingPeak = null;       // a loss that ended in death is the death's story, not "got through it"
            if (!_dead) { _dead = true; lessons.Add(Death(now, deathMessage)); }
            return lessons;
        }
        _dead = false;

        if (_pendingPeak is float pending)
        {
            // A big loss is only "got through" once health has stopped dropping for 5 seconds.
            if (now.Health < _previousHealth) { _lastDropAt = now.At; _pendingLowest = Math.Min(_pendingLowest, now.Health); }
            if ((now.At - _lastDropAt).TotalSeconds >= 5)
            {
                lessons.Add(BigLoss(now, pending - _pendingLowest));
                _pendingPeak = null;
            }
        }
        else
        {
            var recent = _ring.Where(m => (now.At - m.At).TotalSeconds <= 12).ToList();
            var peak = recent.Max(m => m.Health);
            if (peak - now.Health >= BigLossPoints && (now.At - _lastBigLoss).TotalSeconds > 90)
            {
                _lastBigLoss = now.At;
                _pendingPeak = peak;
                _pendingLowest = now.Health;
                _lastDropAt = now.At;
            }
        }
        _previousHealth = now.Health;
        return lessons;
    }

    // ------------------------------------------------------------------ the words

    private Skills.Lesson Death(Moment now, string? deathMessage)
    {
        var history = _ring.Where(m => m.Health > 0).ToList();
        var from = history.Count == 0 ? null : history.MaxBy(m => m.Health);
        var cause = CauseOf(deathMessage, history);
        var facts = Gather(now, history);
        var text = $"I died{(cause.Length > 0 ? $": {cause}" : "")}. " + facts.When + (from == null ? "" : $" In the last {Math.Max(1, (int)(now.At - from.At).TotalSeconds)} seconds my health went from {from.Health / 2:0.#} hearts to none.")
            + facts.Around + facts.Doing + facts.Cover + facts.Kit;
        var tags = new List<string> { "death", facts.Night ? "night" : "day", facts.Covered ? "cover" : "no-cover" };
        tags.AddRange(facts.Kinds.Select(k => "death:" + k.Replace("minecraft:", "")));
        return new Skills.Lesson(text, "death", false, tags);
    }

    private Skills.Lesson BigLoss(Moment now, float lostPoints)
    {
        var history = _ring.Where(m => m.Health > 0).ToList();
        var facts = Gather(now, history);
        var text = $"I lost {lostPoints / 2:0.#} hearts in a few seconds and got through it ({now.Health / 2:0.#} hearts left). " + facts.When + facts.Around + facts.Doing + facts.Cover + facts.Kit;
        var tags = new List<string> { "big-loss", facts.Night ? "night" : "day", facts.Covered ? "cover" : "no-cover" };
        tags.AddRange(facts.Kinds.Select(k => "big-loss:" + k.Replace("minecraft:", "")));
        return new Skills.Lesson(text, "big-loss", true, tags);
    }

    private static string CauseOf(string? deathMessage, List<Moment> history)
    {
        if (!string.IsNullOrWhiteSpace(deathMessage)) return deathMessage.Trim();
        var kind = history.TakeLast(10).SelectMany(m => m.Mobs).OrderBy(m => m.Distance).Select(m => m.Kind).FirstOrDefault();
        return kind == null ? "" : $"a {kind} was next to me";
    }

    private sealed record Facts(string When, string Around, string Doing, string Cover, string Kit, bool Night, bool Covered, List<string> Kinds);

    private static Facts Gather(Moment now, List<Moment> history)
    {
        var tod = ((now.DayTime % 24000) + 24000) % 24000;
        var phase = tod < 12000 ? "day" : tod < 13000 ? "sunset" : tod < 23000 ? "night" : "sunrise";
        var night = phase is "night" or "sunset";
        var weather = now.Weather == "clear" ? "" : $" and {now.Weather}";
        var when = $"It was {phase}{weather}, at {now.Where}.";
        // Kinds seen near him in the last 30 seconds, with the most at one time.
        var window = history.Where(m => (now.At - m.At).TotalSeconds <= 30).ToList();
        var most = window.SelectMany(m => m.Mobs.GroupBy(x => x.Kind).Select(g => (Kind: g.Key, Count: g.Count()))).GroupBy(x => x.Kind).Select(g => (Kind: g.Key, Max: g.Max(x => x.Count))).OrderByDescending(x => x.Max).ToList();
        var around = most.Count == 0 ? " Nothing hostile was near that I saw." : " Near me: " + string.Join(", ", most.Select(x => x.Max > 1 ? $"{x.Max} {Plain(x.Kind)}s" : $"a {Plain(x.Kind)}")) + ".";
        var tasks = history.Select(m => m.Task).Where(t => !string.IsNullOrEmpty(t)).Distinct().TakeLast(4).ToList();
        var doing = tasks.Count == 0 ? " I was not doing anything in particular." : " I was doing: " + string.Join("; ", tasks) + ".";
        var covered = tasks.Any(t => t!.StartsWith("taking cover")) || history.Any(m => m.Task?.StartsWith("taking cover") == true);
        var cover = covered ? " I had dug in." : " I had not taken cover.";
        var kit = (now.HasWeapon ? " I had a weapon." : " I had no weapon.") + (now.HasFood ? " I had food." : " I had no food.");
        return new Facts(when, around, doing, cover, kit, night, covered, most.Select(x => x.Kind).ToList());
    }

    private static string Plain(string kind) => kind.Replace("minecraft:", "").Replace('_', ' ');
}
