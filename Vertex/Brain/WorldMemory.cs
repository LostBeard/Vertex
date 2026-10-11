using System.Text.Json;

namespace Vertex.Brain;

/// <summary>A named spot in the world ("home", "the mine", "Aubri's farm").</summary>
public sealed record Place(string Name, string Dimension, int X, int Y, int Z, string? MarkedBy = null);

/// <summary>What Vertex knows about one player, kept by UUID (a player can change their name).</summary>
public sealed class PlayerMemory
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime FirstMet { get; set; }
    public DateTime LastSeen { get; set; }
    /// <summary>Separate times they came to play (seen again after more than an hour away).</summary>
    public int Visits { get; set; }
    /// <summary>Things they told Vertex about themselves, or asked it to remember about them.</summary>
    public List<string> Facts { get; set; } = [];
    /// <summary>What Vertex did for or with them, oldest first ("2026-10-11: built a house").</summary>
    public List<string> History { get; set; } = [];
}

/// <summary>Something to work toward over many sessions ("build a castle", "find diamonds for Aubri").</summary>
public sealed class Objective
{
    public string Text { get; set; } = "";
    /// <summary>Who it is for (a player name), if anyone.</summary>
    public string? For { get; set; }
    public DateTime Created { get; set; }
    /// <summary>When it was finished, or null while it is still being worked on.</summary>
    public DateTime? Done { get; set; }
    /// <summary>Steps taken so far, oldest first.</summary>
    public List<string> Progress { get; set; } = [];
}

/// <summary>
/// What Vertex remembers about one server's world: facts players told it, and named places. Kept per server
/// (%APPDATA%\Vertex\worlds\host_port.json), because "home" on one server means nothing on another.
/// </summary>
public sealed class WorldMemory
{
    /// <summary>A player seen again after this long away has come for a new visit.</summary>
    public static readonly TimeSpan NewVisitAfter = TimeSpan.FromHours(1);
    private const int MaxHistory = 30, MaxProgress = 20;

    public List<string> Facts { get; set; } = [];
    public List<Place> Places { get; set; } = [];
    /// <summary>The players Vertex has met, by UUID.</summary>
    public Dictionary<Guid, PlayerMemory> People { get; set; } = [];
    /// <summary>Long-running goals, finished ones included (Done set).</summary>
    public List<Objective> Goals { get; set; } = [];

    private string? _path;

