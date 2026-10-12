using System.Diagnostics;
using ILGPU.Runtime;
using SpawnDev.ILGPU.ML;
using SpawnDev.ILGPU.ML.Hub;
using SpawnDev.ILGPU.ML.Pipelines;
using SpawnDev.ILGPU.ML.Preprocessing;

namespace Vertex.AI;

/// <summary>
/// "System One" for what players say: a sentence-embedding model (all-MiniLM-L6-v2 on the GPU through
/// SpawnDev.ILGPU.ML) matches a request against example phrasings of the urgent, argument-free commands - stop,
/// follow me, come here, protect me... - in milliseconds, so Vertex acts at once instead of after the language
/// model has thought it over. Anything it is not sure about (or that is negated: "don't follow me") goes to the
/// language model as before.
/// </summary>
public sealed class FastIntents : IDisposable
{
    /// <summary>A command the fast path can run: the game tool, whether it is about the speaker, and how people say it.</summary>
    public sealed record Intent(string Tool, bool AboutSpeaker, string[] Examples);

    public static readonly Intent[] Known =
    [
        new("stop", false, ["stop", "stop it", "freeze", "halt", "stop what you're doing", "stop moving", "wait, stop", "stand still", "hold it"]),
        new("follow_player", true, ["follow me", "come with me", "follow along", "come along with me", "keep up with me", "stay with me", "let's go, follow me"]),
        new("come_here", true, ["come here", "come over here", "get over here", "come to me", "come back here", "over here", "where are you, come here"]),
        new("guard", true, ["protect me", "guard me", "keep me safe", "defend me", "watch my back", "help me fight", "save me from the monsters"]),
        new("sleep", false, ["go to bed", "go to sleep", "time for bed", "sleep in the bed", "it's night, go to bed"]),
        new("eat", false, ["eat something", "eat some food", "you should eat", "have something to eat", "you're hungry, eat"]),
    ];

    /// <summary>Below this cosine similarity to the best example, it is not that command.</summary>
    public float Threshold { get; set; } = 0.75f;
    /// <summary>The best command must beat the runner-up command by this much.</summary>
    public float Margin { get; set; } = 0.06f;

    private const int MaxTokens = 32;
    private readonly InferenceSession _session;
    private readonly FeatureExtractionPipeline _pipeline;
    private readonly LoadedTokenizer _tokenizer;
    private readonly List<(Intent Intent, float[] Embedding)> _examples = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    // The process-wide GPU lane (null in tests that run the matcher alone): matching enters it as urgent work.
    private readonly GpuLane? _lane;

    private FastIntents(InferenceSession session, LoadedTokenizer tokenizer, Accelerator accelerator, GpuLane? lane)
    {
        _lane = lane;
        _session = session;
        _tokenizer = tokenizer;
        _pipeline = new FeatureExtractionPipeline(session, accelerator, maxLength: MaxTokens, hiddenSize: 384);
    }

    /// <param name="lane">The GPU lane MatchAsync enters. The caller of LoadAsync must already hold it (loading embeds
    /// the examples without entering it again).</param>
    public static async Task<FastIntents> LoadAsync(IModelSource source, Accelerator accelerator, GpuLane? lane = null, CancellationToken ct = default)
    {
        const string repo = ModelHub.KnownModels.AllMiniLmL6V2;
        string tokenizerJson;
        await using (var t = await source.OpenAsync(repo, "tokenizer.json", ct))
        using (var reader = new StreamReader(t))
            tokenizerJson = await reader.ReadToEndAsync(ct);
        var tokenizer = TokenizerLoader.FromTokenizerJson(tokenizerJson);
        InferenceSession session;
        // Needs SpawnDev.ILGPU.ML 5.3.5+ (via SpawnDev.AI.Server 1.1.0): earlier versions fused this BERT graph's
        // attention wrongly (dropped the mask, swapped K's heads and sequence).
        await using (var model = await source.OpenAsync(repo, "onnx/model.onnx", ct))
            session = await InferenceSession.CreateFromOnnxStreamAsync(accelerator, model, inputShapes: new Dictionary<string, int[]>
            {
                ["input_ids"] = [1, MaxTokens],
                ["attention_mask"] = [1, MaxTokens],
                ["token_type_ids"] = [1, MaxTokens],
            });
        var intents = new FastIntents(session, tokenizer, accelerator, lane);
        foreach (var intent in Known)
            foreach (var example in intent.Examples)
                intents._examples.Add((intent, await intents.EmbedAsync(example)));
        return intents;
    }

