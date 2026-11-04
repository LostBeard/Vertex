using System.Text.Json;

namespace Vertex.Brain;

/// <summary>What he can do about monsters.</summary>
public enum DangerAction { Fight, Flee, DigIn, SwimOut }

/// <summary>The situation as far as the choice is concerned. Few values on purpose: each life gives only a few dozen decisions, so what is learned has to be shared between many situations.</summary>
/// <param name="Foe">"melee", "archer", "swarm" (three or more close) or "creeper".</param>
/// <param name="Kit">"bare", "weak" (wood or gold) or "good" (stone or better).</param>
/// <param name="Health">"low" (under 7 points), "mid" or "high" (14 and up).</param>
public sealed record DangerSituation(string Foe, string Kit, string Health)
{
    public static string KitOf(string? bestWeapon) => bestWeapon == null ? "bare" : bestWeapon.Contains("wooden") || bestWeapon.Contains("golden") ? "weak" : "good";
    public static string HealthOf(float health) => health < 7 ? "low" : health < 14 ? "mid" : "high";
    public override string ToString() => $"{Foe}, {Kit}, {Health} health";
}

/// <summary>How one choice turned out, as stored.</summary>
public sealed record DangerRecord(DateTime At, string Foe, string Kit, string Health, DangerAction Action, double Reward, bool Died, float HealthBefore, float HealthAfter, double Seconds);

/// <summary>
/// Choosing what to do about monsters from what has actually happened to him (TJ, 2026-11-03: "figure out how to make him learn so he actually plays the game and not just runs stupid scripts"). A Thompson-sampling
/// bandit: each action has a belief about how well it goes, starting from a small prior a player would give it, and every outcome moves the belief. He samples from each belief and takes the best sample, so what
/// has worked gets chosen more and what is unproven still gets tried. Beliefs are kept at three levels (exact situation; same foe and kit; same foe) and pooled, weighted to the exact situation, which is what makes
/// a few dozen decisions enough; the sample-hungry alternatives (world models, deep RL) cannot learn on one life at a time. The priors are small (<see cref="PriorWeight"/> outcomes' worth), so a few bad results
/// overturn them. Rows go to a JSONL file: it survives restarts, and everything he says about what he learned can be counted from it. No game in it: tested with scripted outcomes.
/// </summary>
public sealed class DangerLearner
{
    /// <summary>How many outcomes the prior counts as.</summary>
    public const double PriorWeight = 4;
    private const double ExactWeight = 1, FoeKitWeight = 0.5, FoeWeight = 0.2;

