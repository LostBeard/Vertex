using System.Text;
using System.Text.RegularExpressions;

namespace Vertex.Brain;

/// <summary>
/// Is Vertex being called, however the speech recogniser wrote its name? A rare word is the recogniser's weak spot: live, "Vertex" came back as
/// "Rutax" and as "Per text" (and, on Whisper, "birdtakes"), and the robot answered "I'm not sure who Rutax is". Two layers: the spellings that have
/// been MEASURED (never guessed), and a consonant-skeleton match for the ones that have not been seen yet.
/// </summary>
public static class WakeWord
{
    /// <summary>How the name has been written when someone said "Vertex" (letters only, spaces removed). Add what the heard folder shows.</summary>
    public static readonly string[] VertexHeardAs = ["vertex", "vortex", "birdtakes", "rutax", "pertext"];

    /// <summary>Real words whose consonants are one edit from "vertex" ("virtues"). Never taken for the name.</summary>
    private static readonly HashSet<string> NotTheName = ["virtue", "virtues", "vertical", "vertices", "vortexes", "ratchet", "reflex", "reflects"];

    private static readonly Regex Words = new(@"[A-Za-z][A-Za-z']*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The consonants of a word, repeats collapsed ("rutax" -> "rtx", "vertex" -> "vrtx"): what survives a recogniser mishearing the vowels.</summary>
    public static string Skeleton(string word)
    {
        var sb = new StringBuilder();
        foreach (var c in word.ToLowerInvariant())
        {
            if (c is < 'a' or > 'z' || "aeiouy".Contains(c)) continue;
            if (sb.Length == 0 || sb[^1] != c) sb.Append(c);
        }
        return sb.ToString();
    }

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

    /// <summary>
    /// The first one or two words (within the first five) that are the name, however written: its start index and length in <paramref name="text"/>, or null.
    /// Only the exact spelling counts anywhere in the line; a near-miss counts near the start, where people put a name.
    /// </summary>
    public static (int Start, int Length)? Find(string text, string name)
    {
        var target = new string(name.ToLowerInvariant().Where(char.IsLetter).ToArray());
        if (target.Length == 0) return null;
        var variants = name.Equals("Vertex", StringComparison.OrdinalIgnoreCase) ? VertexHeardAs : [target];
        var skeleton = Skeleton(target);
        var tokens = Words.Matches(text);

        // Exact spelling (or a measured variant) anywhere.
        for (var i = 0; i < tokens.Count; i++)
            for (var span = 1; span <= 2 && i + span <= tokens.Count; span++)
            {
                var candidate = Letters(tokens, i, span);
                if (variants.Contains(candidate)) return Range(tokens, i, span);
            }

        // A near-miss of the consonants, near the start.
        if (skeleton.Length < 3) return null;
        for (var i = 0; i < Math.Min(tokens.Count, 5); i++)
            for (var span = 1; span <= 2 && i + span <= tokens.Count; span++)
            {
                var candidate = Letters(tokens, i, span);
                if (candidate.Length < 4 || NotTheName.Contains(candidate)) continue;
                if (Distance(Skeleton(candidate), skeleton) <= 1) return Range(tokens, i, span);
            }
        return null;
    }

    /// <summary>Whether the line calls <paramref name="name"/>. The old "contains the name" check is kept, so nothing that matched before stops matching.</summary>
    public static bool Names(string text, string name) =>
        text.Contains(name, StringComparison.OrdinalIgnoreCase) || Find(text, name) != null;

    /// <summary>The line with a misheard name put right ("Rutax, what's the" -> "Vertex, what's the"), so the language model never sees a stranger's name.</summary>
    public static string Normalize(string text, string name)
    {
        if (text.Contains(name, StringComparison.OrdinalIgnoreCase)) return text;
        return Find(text, name) is { } hit ? string.Concat(text.AsSpan(0, hit.Start), name, text.AsSpan(hit.Start + hit.Length)) : text;
    }

    private static string Letters(MatchCollection tokens, int first, int span)
    {
        var sb = new StringBuilder();
        for (var k = first; k < first + span; k++) foreach (var c in tokens[k].Value) if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static (int Start, int Length) Range(MatchCollection tokens, int first, int span)
    {
        var start = tokens[first].Index;
        var last = tokens[first + span - 1];
        return (start, last.Index + last.Length - start);
    }
}

/// <summary>Does the transcript read as a sentence that stopped partway? The recogniser ends a sentence it believes finished with . ? or !.</summary>
public static class TurnText
{
    /// <summary>
    /// A line of at least two words with no closing punctuation ("Rutax, what's the") or ending on a comma, dash or ellipsis. MEASURED on the live
    /// session: every complete sentence came back with its full stop, the cut-off ones did not.
    /// </summary>
    public static bool LooksUnfinished(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return false;
        if (t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) return false;
        var last = t[^1];
        if (last is '.' or '?' or '!') return t.EndsWith("...") || t.EndsWith("…");
        return true;
    }
}
