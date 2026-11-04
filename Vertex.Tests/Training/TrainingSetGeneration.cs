using System.Text.Json;
using SpawnDev.AI;
using SpawnDev.AI.Server;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Core;
using Vertex.Tests.Live;

namespace Vertex.Tests.Training;

/// <summary>
/// Makes the training sets for the adapter work (Tuvok's spec, 2026-11-04), through the real prompt path. Run by hand with Vertex stopped: it needs the GPU. Output goes to VERTEX_TRAINING_OUT, not the repo (the lines are long).
/// <para>
/// Synthetic: DISTILLED. The base model with the full tool list is the teacher (93/100 on the strict score); each request's reply is kept only if it passes that request's own strict check, and failures are dropped and counted, never hand-fixed. Then
/// the same messages are rendered with tool names only: that is what the adapter will see at inference, so that is the line's rendered_prompt, with the teacher's exact output as rendered_target.
/// </para>
/// <para>Honesty: real game-event turns from his own log, with the target replaced by the sentence true to the event (<see cref="HonestyScoring"/>), so the adapter does not learn his slips.</para>
/// </summary>
[Category("Training")]
[Explicit("Makes the training sets: run by hand with Vertex stopped")]
[NonParallelizable]
public class TrainingSetGeneration
{
    private static string Output => Environment.GetEnvironmentVariable("VERTEX_TRAINING_OUT") ?? throw new InvalidOperationException("set VERTEX_TRAINING_OUT");
    private static readonly AiGenerationOptions Production = new() { MaxOutputTokens = 220, Strategy = "top_p", Temperature = 0.7f, TopP = 0.9f, RepetitionPenalty = 1.1f };

    private sealed record Kept(string Id, string Split, ToolRequest Request, List<AiChatMessage> Messages, string Target);

