using System.Collections.Concurrent;
using SpawnDev.AI;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Core;
using Vertex.Reachy;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// The live failure of 2026-10-14, on the real model: TJ said "Vertex." (too short to identify), then "We're gonna play Minecraft today. Can you tell me what the best
/// thing to build first is?" The question was heard and DROPPED (not said to Vertex: the two voices had different keys), and the robot answered the bare name 3.6 s
/// later, over the question. Now a bare name makes the body perk up at once with no spoken answer, and the next line is part of the same conversation.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class WakeCallTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private VertexAi _ai = null!;
    private string _model = "";

    /// <summary>The robot's microphone with "answer everything" OFF: only a line that calls Vertex, or continues a conversation, is for it.</summary>
    private sealed class MicChannel : IVoiceChannel
    {
        public readonly ConcurrentQueue<string> Shown = new();
        public string Name => "robot mic";
        public bool AnswersEverything => false;
        public bool CanSpeak => true;
        public bool Muted { get; set; }
        public event Action<HeardLine>? Heard;
        public void Hear(string key, string name, string text) => Heard?.Invoke(new HeardLine(this, key, name, text, true, Environment.TickCount64, 0));
        public Task PlayAsync(float[] pcm24k, CancellationToken ct) => Task.CompletedTask;
        public Task ShowTextAsync(string text, CancellationToken ct) { Shown.Enqueue(text); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WatchingBody : IBody
    {
        public readonly ConcurrentQueue<string> Acted = new();
        public string Name => "Reachy Mini";
        public string Setting => RobotBody.SettingText;
        public IReadOnlyList<string> Rules => RobotBody.RuleLines;
        public string DescribeNow() => "";
        public IReadOnlyList<IAiTool> Tools => [];
        public void Act(string action) => Acted.Enqueue(action);
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

    private async Task<(Companion Companion, MicChannel Mic, WatchingBody Body, ConcurrentQueue<string> Log)> NewRoomAsync()
    {
        var log = new ConcurrentQueue<string>();
        var store = new CharacterStore(Path.Combine(Path.GetTempPath(), $"vertex-wake-{Guid.NewGuid():N}", "characters"));
        var companion = new Companion(_ai, store, Character.BuiltIn[0], _model);
        companion.Log += m => log.Enqueue(m);
        var mic = new MicChannel();
        var body = new WatchingBody();
        companion.AddChannel(mic);
        companion.Attach(body);
        companion.Start();
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("ready:")), Timeout, () => "never primed: " + string.Join(" | ", log));
        return (companion, mic, body, log);
    }

    [Test]
    public async Task ABareName_PerksUpAtOnceWithoutAnAnswer_AndTheQuestionThatFollowsIsAnswered_EvenFromAnotherKey()
    {
        var (companion, mic, body, log) = await NewRoomAsync();
        await using var _ = companion;

        mic.Hear("room", "someone in the room", "Vertex.");                    // too short to identify
        await Wait.UntilAsync(() => !body.Acted.IsEmpty, TimeSpan.FromSeconds(10), () => "no perk; log: " + string.Join(" | ", log));
        await Task.Delay(1500);
        Assert.That(body.Acted, Does.Contain("antennas perk up"));
        Assert.That(mic.Shown, Is.Empty, "a bare name must not get a spoken answer");
        Assert.That(log.Count(l => l.StartsWith("> ")), Is.Zero, "the language model was asked about a bare name");

        mic.Hear("voice:Todd", "Todd", "We're gonna play Minecraft today. Can you tell me what the best thing to build first is?");   // identified, no name in it
        await Wait.UntilAsync(() => mic.Shown.Count > 0 && !companion.IsThinking && !companion.Mouth.IsSpeaking, Timeout, () => "the question was dropped; log: " + string.Join(" | ", log));
        TestContext.Progress.WriteLine("[wake] answer: " + string.Join(" ", mic.Shown));
        Assert.That(string.Join(" ", mic.Shown).Count(char.IsLetter), Is.GreaterThan(15));
    }

    [Test]
    public async Task ALineThatNeverCalledVertex_AndFollowsNoConversation_IsNotAnswered()
    {
        var (companion, mic, body, log) = await NewRoomAsync();
        await using var _ = companion;
        mic.Hear("voice:Todd", "Todd", "We're gonna play Minecraft today. Can you tell me what the best thing to build first is?");
        await Task.Delay(4000);
        Assert.That(mic.Shown, Is.Empty, "answered a line that was not said to it: " + string.Join(" | ", log));
        Assert.That(body.Acted, Is.Empty);
    }

    /// <summary>
    /// "We're gonna play Minecraft today." ... "What's the best thing to build first?" in a normal flow: two sentences, the first of which is complete and gets a turn of its
    /// own. People do not take turns sentence by sentence, so when the second arrives before the robot has made a sound, the half-formed answer is dropped and ONE answer
    /// covers both. (Live 2026-10-14: one answer to the first, and the second lost.)
    /// </summary>
    [Test]
    public async Task TwoSentencesInANormalFlow_GetOneAnswer_ToBoth()
    {
        var (companion, mic, body, log) = await NewRoomAsync();
        await using var _ = companion;

        mic.Hear("voice:Todd", "Todd", "Vertex, we're gonna play Minecraft today.");
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("> ")), TimeSpan.FromSeconds(20), () => "never started thinking; log: " + string.Join(" | ", log));
        await Task.Delay(300);                       // the second sentence follows within a breath, while the answer to the first is still being formed
        mic.Hear("voice:Todd", "Todd", "What's the best thing to build first?");

        await Wait.UntilAsync(() => log.Count(l => l.StartsWith("timing: spoke")) >= 1 && !companion.IsThinking && !companion.Mouth.IsSpeaking, Timeout, () => "no answer; log: " + string.Join(" | ", log));
        await Task.Delay(2500);
        var asked = log.Where(l => l.StartsWith("> ")).ToList();
        TestContext.Progress.WriteLine("[flow] questions put to the model: " + string.Join("  //  ", asked));
        TestContext.Progress.WriteLine("[flow] answer: " + string.Join(" ", mic.Shown));
        Assert.That(log.Any(l => l.Contains("kept talking before I answered")), Is.True, "the first answer was not dropped: " + string.Join(" | ", log));
        Assert.That(log.Count(l => l.StartsWith("timing: spoke")), Is.EqualTo(1), "answered twice: " + string.Join(" | ", log));
        Assert.That(asked.Last(), Does.Contain("Minecraft today").And.Contain("best thing to build first"), "the one answer must be to both lines");
        Assert.That(string.Join(" ", mic.Shown).Count(char.IsLetter), Is.GreaterThan(15));
    }

    [Test]
    public async Task AStopAfterASentence_IsNotMergedIntoIt()
    {
        var (companion, mic, body, log) = await NewRoomAsync();
        await using var _ = companion;
        mic.Hear("voice:Todd", "Todd", "Vertex, tell me a very long story about dragons.");
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("> ")), TimeSpan.FromSeconds(20), () => "never started thinking");
        await Task.Delay(300);
        mic.Hear("voice:Todd", "Todd", "Actually stop.");
        await Task.Delay(6000);
        Assert.That(log.Any(l => l.Contains("kept talking before I answered")), Is.False, "a 'stop' was merged into the line it stops: " + string.Join(" | ", log));
    }
}
