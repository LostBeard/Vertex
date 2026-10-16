using System.Text.RegularExpressions;

namespace Vertex.Brain;

/// <summary>
/// System One's guess at which tool a request wants, in a few milliseconds and with no GPU: a bag of words and letter triples, hashed, into a small softmax classifier trained at start-up
/// on <see cref="InstinctData"/>. It never acts by itself; it hands the language model a hint ("your first instinct is build_shape") which the model may overrule. "chat" means the
/// instinct says it is not a request.
/// </summary>
public sealed class Instinct
{
    private const int Dim = 8192;
    private static readonly Regex Words = new(@"[a-z0-9']+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Skip = ["vertex", "vortex", "please", "can", "you", "could", "would", "will", "the", "a", "an", "to", "of", "my", "me", "i", "it", "that", "some"];

    private readonly string[] _classes;
    private readonly float[][] _weights;      // [class][Dim]
    private readonly float[] _bias;

    public Instinct(IReadOnlyDictionary<string, string[]>? data = null, int epochs = 60)
    {
        data ??= InstinctData.Phrases;
        _classes = data.Keys.ToArray();
        _weights = _classes.Select(_ => new float[Dim]).ToArray();
        _bias = new float[_classes.Length];
        var samples = new List<(int Class, (int Index, float Value)[] Features)>();
        for (var c = 0; c < _classes.Length; c++)
            foreach (var phrase in data[_classes[c]]) samples.Add((c, Features(phrase)));
        Train(samples, epochs);
    }

    public IReadOnlyList<string> Classes => _classes;

    private static int Hash(string s)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var ch in s) h = (h ^ ch) * 16777619u;
            return (int)(h % Dim);
        }
    }

    /// <summary>Unigrams and bigrams of the words that matter, and letter triples of every word (they survive a misspelling or a speech-to-text slip), L2-normalised.</summary>
    public static (int Index, float Value)[] Features(string text)
    {
        var words = Words.Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => !Skip.Contains(w)).ToList();
        var counts = new Dictionary<int, float>();
        void Add(string f, float v = 1f) { var i = Hash(f); counts[i] = counts.GetValueOrDefault(i) + v; }
        for (var i = 0; i < words.Count; i++)
        {
            Add("w:" + words[i], 2f);
            if (i + 1 < words.Count) Add("b:" + words[i] + " " + words[i + 1], 1.5f);
            var padded = "^" + words[i] + "$";
            for (var k = 0; k + 3 <= padded.Length; k++) Add("c:" + padded.Substring(k, 3), 0.5f);
        }
        var norm = MathF.Sqrt(counts.Values.Sum(v => v * v));
        if (norm == 0) return [];
        return counts.Select(kv => (kv.Key, kv.Value / norm)).ToArray();
    }

    private float[] Scores((int Index, float Value)[] f)
    {
        var s = new float[_classes.Length];
        for (var c = 0; c < s.Length; c++)
        {
            var sum = _bias[c];
            var w = _weights[c];
            foreach (var (i, v) in f) sum += w[i] * v;
            s[c] = sum;
        }
        return s;
    }

    private static float[] Softmax(float[] s)
    {
        var max = s.Max();
        var e = s.Select(x => MathF.Exp(x - max)).ToArray();
        var sum = e.Sum();
        return e.Select(x => x / sum).ToArray();
    }

    private void Train(List<(int Class, (int Index, float Value)[] Features)> samples, int epochs)
    {
        var rng = new Random(20261015);
        const float rate = 0.9f, decay = 1e-4f;
        for (var epoch = 0; epoch < epochs; epoch++)
        {
            foreach (var (cls, f) in samples.OrderBy(_ => rng.Next()))
            {
                if (f.Length == 0) continue;
                var p = Softmax(Scores(f));
                for (var c = 0; c < _classes.Length; c++)
                {
                    var g = p[c] - (c == cls ? 1f : 0f);
                    if (MathF.Abs(g) < 1e-4f) continue;
                    var w = _weights[c];
                    foreach (var (i, v) in f) w[i] -= rate * (g * v + decay * w[i]);
                    _bias[c] -= rate * g * 0.1f;
                }
            }
        }
    }

    /// <summary>The most likely tool (or "chat") and how sure the instinct is, 0 to 1.</summary>
    public (string Tool, float Confidence) Guess(string text)
    {
        var f = Features(text);
        if (f.Length == 0) return ("chat", 0f);
        var p = Softmax(Scores(f));
        var best = Array.IndexOf(p, p.Max());
        return (_classes[best], p[best]);
    }
}