    public static WorldMemory Load(string folder, string server)
    {
        var path = Path.Combine(folder, string.Concat(server.ToLowerInvariant().Split(Path.GetInvalidFileNameChars())) + ".json");
        WorldMemory memory;
        try { memory = File.Exists(path) ? JsonSerializer.Deserialize<WorldMemory>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { memory = new(); }
        memory._path = path;
        return memory;
    }

    public void Save()
    {
        if (_path == null) return;
        lock (this)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Write, then replace: a crash mid-save must not leave half a file (an unreadable one loads as nothing).
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
    }

    public void AddFact(string fact)
    {
        lock (this) Facts.Add(fact.Trim());
        Save();
    }

    /// <summary>
    /// The facts worth putting in front of the language model now: every fact that names one of these players
    /// (who is around), then the most recent others, up to `max`. Oldest first.
    /// </summary>
    public List<string> FactsFor(IEnumerable<string> players, int max = 16)
    {
        List<string> facts;
        lock (this) facts = Facts.ToList();
        var names = players.Where(n => n.Length >= 2).Select(n => n.ToLowerInvariant()).ToList();
        bool AboutSomeoneHere(string f) => names.Any(n => f.Contains(n, StringComparison.OrdinalIgnoreCase));
        var about = facts.Where(AboutSomeoneHere).TakeLast(max).ToList();
        var recent = facts.Where(f => !AboutSomeoneHere(f)).TakeLast(Math.Max(0, max - about.Count)).ToList();
        return facts.Where(f => about.Contains(f) || recent.Contains(f)).ToList();
    }

    // ---------------------------------------------------------------- people

    /// <summary>
    /// Notes that a player is here: the first meeting, or (after an hour away) a new visit. Returns their memory
    /// and when they were last seen before now (null the first time).
    /// </summary>
    public (PlayerMemory Player, DateTime? LastSeen) Meet(Guid id, string name, DateTime now)
    {
        PlayerMemory? p;
        DateTime? before;
        lock (this)
        {
            if (!People.TryGetValue(id, out p))
            {
                People[id] = p = new PlayerMemory { Id = id, Name = name, FirstMet = now, LastSeen = now, Visits = 1 };
                before = null;
            }
            else
            {
                before = p.LastSeen;
                if (now - p.LastSeen > NewVisitAfter) p.Visits++;
                p.LastSeen = now;
                p.Name = name; // they may have changed it
            }
        }
        Save();
        return (p, before);
    }

    /// <summary>Keeps a known player's "last seen" fresh while they are on (saved with the next change).</summary>
    public void Seen(Guid id, DateTime now)
    {
        lock (this)
            if (People.TryGetValue(id, out var p))
                p.LastSeen = now;
    }

    /// <summary>A player we know, by (fuzzy) name.</summary>
    public PlayerMemory? Person(string name)
    {
        var n = Simplify(name);
        if (n.Length == 0) return null;
        lock (this)
        {
            return People.Values.FirstOrDefault(p => Simplify(p.Name) == n)
                ?? People.Values.FirstOrDefault(p => Simplify(p.Name).StartsWith(n) || n.StartsWith(Simplify(p.Name)))
                ?? People.Values.Where(p => Distance(Simplify(p.Name), n) <= Math.Max(1, n.Length / 3)).OrderBy(p => Distance(Simplify(p.Name), n)).FirstOrDefault();
        }
    }

    /// <summary>The one player we know whose name appears as a word in the text, or null (none, or several).</summary>
    public PlayerMemory? PersonNamedIn(string text)
    {
        var words = System.Text.RegularExpressions.Regex.Split(text, @"[^\p{L}\p{N}_]+").Where(w => w.Length > 0)
            .Select(w => w.EndsWith("s", StringComparison.OrdinalIgnoreCase) && w.Length > 2 ? [w, w[..^1]] : new[] { w })
            .SelectMany(w => w).Select(Simplify).ToHashSet();
        List<PlayerMemory> named;
        lock (this) named = People.Values.Where(p => words.Contains(Simplify(p.Name))).ToList();
        if (named.Count > 0) return named.Count == 1 ? named[0] : null;
        // A nickname ("Aubs" for Aubri: the model used it on 2026-10-11): a capitalized word that shares its first
        // three letters, and most of itself, with exactly one player's name. Capitalized only, so "the" never
        // matches a "Theo".
        var nicknames = System.Text.RegularExpressions.Regex.Split(text, @"[^\p{L}\p{N}_]+")
            .Where(w => w.Length >= 3 && char.IsUpper(w[0])).Select(Simplify).ToList();
        lock (this)
            named = People.Values.Where(p => nicknames.Any(w => SharedPrefix(w, Simplify(p.Name)) is var n && n >= 3 && n >= w.Length * 0.6)).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static int SharedPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return n;
    }

    public void AddPersonFact(PlayerMemory person, string fact)
    {
        lock (this) person.Facts.Add(fact.Trim());
        Save();
    }

    public void AddHistory(PlayerMemory person, string entry, DateTime now)
    {
        lock (this)
        {
            person.History.Add($"{now:yyyy-MM-dd}: {entry.Trim()}");
            if (person.History.Count > MaxHistory) person.History.RemoveRange(0, person.History.Count - MaxHistory);
        }
        Save();
    }

    // ---------------------------------------------------------------- goals

    public IReadOnlyList<Objective> ActiveGoals
    {
        get { lock (this) return Goals.Where(g => g.Done == null).ToList(); }
    }

    public Objective AddGoal(string text, string? forPlayer, DateTime now)
    {
        var goal = new Objective { Text = text.Trim(), For = string.IsNullOrWhiteSpace(forPlayer) ? null : forPlayer, Created = now };
        lock (this) Goals.Add(goal);
        Save();
        return goal;
    }

    /// <summary>An unfinished goal by what someone called it (exact, containing, near spelling), or the only one.</summary>
    public Objective? FindGoal(string? text)
    {
        var active = ActiveGoals;
        if (string.IsNullOrWhiteSpace(text)) return active.Count == 1 ? active[0] : null;
        var n = GoalKey(text);
        if (n.Length == 0) return null;
        return active.FirstOrDefault(g => GoalKey(g.Text) == n)
            ?? active.FirstOrDefault(g => GoalKey(g.Text).Contains(n) || n.Contains(GoalKey(g.Text)))
            ?? active.Where(g => Distance(GoalKey(g.Text), n) <= Math.Max(2, n.Length / 3)).OrderBy(g => Distance(GoalKey(g.Text), n)).FirstOrDefault();
    }

    private static readonly HashSet<string> Filler = ["the", "a", "an", "our", "my", "your", "that", "this", "goal"];

    /// <summary>A goal's words without the filler ("the castle" is part of "build a castle by the lake").</summary>
    private static string GoalKey(string text) =>
        string.Concat(text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Simplify).Where(w => w.Length > 0 && !Filler.Contains(w)));

    public void AddProgress(Objective goal, string step)
    {
        lock (this)
        {
            goal.Progress.Add(step.Trim());
            if (goal.Progress.Count > MaxProgress) goal.Progress.RemoveRange(0, goal.Progress.Count - MaxProgress);
        }
        Save();
    }

    public void FinishGoal(Objective goal, DateTime now)
    {
        lock (this) goal.Done = now;
        Save();
    }

    /// <summary>Remembers (or moves) a named place.</summary>
    public Place SetPlace(string name, string dimension, int x, int y, int z, string? by)
    {
        var place = new Place(CleanName(name), dimension, x, y, z, by);
        lock (this)
        {
            Places.RemoveAll(p => Simplify(p.Name) == Simplify(place.Name));
            Places.Add(place);
        }
        Save();
        return place;
    }

    public bool ForgetPlace(string name)
    {
        var p = FindPlace(name);
        if (p == null) return false;
        lock (this) Places.Remove(p);
        Save();
        return true;
    }

    /// <summary>A place by what a player called it: exact, then containing, then a near spelling.</summary>
    public Place? FindPlace(string name)
    {
        var n = Simplify(CleanName(name));
        if (n.Length == 0) return null;
        lock (this)
        {
            return Places.FirstOrDefault(p => Simplify(p.Name) == n)
                ?? Places.FirstOrDefault(p => Simplify(p.Name).Contains(n) || n.Contains(Simplify(p.Name)))
                ?? Places.Where(p => Distance(Simplify(p.Name), n) <= Math.Max(1, n.Length / 4)).OrderBy(p => Distance(Simplify(p.Name), n)).FirstOrDefault();
        }
    }

    /// <summary>"the mine" and "our base" are the same names as "mine" and "base".</summary>
    private static string CleanName(string name)
    {
        var words = name.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && words[0] is "the" or "our" or "a") words.RemoveAt(0);
        return string.Join(' ', words);
    }

    private static string Simplify(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
