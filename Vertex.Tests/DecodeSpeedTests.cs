using System.Diagnostics;
using SpawnDev.AI;
using Vertex.AI;

namespace Vertex.Tests;

/// <summary>
/// How fast does the language model write, and what slows it? Measured 2026-10-16: Vertex's turns decode at about 4.7 tokens per second (ToolChoiceTests timing lines), while the engine alone has
/// been measured at 50 to 88 tokens per second on this GPU. This tries the sampling settings one at a time on a short prompt with no tools.
/// </summary>
[Category("GPU")]
[Explicit("a benchmark: needs the GPU to itself")]
[NonParallelizable]
public class DecodeSpeedTests
{
    private VertexAi _ai = null!;
    private string _model = "";

    [OneTimeSetUp]
    public async Task Start()
    {
        _ai = TestAi.Create();
        var models = await _ai.ModelNamesAsync();
        _model = models.FirstOrDefault(m => m.StartsWith(Environment.GetEnvironmentVariable("VERTEX_TEST_MODEL") ?? "llama3.1")) ?? throw new AssertionException("model not found");
        await _ai.EnsureChatReadyAsync(_model);
    }

    [OneTimeTearDown]
    public async Task Stop() { if (_ai != null) await _ai.DisposeAsync(); }

    private async Task<(double TokensPerSecond, int Tokens, double Seconds)> RunAsync(AiGenerationOptions options, string label)
    {
        var request = new AiChatRequest
        {
            Model = _model,
            Messages = [new("system", "You are a friendly storyteller."), new("user", "Tell me a story about a small dragon who learns to fly. Use about eighty words.")],
            Options = options,
        };
        var sw = Stopwatch.StartNew();
        var first = -1.0;
        var result = await _ai.ChatAsync(request, _ => { if (first < 0) first = sw.Elapsed.TotalSeconds; return Task.CompletedTask; }, CancellationToken.None);
        sw.Stop();
        var decode = sw.Elapsed.TotalSeconds - Math.Max(first, 0);
        var rate = result.GeneratedTokens / Math.Max(decode, 1e-6);
        TestContext.Out.WriteLine($"{label,-34} {result.GeneratedTokens,3} tokens, first token {first:0.00} s, total {sw.Elapsed.TotalSeconds:0.00} s, decode {rate:0.0} tok/s (prompt {result.PromptTokens} tokens)");
        return (rate, result.GeneratedTokens, sw.Elapsed.TotalSeconds);
    }

    /// <summary>Where a decode step spends its time, by op type and by phase of the executor's node loop (GraphExecutor.OpProfile), for one greedy 60-token answer.</summary>
    [Test]
    public async Task WhereTheTimeGoes_OneGreedyAnswer()
    {
        await RunAsync(new AiGenerationOptions { MaxOutputTokens = 20 }, "warm-up");
        Array.Clear(SpawnDev.ILGPU.ML.Graph.GraphExecutor.OpPhaseMs);
        SpawnDev.ILGPU.ML.Graph.GraphExecutor.OpProfile = new Dictionary<string, SpawnDev.ILGPU.ML.Graph.GraphExecutor.OpProfileEntry>();
        var r = await RunAsync(new AiGenerationOptions { MaxOutputTokens = 60 }, "profiled");
        var prof = SpawnDev.ILGPU.ML.Graph.GraphExecutor.OpProfile;
        SpawnDev.ILGPU.ML.Graph.GraphExecutor.OpProfile = null;
        var tokens = Math.Max(r.Tokens, 1);
        TestContext.Out.WriteLine($"per token: total wall {r.Seconds * 1000 / tokens:0.0} ms");
        foreach (var kv in prof.OrderByDescending(k => k.Value.WallMs).Take(14))
            TestContext.Out.WriteLine($"  {kv.Key,-24} {kv.Value.WallMs / tokens,8:0.00} ms/tok  x{kv.Value.Count / tokens,4}/tok  dispatches {kv.Value.Dispatches / tokens,4}/tok  readbacks {kv.Value.Readbacks / (double)tokens:0.0}/tok  drains {kv.Value.Drains / (double)tokens:0.0}/tok  alloc {kv.Value.AllocBytes / tokens / 1024} KB/tok");
        for (var i = 0; i < SpawnDev.ILGPU.ML.Graph.GraphExecutor.OpPhaseNames.Length; i++)
            TestContext.Out.WriteLine($"  phase {SpawnDev.ILGPU.ML.Graph.GraphExecutor.OpPhaseNames[i],-16} {SpawnDev.ILGPU.ML.Graph.GraphExecutor.OpPhaseMs[i] / tokens:0.00} ms/tok");
    }