    [Test]
    public async Task Synthetic()
    {
        Directory.CreateDirectory(Output);
        var ai = TestAi.Create();
        await using var _ai = ai;
        var model = TestModel.Pick(await ai.ModelNamesAsync());
        await ai.EnsureChatReadyAsync(model);
        var folder = Path.Combine(Path.GetTempPath(), $"vertex-synth-{Guid.NewGuid():N}");
        var store = new CharacterStore(Path.Combine(folder, "characters"));
        var client = new MinecraftClient(GameProfile.Offline("Vertex"));
        await using var brain = new VertexBrain(client, null, ai, store, Character.BuiltIn[0], model);
        var system = brain.Companion.SystemPrompt();
        var limit = int.TryParse(Environment.GetEnvironmentVariable("VERTEX_SYNTH_LIMIT"), out var n) ? n : int.MaxValue;
        var random = new Random(20261104);
        var requests = SyntheticToolRequests.All().OrderBy(_ => random.Next()).Take(limit).ToList();
        ai.Chat.MaxToolRounds = 0;     // a call comes back unexecuted: this is about what he would call, and nothing here may touch a world

        // The teacher: the full tool list, as he plays.
        GameTool.NamesOnly = false;
        var kept = new List<Kept>();
        var dropped = new Dictionary<string, int>();
        var total = new Dictionary<string, int>();
        AiChatPass? pass = null;
        ai.Chat.PassObserver = p => pass = p;
        var index = 0;
        foreach (var request in requests)
        {
            var key = request.Tool ?? "(no tool)";
            total[key] = total.GetValueOrDefault(key) + 1;
            var spoken = SyntheticToolRequests.Spoken(request, random);
            var messages = new List<AiChatMessage> { new("system", system), new("system", SyntheticToolRequests.StateNote(random)) };
            if (Companion.InstinctHint(spoken) is { } hint) messages.Add(new("system", hint));
            messages.Add(new("user", spoken));
            pass = null;
            var result = await ai.ChatAsync(new AiChatRequest { Model = model, Messages = messages, Options = Production }, null, default);
            var call = result.ToolCalls.FirstOrDefault();
            var ok = request.Tool == null
                ? call == null && result.TextWithoutToolCalls.Count(char.IsLetter) >= 4
                : call != null && call.Name == request.Tool && ArgsOk(request, call.ArgumentsJson);
            index++;
            if (ok && pass != null) kept.Add(new Kept($"synthetic-{index}", random.Next(5) == 0 ? "dev" : "train", request, messages, pass.RawOutput));
            else dropped[key] = dropped.GetValueOrDefault(key) + 1;
            if (index % 50 == 0) TestContext.Out.WriteLine($"{index} of {requests.Count}, kept {kept.Count}");
        }

        // The student's prompt: the same messages with tool names only. One token is enough: only the rendering is wanted.
        GameTool.NamesOnly = true;
        var file = Path.Combine(Output, "synthetic-tool-choice.jsonl");
        await using (var writer = new StreamWriter(file))
            foreach (var k in kept)
            {
                pass = null;
                await ai.ChatAsync(new AiChatRequest { Model = model, Messages = k.Messages, Options = new AiGenerationOptions { MaxOutputTokens = 1, Strategy = "greedy" } }, null, default);
                if (pass == null) throw new InvalidOperationException("the engine reported no pass");
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    format = TrainingLog.FormatVersion, id = k.Id, split = k.Split,
                    messages = k.Messages.Select(m => new { role = m.Role, content = m.Content }).Append(new { role = "assistant", content = k.Target }).ToList(),
                    tools = pass.ToolsJson ?? [], target_turn = k.Messages.Count, truth = (string?)null, source = "synthetic-v1",
                    rendered_prompt = pass.RenderedPrompt, rendered_target = k.Target, prompt_ids = TrainingLog.KeptIds(pass.PromptTokenIds, pass.PromptTokens), target_ids = (int[]?)null,
                    prompt_cut = pass.PromptTokens < pass.PromptTokenIds.Length, expected_tool = k.Request.Tool, says = k.Request.Says,
                }));
            }
        GameTool.NamesOnly = false;
        ai.Chat.PassObserver = null;

        var report = new
        {
            model, requests = requests.Count, kept = kept.Count, dropped = requests.Count - kept.Count, drop_rate = Math.Round((requests.Count - kept.Count) / (double)requests.Count, 3),
            per_tool = total.OrderBy(k => k.Key).Select(k => new { tool = k.Key, total = k.Value, dropped = dropped.GetValueOrDefault(k.Key) }),
        };
        var text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(Output, "synthetic-tool-choice.report.json"), text);
        TestContext.Out.WriteLine(text);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Assert.That(kept.Count, Is.GreaterThan(0));
    }

    private static bool ArgsOk(ToolRequest request, string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return request.ArgsOk(doc.RootElement);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The honesty set: the real turns in his log with an event it knows, target corrected. No GPU, and Vertex may be running.</summary>
    [Test]
    public async Task Honesty()
    {
        Directory.CreateDirectory(Output);
        var from = Environment.GetEnvironmentVariable("VERTEX_TRAINING_IN") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vertex", "training");
        int read = 0, unknown = 0, honest = 0, written = 0;
        await using var writer = new StreamWriter(Path.Combine(Output, "honesty.jsonl"));
        foreach (var file in Directory.GetFiles(from, "turns-*.jsonl").OrderBy(f => f))
            foreach (var line in File.ReadLines(file))
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                read++;
                var truth = r.GetProperty("truth").GetString();
                var said = r.GetProperty("rendered_target").GetString();
                if (HonestyScoring.Correct(truth) is not { } corrected) { unknown++; continue; }
                var wasHonest = HonestyScoring.Honest(truth!, said);
                if (wasHonest) honest++;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    format = r.GetProperty("format").GetInt32(), id = r.GetProperty("id").GetString(), split = r.GetProperty("split").GetString(), truth, source = r.GetProperty("source").GetString(),
                    rendered_prompt = r.GetProperty("rendered_prompt").GetString(), rendered_target = corrected, said_live = said, said_live_honest = wasHonest, prompt_cut = r.GetProperty("prompt_cut").GetBoolean(),
                    prompt_ids = r.TryGetProperty("prompt_ids", out var ids) && ids.ValueKind == JsonValueKind.Array ? ids.EnumerateArray().Select(i => i.GetInt32()).ToArray() : null,
                }));
                written++;
            }
        TestContext.Out.WriteLine($"read {read}, no known event {unknown}, written {written}; what he really said was honest {honest} of {written}");
        Assert.That(read, Is.GreaterThan(0));
    }
}