    private readonly Dictionary<(string Level, DangerAction Action), (double Sum, double Count)> _stats = [];
    private readonly string? _file;
    private readonly Random _random;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public DangerLearner(string? file = null, Random? random = null)
    {
        _file = file;
        _random = random ?? new Random();
        if (file == null || !File.Exists(file)) return;
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<DangerRecord>(line, Json) is { } r) Add(new DangerSituation(r.Foe, r.Kit, r.Health), r.Action, r.Reward); }
            catch (JsonException) { /* a torn last line after a crash loses one outcome, not all of them */ }
        }
    }

    /// <summary>How many outcomes he has learned from.</summary>
    public int Outcomes { get { lock (_lock) return (int)_stats.Where(k => k.Key.Level.Split('/').Length == 3).Sum(k => k.Value.Count); } }

    /// <summary>What a player would expect before trying: how likely each action is to go well.</summary>
    public static double Prior(DangerSituation s, DangerAction a)
    {
        var p = (s.Foe, a) switch
        {
            ("melee", DangerAction.Fight) => s.Kit switch { "good" => 0.8, "weak" => 0.65, _ => 0.35 },
            ("melee", DangerAction.Flee) => 0.5,
            ("melee", _) => 0.5,
            ("archer", DangerAction.Fight) => s.Kit switch { "good" => 0.6, "weak" => 0.45, _ => 0.25 },
            ("archer", DangerAction.Flee) => 0.4,
            ("archer", _) => 0.6,
            ("swarm", DangerAction.Fight) => s.Kit == "good" ? 0.4 : 0.15,
            ("swarm", DangerAction.Flee) => 0.45,
            ("swarm", _) => 0.6,
            ("creeper", DangerAction.Fight) => 0.1,
            ("creeper", DangerAction.Flee) => 0.8,
            _ => 0.5,
        };
        if (a == DangerAction.Fight && s.Health == "low") p = Math.Max(0.05, p - 0.25);
        return p;
    }

    private static string[] Levels(DangerSituation s) => [$"{s.Foe}/{s.Kit}/{s.Health}", $"{s.Foe}/{s.Kit}", s.Foe];

    private void Add(DangerSituation s, DangerAction a, double reward)
    {
        foreach (var level in Levels(s))
        {
            var (sum, count) = _stats.GetValueOrDefault((level, a));
            _stats[(level, a)] = (sum + reward, count + 1);
        }
    }

    /// <summary>The belief about an action: the two numbers of a Beta distribution, prior plus pooled outcomes.</summary>
    public (double Alpha, double Beta) Belief(DangerSituation s, DangerAction a)
    {
        var prior = Prior(s, a);
        double alpha = prior * PriorWeight, beta = (1 - prior) * PriorWeight;
        var levels = Levels(s);
        double[] weights = [ExactWeight, FoeKitWeight, FoeWeight];
        lock (_lock)
            for (var i = 0; i < levels.Length; i++)
            {
                var (sum, count) = _stats.GetValueOrDefault((levels[i], a));
                alpha += weights[i] * sum;
                beta += weights[i] * (count - sum);
            }
        return (alpha, beta);
    }

    /// <summary>The expected result of an action, 0 to 1: what the belief comes to, without the sampling.</summary>
    public double Expected(DangerSituation s, DangerAction a)
    {
        var (alpha, beta) = Belief(s, a);
        return alpha / (alpha + beta);
    }

    /// <summary>Samples each belief and takes the best sample.</summary>
    public DangerAction Choose(DangerSituation s, IReadOnlyCollection<DangerAction> legal)
    {
        if (legal.Count == 0) throw new ArgumentException("nothing to choose from", nameof(legal));
        var best = legal.First();
        var bestSample = double.MinValue;
        foreach (var a in legal)
        {
            var (alpha, beta) = Belief(s, a);
            double sample;
            lock (_lock) sample = SampleBeta(alpha, beta);
            if (sample > bestSample) { bestSample = sample; best = a; }
        }
        return best;
    }

    /// <summary>
    /// How a choice went, 0 to 1. Dying is 0. Otherwise the share of his health he kept, cut when the monster is still on him (running from it and being caught is not the same as being rid of it), and halved when
    /// the job itself failed.
    /// </summary>
    public static double Reward(bool died, float before, float after, bool foeStillClose, bool jobWorked)
    {
        if (died) return 0;
        var kept = before <= 0 ? 0 : Math.Clamp(after / before, 0, 1);
        var r = (double)kept * kept;
        if (foeStillClose) r *= 0.6;
        if (!jobWorked) r *= 0.5;
        return Math.Clamp(r, 0, 1);
    }

    public void Record(DangerSituation s, DangerAction a, double reward, bool died, float before, float after, double seconds)
    {
        var row = new DangerRecord(DateTime.UtcNow, s.Foe, s.Kit, s.Health, a, reward, died, before, after, Math.Round(seconds, 1));
        lock (_lock)
        {
            Add(s, a, reward);
            if (_file == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.AppendAllText(_file, JsonSerializer.Serialize(row, Json) + "\n");
            }
            catch (IOException) { /* a full or locked disk costs the record of this outcome, never the game */ }
        }
    }

    /// <summary>What he has found out, in his words, counted from the outcomes: the best and worst of what he has really tried. Empty until he has tried things.</summary>
    public string Summary(int most = 4)
    {
        List<(string Foe, string Kit, DangerAction Action, double Mean, int Count)> rows;
        lock (_lock)
            rows = _stats.Where(k => k.Key.Level.Split('/').Length == 2 && k.Value.Count >= 3)
                .Select(k => (Foe: k.Key.Level.Split('/')[0], Kit: k.Key.Level.Split('/')[1], k.Key.Action, Mean: k.Value.Sum / k.Value.Count, Count: (int)k.Value.Count)).ToList();
        return string.Join(" ", rows.OrderByDescending(r => r.Count).Take(most).Select(r => $"Against {Foe(r.Foe)} with {Kit(r.Kit)}, {Doing(r.Action)} has gone {(r.Mean >= 0.6 ? "well" : r.Mean >= 0.3 ? "so-so" : "badly")} ({r.Count} tries)."));
    }

    public static string Doing(DangerAction a) => a switch { DangerAction.Fight => "fighting", DangerAction.Flee => "running away", DangerAction.DigIn => "digging in", _ => "swimming out to open water" };
    private static string Foe(string f) => f switch { "melee" => "a monster", "archer" => "an archer", "swarm" => "a crowd of monsters", _ => "a creeper" };
    private static string Kit(string k) => k switch { "bare" => "bare hands", "weak" => "a poor weapon", _ => "a good weapon" };

    private double SampleBeta(double alpha, double beta)
    {
        var x = SampleGamma(alpha);
        var y = SampleGamma(beta);
        return x + y <= 0 ? 0.5 : x / (x + y);
    }

    /// <summary>Marsaglia and Tsang's method; shapes under 1 by the usual boost.</summary>
    private double SampleGamma(double shape)
    {
        if (shape < 1) return SampleGamma(shape + 1) * Math.Pow(Math.Max(_random.NextDouble(), 1e-12), 1 / shape);
        var d = shape - 1.0 / 3;
        var c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double x, v;
            do
            {
                var u1 = Math.Max(_random.NextDouble(), 1e-12);
                var u2 = _random.NextDouble();
                x = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                v = 1 + c * x;
            } while (v <= 0);
            v = v * v * v;
            var u = Math.Max(_random.NextDouble(), 1e-12);
            if (Math.Log(u) < 0.5 * x * x + d - d * v + d * Math.Log(v)) return d * v;
        }
    }
}
