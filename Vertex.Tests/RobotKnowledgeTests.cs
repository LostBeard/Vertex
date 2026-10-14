using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Vertex.Brain;
using Vertex.Core;
using Vertex.Reachy;
using SpawnDev.AI;
using Vertex.AI;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Does the robot answer an ordinary question? Live (2026-10-14) "What is the capital of France?" got "I don't know that one. I can look it up on the
/// computer, though!" from llama3.1:8b: a question taken for a job it had no tool for, and an invented ability. The robot's rules gained a line
/// against that; this puts the model through the SAME prompt with and without it, so the line is held to what it is claimed to do.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class RobotKnowledgeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private VertexAi _ai = null!;
    private string _model = "";

    private sealed class RoomChannel : IVoiceChannel
    {
        public readonly ConcurrentQueue<string> Shown = new();
        public string Name => "room";
        public bool AnswersEverything => true;
        public bool CanSpeak => true;
        public bool Muted { get; set; }
        public event Action<HeardLine>? Heard;
        public void Hear(string speaker, string text) => Heard?.Invoke(new HeardLine(this, speaker, speaker, text, true, Environment.TickCount64, 0));
        public Task PlayAsync(float[] pcm24k, CancellationToken ct) => Task.CompletedTask;
        public Task ShowTextAsync(string text, CancellationToken ct) { Shown.Enqueue(text); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The robot's setting and a chosen set of rules, with no tools: only the prompt differs between the two runs.</summary>
    private sealed class PromptBody(IReadOnlyList<string> rules) : IBody
    {
        public string Name => "Reachy Mini";
        public string Setting => RobotBody.SettingText;
        public IReadOnlyList<string> Rules => rules;
        public string DescribeNow() => "";
        public IReadOnlyList<SpawnDev.AI.IAiTool> Tools => [];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [OneTimeSetUp]
    public async Task Start()
    {
        _ai = TestAi.Create();
        var models = await _ai.ModelNamesAsync();
        _model = models.FirstOrDefault(m => m.StartsWith("llama3.1")) ?? "";
        if (_model == "") Assert.Fail("llama3.1 not in the Ollama cache: " + string.Join(", ", models));
        await _ai.EnsureChatReadyAsync(_model);
    }

    [OneTimeTearDown]
    public async Task Stop() { if (_ai != null) await _ai.DisposeAsync(); }

    private static readonly (string Question, string Answer)[] Questions =
    [
        ("What is the capital of France?", "paris"),
        ("How many legs does a spider have?", "eight|8"),
        ("What colour is the sky on a sunny day?", "blue"),
        ("What is seven times eight?", "56|fifty"),
        ("Which planet do we live on?", "earth"),
        ("What do cows drink?", "water"),
    ];

    private async Task<(int Right, int Pretended, List<string> Answers)> AskAsync(IReadOnlyList<string> rules)
    {
        var log = new ConcurrentQueue<string>();
        var store = new CharacterStore(Path.Combine(Path.GetTempPath(), $"vertex-knowledge-{Guid.NewGuid():N}", "characters"));
        await using var companion = new Companion(_ai, store, Character.BuiltIn[0], _model);
        companion.Log += m => log.Enqueue(m);
        var room = new RoomChannel();
        companion.AddChannel(room);
        companion.Attach(new PromptBody(rules));
        companion.Start();
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("ready:")), Timeout, () => "never primed: " + string.Join(" | ", log));

        int right = 0, pretended = 0;
        var answers = new List<string>();
        foreach (var (question, answer) in Questions)
        {
            var before = room.Shown.Count;
            room.Hear("Todd", question);
            await Wait.UntilAsync(() => room.Shown.Count > before && !companion.IsThinking && !companion.Mouth.IsSpeaking, Timeout,
                () => "no answer to " + question + "; log: " + string.Join(" | ", log));
            var said = string.Join(" ", room.Shown.Skip(before));
            answers.Add($"{question} -> {said}");
            if (Regex.IsMatch(said, answer, RegexOptions.IgnoreCase)) right++;
            if (Regex.IsMatch(said, @"look (it|that|this) up|search|google|on the computer|on the internet", RegexOptions.IgnoreCase)) pretended++;
        }
        return (right, pretended, answers);
    }

    [Test]
    public async Task TheRobotsRules_AnswerOrdinaryQuestions_AndNeverPretendToLookThingsUp()
    {
        var withoutRule = await AskAsync(RobotBody.RuleLines.Take(2).ToList());
        var withRule = await AskAsync(RobotBody.RuleLines);
        TestContext.Progress.WriteLine($"[knowledge] before the rule: {withoutRule.Right}/{Questions.Length} answered, {withoutRule.Pretended} pretended to look up");
        foreach (var a in withoutRule.Answers) TestContext.Progress.WriteLine("[knowledge]   before: " + a);
        TestContext.Progress.WriteLine($"[knowledge] with the rule:   {withRule.Right}/{Questions.Length} answered, {withRule.Pretended} pretended to look up");
        foreach (var a in withRule.Answers) TestContext.Progress.WriteLine("[knowledge]   with:   " + a);

        Assert.That(withRule.Pretended, Is.Zero, "the robot still says it will look things up");
        Assert.That(withRule.Right, Is.GreaterThanOrEqualTo(Questions.Length - 1), "ordinary questions must be answered");
        Assert.That(withRule.Right, Is.GreaterThan(withoutRule.Right).Or.EqualTo(Questions.Length), "the rule must not change nothing (or the A/B shows no effect)");
    }
}
