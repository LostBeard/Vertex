using System.Text.Json;

namespace Vertex.Brain;

/// <summary>A named spot in the world ("home", "the mine", "Aubri's farm").</summary>
public sealed record Place(string Name, string Dimension, int X, int Y, int Z, string? MarkedBy = null);

/// <summary>
/// What Vertex remembers about one server's world: facts players told it, and named places. Kept per server
/// (%APPDATA%\Vertex\worlds\host_port.json), because "home" on one server means nothing on another.
/// </summary>
public sealed class WorldMemory
{
    public List<string> Facts { get; set; } = [];
    public List<Place> Places { get; set; } = [];

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
            File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
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