    private async Task<float[]> EmbedAsync(string text)
    {
        await _gate.WaitAsync();
        try { return (await _pipeline.EmbedAsync(text, _tokenizer)).Embedding; }
        finally { _gate.Release(); }
    }

    /// <summary>An embedding for matching: inside the GPU lane, ahead of a reply being generated.</summary>
    private async Task<float[]> EmbedInLaneAsync(string text)
    {
        if (_lane == null) return await EmbedAsync(text);
        using (await _lane.EnterAsync(urgent: true))
            return await EmbedAsync(text);
    }

    private static readonly string[] Negations = ["don't", "dont", "do not", "never", "not ", "no more", "stop following", "stop coming"];
    private static readonly string[] Fillers = ["hey", "ok", "okay", "please", "can you", "could you", "would you", "now", "right now", "go ahead and"];

    /// <summary>What a request was reduced to before matching (the name and politeness removed).</summary>
    public static string Clean(string text, string name)
    {
        var t = text.ToLowerInvariant().Replace(name.ToLowerInvariant(), " ").Replace("vertex", " ");
        t = new string(t.Select(c => char.IsLetterOrDigit(c) || c is '\'' or ' ' ? c : ' ').ToArray());
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var joined = " " + string.Join(' ', words) + " ";
        foreach (var f in Fillers) joined = joined.Replace(" " + f + " ", " ");
        return joined.Trim();
    }

    public sealed record Match(Intent Intent, float Score, float RunnerUp, string Cleaned, double Milliseconds);

    /// <summary>The command this request is, when the model is sure; null hands it to the language model.</summary>
    public async Task<Match?> MatchAsync(string text, string name)
    {
        var sw = Stopwatch.StartNew();
        var cleaned = Clean(text, name);
        if (cleaned.Length == 0 || cleaned.Split(' ').Length > 10) return null; // long sentences are conversation
        if (Negations.Any(n => (" " + cleaned + " ").Contains(" " + n))) return null;
        var e = await EmbedInLaneAsync(cleaned);
        var best = _examples.GroupBy(x => x.Intent)
            .Select(g => (Intent: g.Key, Score: g.Max(x => Dot(e, x.Embedding))))
            .OrderByDescending(x => x.Score).ToList();
        sw.Stop();
        var (top, runnerUp) = (best[0], best[1]);
        if (top.Score < Threshold || top.Score - runnerUp.Score < Margin) return null;
        // "stop following me" style: anything with "stop" in it is only ever the stop command here.
        if (top.Intent.Tool != "stop" && cleaned.Contains("stop")) return null;
        return new Match(top.Intent, top.Score, runnerUp.Score, cleaned, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Scores against every command, best first (for tests and tuning).</summary>
    public async Task<List<(string Tool, float Score)>> ScoresAsync(string text, string name)
    {
        var e = await EmbedInLaneAsync(Clean(text, name));
        return _examples.GroupBy(x => x.Intent.Tool).Select(g => (g.Key, g.Max(x => Dot(e, x.Embedding)))).OrderByDescending(x => x.Item2).ToList();
    }

    private static float Dot(float[] a, float[] b)
    {
        var s = 0f;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s; // both are L2-normalised: this is the cosine
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _session.Dispose();
    }
}
