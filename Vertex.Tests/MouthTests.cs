using System.Collections.Concurrent;
using SpawnDev.AI;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Core;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// The mouth on its own, with a fake speech synthesiser and fake channels (no GPU, no robot, no server): sentences
/// rendered ahead and played strictly in order, ears closed for the whole reply, interruption, text-only channels.
/// </summary>
public class MouthTests
{
    /// <summary>A channel that records what happens to it, in order.</summary>
    private sealed class FakeChannel(string name, bool canSpeak, int playMs = 0) : IVoiceChannel
    {
        private bool _muted;
        public readonly ConcurrentQueue<string> Events = new();
        public int RenderedWhenFirstDone = -1;
        public string Name => name;
        public bool CanSpeak => canSpeak;
        public bool Muted
        {
            get => _muted;
            set { if (_muted != value) Events.Enqueue(value ? "mute" : "unmute"); _muted = value; }
        }
        public event Action<HeardLine>? Heard;
        public void Hear(string text) => Heard?.Invoke(new HeardLine(this, "room", "Aubri", text, true, Environment.TickCount64, 0));
        public async Task PlayAsync(float[] pcm24k, CancellationToken ct)
        {
            Events.Enqueue("play " + pcm24k.Length);
            await Task.Delay(playMs, ct);
            if (RenderedWhenFirstDone < 0) RenderedWhenFirstDone = Rendered.Count;
            Events.Enqueue("done " + pcm24k.Length);
        }
        public Task ShowTextAsync(string text, CancellationToken ct)
        {
            Events.Enqueue("text " + text);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>"Speech" whose length is the sentence's length, so the log shows which sentence played.</summary>
    private static readonly ConcurrentQueue<string> Rendered = new();
    private static async Task<(float[], int)> FakeTts(string text, string voice, CancellationToken ct)
    {
        Rendered.Enqueue(text);
        await Task.Delay(20, ct);
        return (new float[text.Length], 24000);
    }

    [Test]
    public async Task PlaysEverySentenceInOrder_NeverOverlapping_AndRendersAhead()
    {
        Rendered.Clear();
        var robot = new FakeChannel("robot", canSpeak: true, playMs: 150);
        await using var mouth = new Mouth(FakeTts);
        mouth.Say("One. Two two. Three three three.", [robot]);
        await Wait.UntilAsync(() => robot.Events.Count(e => e.StartsWith("done")) == 3, TimeSpan.FromSeconds(10), () => string.Join(", ", robot.Events));
        var plays = robot.Events.Where(e => e.StartsWith("play") || e.StartsWith("done")).ToList();
        Assert.That(plays, Is.EqualTo(new[] { "play 4", "done 4", "play 8", "done 8", "play 18", "done 18" }), "one clip at a time, in order");
        // Rendering ahead: all three were synthesised before the first finished playing (150 ms each vs 20 ms each).
        var events = robot.Events.ToList();
        Assert.That(robot.RenderedWhenFirstDone, Is.EqualTo(3), "the next sentences were rendered while the first played");
        Assert.That(events[0], Is.EqualTo("text One. Two two. Three three three."));
    }

    [Test]
    public async Task EarsStayClosedForTheWholeReply_NotOpenedBetweenSentences()
    {
        Rendered.Clear();
        var robot = new FakeChannel("robot", canSpeak: true, playMs: 60);
        await using var mouth = new Mouth(FakeTts);
        mouth.BeginReply();
        mouth.Say("First sentence.", [robot]);
        await Wait.UntilAsync(() => robot.Events.Contains("done 15"), TimeSpan.FromSeconds(5), () => string.Join(", ", robot.Events));
        await Task.Delay(200); // the model is still generating the next sentence
        Assert.That(robot.Muted, "ears stay closed while the reply is still being generated");
        mouth.Say("Second one.", [robot]);
        await Wait.UntilAsync(() => robot.Events.Contains("done 11"), TimeSpan.FromSeconds(5), () => string.Join(", ", robot.Events));
        await Task.Delay(100);
        Assert.That(robot.Muted, "still closed: the reply is not over");
        mouth.EndReply();
        await Wait.UntilAsync(() => !robot.Muted, TimeSpan.FromSeconds(5), () => string.Join(", ", robot.Events));
        Assert.That(robot.Events.Count(e => e == "mute"), Is.EqualTo(1), string.Join(", ", robot.Events));
        Assert.That(robot.Events.Count(e => e == "unmute"), Is.EqualTo(1), string.Join(", ", robot.Events));
    }

    [Test]
    public async Task Hush_CutsOffTheLine_DropsTheRest_AndOpensTheEars()
    {
        Rendered.Clear();
        var robot = new FakeChannel("robot", canSpeak: true, playMs: 2000);
        await using var mouth = new Mouth(FakeTts);
        mouth.Say("A long first sentence. And more. And more after that.", [robot]);
        await Wait.UntilAsync(() => robot.Events.Any(e => e.StartsWith("play")), TimeSpan.FromSeconds(5), () => string.Join(", ", robot.Events));
        mouth.Hush();
        await Wait.UntilAsync(() => !robot.Muted && !mouth.IsSpeaking, TimeSpan.FromSeconds(2), () => string.Join(", ", robot.Events));
        await Task.Delay(300);
        Assert.That(robot.Events.Count(e => e.StartsWith("play")), Is.EqualTo(1), "nothing after the interruption: " + string.Join(", ", robot.Events));
        // It talks again after being hushed.
        mouth.Say("Okay.", [robot]);
        await Wait.UntilAsync(() => robot.Events.Contains("play 5"), TimeSpan.FromSeconds(5), () => string.Join(", ", robot.Events));
    }

    [Test]
    public async Task TextOnlyChannel_ShowsTheLine_AndCountsAsStarted()
    {
        var chat = new FakeChannel("chat", canSpeak: false);
        await using var mouth = new Mouth(FakeTts);
        var started = new TaskCompletionSource();
        mouth.Say("Hello there!", [chat], () => started.TrySetResult());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(chat.Events, Is.EqualTo(new[] { "text Hello there!" }), "shown, never played, ears untouched");
    }

    [Test]
    public async Task OnlyTheChannelsGiven_HearTheLine()
    {
        var robot = new FakeChannel("robot", canSpeak: true);
        var game = new FakeChannel("game", canSpeak: true);
        await using var mouth = new Mouth(FakeTts);
        mouth.Say("Just for the room.", [robot]);
        await Wait.UntilAsync(() => robot.Events.Any(e => e.StartsWith("done")), TimeSpan.FromSeconds(5), () => string.Join(", ", robot.Events));
        Assert.That(game.Events, Is.Empty);
    }

    [Test]
    public void SpeakerIds_AreStable_AndKeepPlayerUuids()
    {
        var player = Guid.NewGuid();
        Assert.That(Companion.SpeakerId(player.ToString()), Is.EqualTo(player));
        Assert.That(Companion.SpeakerId("room"), Is.EqualTo(Companion.SpeakerId("room")));
        Assert.That(Companion.SpeakerId("room"), Is.Not.EqualTo(Guid.Empty), "the empty id is the operator's");
    }
}