    /// <summary>The cost of a LONG context (Vertex's prompt is about 4,000 tokens): the first prefill, a second turn on the same prefix (what each chat turn is), and the decode rate at that length.</summary>
    [Test]
    public async Task LongContext_PrefillAndDecode()
    {
        var filler = string.Join(" ", Enumerable.Range(0, 250).Select(i => $"Fact {i}: the village has {i % 17 + 3} houses, {i % 5 + 1} wells and {i % 11 + 2} gardens."));
        async Task<(double First, double Decode, int Prompt, int Tokens)> Turn(string user)
        {
            var request = new AiChatRequest
            {
                Model = _model,
                Messages = [new("system", "You are a friendly helper. Here is what you know about the village. " + filler), new("user", user)],
                Options = new AiGenerationOptions { MaxOutputTokens = 40 },
            };
            var sw = Stopwatch.StartNew();
            var first = -1.0;
            var result = await _ai.ChatAsync(request, _ => { if (first < 0) first = sw.Elapsed.TotalSeconds; return Task.CompletedTask; }, CancellationToken.None);
            var decode = result.GeneratedTokens / Math.Max(sw.Elapsed.TotalSeconds - Math.Max(first, 0), 1e-6);
            return (first, decode, result.PromptTokens, result.GeneratedTokens);
        }
        var a = await Turn("Say hello to the village in one sentence.");
        TestContext.Out.WriteLine($"first turn : prompt {a.Prompt} tokens, first token {a.First:0.00} s, decode {a.Decode:0.0} tok/s");
        var b = await Turn("Now say goodbye to the village in one sentence.");
        TestContext.Out.WriteLine($"second turn: prompt {b.Prompt} tokens, first token {b.First:0.00} s, decode {b.Decode:0.0} tok/s");
        var c = await Turn("Tell me one thing about house number three.");
        TestContext.Out.WriteLine($"third turn : prompt {c.Prompt} tokens, first token {c.First:0.00} s, decode {c.Decode:0.0} tok/s");
    }

    [Test]
    public async Task DecodeRate_ByTheSamplingSettings()
    {
        await RunAsync(new AiGenerationOptions { MaxOutputTokens = 80 }, "warm-up (greedy)");
        var greedy = await RunAsync(new AiGenerationOptions { MaxOutputTokens = 80, Strategy = "greedy" }, "greedy");
        var topP = await RunAsync(new AiGenerationOptions { MaxOutputTokens = 80, Strategy = "top_p", Temperature = 0.7f, TopP = 0.9f }, "top_p 0.9, no penalty");
        var topK = await RunAsync(new AiGenerationOptions { MaxOutputTokens = 80, Strategy = "top_k", Temperature = 0.7f, TopK = 40 }, "top_k 40, no penalty");
        var vertex = await RunAsync(new AiGenerationOptions { MaxOutputTokens = 80, Strategy = "top_p", Temperature = 0.7f, TopP = 0.9f, RepetitionPenalty = 1.1f }, "top_p 0.9 + penalty 1.1 (Vertex)");
        var greedyPenalty = await RunAsync(new AiGenerationOptions { MaxOutputTokens = 80, Strategy = "greedy", RepetitionPenalty = 1.1f }, "greedy + penalty 1.1");
        TestContext.Out.WriteLine($"summary: greedy {greedy.TokensPerSecond:0.0}, top_p {topP.TokensPerSecond:0.0}, top_k {topK.TokensPerSecond:0.0}, Vertex {vertex.TokensPerSecond:0.0}, greedy+penalty {greedyPenalty.TokensPerSecond:0.0} tokens per second");
    }
}
