using System.Text.Json;
using SpawnDev.AI;

namespace Vertex.Brain;

/// <summary>
/// What the model was shown and what it wrote, pass by pass, as training pairs for an adapter of his own (Tuvok's format, 2026-11-04, for the training work in SpawnDev.ILGPU.ML). One JSON object per line under the data folder, a file a day;
/// nothing is sent anywhere. The strings are the engine's own (AiChatEngine.PassObserver), so training sees exactly what inference does: a template mismatch between the two is the commonest way for an adapter to do nothing.
/// <para>
/// Only passes with nobody's words in them: <see cref="Allowed"/> refuses any pass holding something a person said or a fact about a person. The privacy page promises that chat is not logged, and an adapter is a copy of what it was trained on.
/// </para>
/// </summary>
public sealed class TrainingLog(string folder, string? source = null)
{
    /// <summary>
    /// On every line, so the trainer can tell old lines from new. 2: rendered_prompt and rendered_target from the engine; a tool call is in rendered_target as the model wrote it; the target does not include the end-of-turn token, the formatter adds it.
    /// 3: also prompt_ids, the token ids the model was given (the end that was kept, when prompt_cut), so training does not have to tokenize the text again and differ from inference. target_ids is null.
    /// 4: target_ids, what the model generated, with the ending stop token excluded, and end_of_turn_ids, the template's stop ids. Training input = prompt_ids + target_ids + one of end_of_turn_ids.
    /// </summary>
    public const int FormatVersion = 4;

    /// <summary>The ids the model really saw: the decoder keeps the newest tokens when the prompt does not fit its context.</summary>
    public static int[] KeptIds(int[] ids, int promptTokens) => promptTokens > 0 && promptTokens < ids.Length ? ids[^promptTokens..] : ids;

    private readonly object _lock = new();
    private long _next;

    /// <summary>One run of the program: the unit a split goes by, so a held-out pass shares no session with a training one.</summary>
    public string Source { get; } = source ?? $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..23];

    /// <summary>One session in five is held out, decided by its name alone so every line of a session lands on the same side.</summary>
    public static string SplitOf(string source)
    {
        var h = 0u;
        foreach (var c in source) h = (h * 31) + c;
        return h % 5 == 0 ? "heldout" : "train";
    }

    /// <summary>
    /// No person in it. A line a friend said is a user line not in brackets (game updates begin with one); what he knows about a person is an "About Aubri: ..." line in the state, and the people in his history are there as friends' lines.
    /// </summary>
    public static bool Allowed(IReadOnlyList<AiChatMessage> messages) =>
        messages.All(m => !(m.Role == "user" && !m.Content.TrimStart().StartsWith('['))) && messages.All(m => !m.Content.Contains("\n- About ", StringComparison.Ordinal) && !m.Content.StartsWith("- About ", StringComparison.Ordinal));

    /// <summary>The facts the turn was given to report, from the bracket in its last line: what an honest answer has to match. Null when there is no bracket.</summary>
    public static string? TruthIn(IReadOnlyList<AiChatMessage> messages)
    {
        var last = messages.LastOrDefault(m => m.Role == "user")?.Content.Trim();
        if (last == null || !last.StartsWith('[')) return null;
        var end = last.IndexOf(']');
        return end > 0 ? last[1..end] : last[1..];
    }

    /// <summary>Writes one pass, if it may be written. A disk that cannot take it costs the line, never the game.</summary>
    /// <param name="promptCut">The decoder dropped the oldest tokens to fit its context, so the rendered prompt is longer than what the model saw.</param>
    /// <param name="promptIds">The token ids the model was given.</param>
    /// <param name="targetIds">The token ids the model generated.</param>
    /// <param name="endOfTurnIds">The ids that end a turn.</param>
    /// <param name="adapter">The adapter that produced this reply, or null for the plain model. Replies from an adapter are its own output: a later training run must not take them for a teacher's, or it would only be learning from itself.</param>
    /// <param name="endToken">The token that ended the reply.</param>
    public bool Record(IReadOnlyList<AiChatMessage> messages, IReadOnlyList<string>? toolsJson, string rawOutput, string renderedPrompt, bool promptCut = false, int[]? promptIds = null, IReadOnlyList<int>? targetIds = null, IReadOnlyList<int>? endOfTurnIds = null, string? adapter = null, int? endToken = null)
    {
        if (!Allowed(messages)) return false;
        var sent = messages.Select(m => new { role = m.Role, content = m.Content }).ToList();
        var line = JsonSerializer.Serialize(new
        {
            format = FormatVersion,
            id = $"{Source}-{Interlocked.Increment(ref _next)}",
            split = SplitOf(Source),
            messages = sent.Append(new { role = "assistant", content = rawOutput }).ToList(),
            tools = toolsJson ?? [],
            target_turn = sent.Count,
            truth = TruthIn(messages),
            source = Source,
            rendered_prompt = renderedPrompt,
            rendered_target = rawOutput,
            prompt_ids = promptIds,
            target_ids = targetIds,
            end_of_turn_ids = endOfTurnIds,
            end_token_id = endToken,
            adapter,
            prompt_cut = promptCut,
        });
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, $"turns-{DateTime.UtcNow:yyyy-MM-dd}.jsonl"), line + "\n");
            }
            return true;
        }
        catch (IOException) { return false; }
    }
}
