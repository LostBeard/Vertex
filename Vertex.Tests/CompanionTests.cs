using System.Collections.Concurrent;
using SpawnDev.AI;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Core;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Vertex with no game (PLAN M10): the companion on the GPU (llama3.1:8b, Kokoro), talking through voice channels
/// that stand in for the robot's microphone and speaker. What is heard on a channel is answered on that channel, out
/// loud; a body's tools come and go with the body.
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class CompanionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private VertexAi _ai = null!;
    private string _model = "";

    /// <summary>A voice channel that records what it is asked to show and play.</summary>
    private sealed class RoomChannel(string name, bool answersEverything) : IVoiceChannel
    {
        public readonly ConcurrentQueue<string> Shown = new();
        public readonly ConcurrentQueue<double> PlayedSeconds = new();
        public string Name => name;
        public bool AnswersEverything => answersEverything;
        public bool CanSpeak => true;
        public bool Muted { get; set; }
        public event Action<HeardLine>? Heard;
        public void Hear(string speaker, string text) => Heard?.Invoke(new HeardLine(this, speaker, speaker, text, true, Environment.TickCount64, 0));
        public Task PlayAsync(float[] pcm24k, CancellationToken ct)
        {
            PlayedSeconds.Enqueue(pcm24k.Length / 24000.0);
            return Task.CompletedTask;
        }
        public Task ShowTextAsync(string text, CancellationToken ct)
        {
            Shown.Enqueue(text);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A body with one tool, to see it offered while attached and gone once detached.</summary>
    private sealed class DeskBody : IBody
    {
        public int Waves;
        public string Name => "desk";
        public string Setting => "You sit on a desk.";
        public IReadOnlyList<string> Rules => [];
        public string DescribeNow() => "";
        public IReadOnlyList<IAiTool> Tools => [new WaveTool(this)];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class WaveTool(DeskBody body) : IAiTool
        {
            public string Name => "wave_hello";
            public string Description => "Wave hello with your antennas.";
            public string ParametersJsonSchema => "{\"type\":\"object\",\"properties\":{}}";
            public Task<AiToolExecutionResult> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            {
                Interlocked.Increment(ref body.Waves);
                return Task.FromResult(new AiToolExecutionResult("you waved"));
            }
        }
    }

    [OneTimeSetUp]
    public async Task Start()
    {
        _ai = TestAi.Create();
        var models = await _ai.ModelNamesAsync();
        var model = models.FirstOrDefault(m => m.StartsWith("llama3.1"));
        if (model == null) Assert.Fail("llama3.1 not in the Ollama cache: " + string.Join(", ", models));
        _model = model!;
        await _ai.EnsureChatReadyAsync(_model);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_ai != null) await _ai.DisposeAsync();
    }

    private Companion NewCompanion(ConcurrentQueue<string> log)
    {
        var store = new CharacterStore(Path.Combine(Path.GetTempPath(), $"vertex-companion-{Guid.NewGuid():N}", "characters"));
        var companion = new Companion(_ai, store, Character.BuiltIn[0], _model);
        companion.Log += m => { log.Enqueue(m); TestContext.Out.WriteLine(m); };
        return companion;
    }

    /// <summary>
    /// The room talks, Vertex answers out loud in the room, and only there: a line heard on the robot's microphone
    /// is not played into the game. Everything said to the robot is for Vertex (no name needed).
    /// </summary>
    [Test]
    public async Task AnswersOutLoud_OnTheChannelItHeard_WithNoGame()
    {
        var log = new ConcurrentQueue<string>();
        await using var companion = NewCompanion(log);
        var room = new RoomChannel("room", answersEverything: true);
        var game = new RoomChannel("game", answersEverything: false);
        companion.AddChannel(room);
        companion.AddChannel(game);
        companion.Start();
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("ready:")), Timeout, () => "never primed: " + string.Join(" | ", log));

        room.Hear("Aubri", "What is your favourite animal?");
        await Wait.UntilAsync(() => !room.PlayedSeconds.IsEmpty && !companion.IsThinking && !companion.Mouth.IsSpeaking, Timeout,
            () => "no spoken answer; log: " + string.Join(" | ", log));
        TestContext.Out.WriteLine($"shown: {string.Join(" / ", room.Shown)}; played {room.PlayedSeconds.Sum():0.0} s");
        Assert.That(room.PlayedSeconds.Sum(), Is.GreaterThan(0.5), "a real spoken answer");
        Assert.That(game.Shown, Is.Empty, "nothing typed into the game");
        Assert.That(game.PlayedSeconds, Is.Empty, "nothing played into the game");
        Assert.That(log.Any(l => l.StartsWith("timing: spoke")), "the turn was timed: " + string.Join(" | ", log));

        // In the game, a line that does not name Vertex is someone else's conversation.
        game.Hear("Steve", "anyone want to go mining later?");
        await Task.Delay(3000);
        Assert.That(log.Count(l => l.StartsWith("> ")), Is.EqualTo(1), "answered a line not said to it: " + string.Join(" | ", log));
    }

    /// <summary>A body's tool is offered while the body is there and taken away with it.</summary>
    [Test]
    public async Task ABodysToolsComeAndGoWithIt()
    {
        var log = new ConcurrentQueue<string>();
        await using var companion = NewCompanion(log);
        var room = new RoomChannel("room", answersEverything: true);
        var desk = new DeskBody();
        companion.AddChannel(room);
        companion.Attach(desk);
        companion.Start();
        Assert.That(_ai.Tools.Get("wave_hello"), Is.Not.Null);
        Assert.That(companion.SystemPrompt(), Does.Contain("You sit on a desk."));
        companion.Detach(desk);
        Assert.That(_ai.Tools.Get("wave_hello"), Is.Null, "the tool left with the body");
        Assert.That(companion.SystemPrompt(), Does.Not.Contain("You sit on a desk."));
    }
}
