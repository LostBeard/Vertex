using System.Collections.Concurrent;
using SpawnDev.Reachy;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Reachy;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// The real Reachy Mini (reachy-mini.local, or VERTEX_REACHY_HOST): Vertex wakes it, speaks a reply through its
/// speaker with its body language, and parks it with the confirmed recipe. EXPLICIT: it moves and sounds a physical
/// robot in the family's home, so it only runs when asked for by name (and not after Aubs' bedtime).
/// </summary>
[Category("GPU")]
[Category("Robot")]
[Explicit("moves and sounds the real robot")]
[NonParallelizable]
public class RobotTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private static string RobotHost => Environment.GetEnvironmentVariable("VERTEX_REACHY_HOST") ?? "reachy-mini.local";

    [Test]
    public async Task WakesSpeaksAndParks()
    {
        await using var ai = TestAi.Create();
        var models = await ai.ModelNamesAsync();
        var model = models.FirstOrDefault(m => m.StartsWith("llama3.1")) ?? throw new AssertionException("llama3.1 not in the Ollama cache");
        await ai.EnsureChatReadyAsync(model);
        await ai.WarmUpAsync(Character.BuiltIn[0].Voice);

        var log = new ConcurrentQueue<string>();
        void Log(string m) { log.Enqueue(m); TestContext.Out.WriteLine(m); }
        var store = new CharacterStore(Path.Combine(Path.GetTempPath(), $"vertex-robot-{Guid.NewGuid():N}", "characters"));
        await using var companion = new Companion(ai, store, Character.BuiltIn[0], model);
        companion.Log += Log;
        var reachy = await ReachyChannel.ConnectAsync(RobotHost, ai);
        reachy.Log += m => Log("[reachy] " + m);
        var robot = new RobotBody(reachy, companion);
        robot.Log += m => Log("[reachy] " + m);
        try
        {
            await robot.WakeAsync();
            companion.Attach(robot);
            companion.AddChannel(reachy);
            companion.Start();
            await Wait.UntilAsync(() => log.Any(l => l.StartsWith("ready:")), Timeout, () => "never primed");

            // As if someone in the room had said it (the microphone path is MicrophoneTests' job).
            companion.Post(new SaidEvent(Companion.SpeakerId("room"), "TJ", "Hi Vertex! Say hello to the family in one short sentence.", true, Channel: reachy));
            await Wait.UntilAsync(() => log.Any(l => l.Contains("[reachy] played")), Timeout, () => "nothing played; log: " + string.Join(" | ", log));
            await Wait.UntilAsync(() => !companion.IsThinking && !companion.Mouth.IsSpeaking, Timeout, () => "still talking");
            Assert.That(log.Any(l => l.StartsWith("timing: spoke")), "timed: " + string.Join(" | ", log));
            Assert.That(reachy.Muted, Is.False, "ears open again after the reply");
        }
        finally
        {
            await companion.DisposeAsync();
            await robot.DisposeAsync(); // parks: home, sleep, motors off
            var motors = await reachy.Robot.GetMotorStatusAsync();
            await reachy.DisposeAsync();
            TestContext.Out.WriteLine($"motors after parking: {motors?.Mode}");
            Assert.That(log.Any(l => l.Contains("parked")), "parked");
            Assert.That(motors?.Mode, Is.EqualTo("disabled"), "motors released after parking");
        }
    }
}
