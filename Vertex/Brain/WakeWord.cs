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

    /// <summary>Whether the line is only the name, perhaps with a greeting ("Vertex.", "Hey Vertex"): nothing has been asked yet.</summary>
    public static bool IsOnlyTheName(string text, string name)
    {
        var tokens = Words.Matches(Normalize(text, name)).Select(m => m.Value.ToLowerInvariant()).ToList();
        if (tokens.Count == 0 || tokens.Count > 3) return false;
        var target = name.ToLowerInvariant();
        return tokens.Contains(target) && tokens.All(t => t == target || t is "hey" or "hi" or "hello" or "ok" or "okay" or "yo");
    }

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
    /// <summary>
    /// Does this line leave the speaker with more to say? A question ends a turn; a statement ("We're gonna play Minecraft today.") is usually followed by what they actually
    /// want. Said after the name and a greeting are set aside: "Vertex, what's the best thing to build?" is a question, "Vertex, we're going to play Minecraft." is not.
    /// </summary>
    public static bool ExpectsMore(string text, string name)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.EndsWith('?')) return false;
        var words = System.Text.RegularExpressions.Regex.Matches(WakeWord.Normalize(t, name), @"[A-Za-z][A-Za-z']*").Select(m => m.Value.ToLowerInvariant()).ToList();
        var skip = 0;
        while (skip < words.Count && (words[skip] == name.ToLowerInvariant() || words[skip] is "hey" or "hi" or "hello" or "ok" or "okay" or "so" or "well" or "um" or "uh")) skip++;
        if (skip >= words.Count) return false;                                     // only the name: handled before this
        return !QuestionStarts.Contains(words[skip]);
    }

    /// <summary>The words a question or a request starts with: nothing further is expected after them.</summary>
    private static readonly HashSet<string> QuestionStarts =
    [
        "what", "what's", "whats", "why", "how", "how's", "who", "who's", "where", "where's", "when", "which", "can", "could", "will", "would", "do", "does", "did", "is", "are", "was", "were",
        "should", "shall", "may", "might", "have", "has", "tell", "show", "give", "please", "let's", "lets", "come", "go", "stop", "wait", "look", "help", "find", "build", "make", "get", "bring",
    ];

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

/// <summary>
/// The language model carrying on the transcript. Every message to it is "{name} says (out loud): {words}", and an 8B model will sometimes finish its reply by
/// writing the friend's NEXT line in that format. Live (2026-10-14): after "what's the best thing to build first?" it answered with
/// "Todd says (out loud): Yeah, I'm not really sure... Can you build a house that's like, a castle?" - words nobody said, spoken by the robot and
/// remembered as if heard.
/// </summary>
public static class SelfTalk
{
    private static readonly Regex FriendsLine = new(@"(?<![\p{L}'])[\p{L}][\p{L}' ]{0,30} says \((?:out loud|in chat)[^)]*\)\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Where in <paramref name="text"/> a made-up friend's line begins, or null.</summary>
    public static int? IndexOfFriendsLine(string text)
    {
        var m = FriendsLine.Match(text);
        return m.Success ? m.Index : null;
    }

    /// <summary>The reply up to a made-up friend's line (all of it when there is none; nothing when it starts with one).</summary>
    public static string CutFriendsLine(string reply) => IndexOfFriendsLine(reply) is { } at ? reply[..at].TrimEnd() : reply;
}
