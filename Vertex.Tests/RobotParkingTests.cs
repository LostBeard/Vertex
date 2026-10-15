using System.Diagnostics;
using System.Net;
using System.Text;
using SpawnDev.Reachy;

namespace Vertex.Tests;

/// <summary>
/// Parking the robot. Found live (2026-10-15): clicking "Stop Vertex" dropped the head "like a sack of potatoes". The log of the robot's daemon showed goto_sleep and the motors-off
/// call TWO seconds apart: the old wait watched the head's pose for stillness, but a queued move has not started yet, so the pose is still for a moment and the wait ended at once.
/// A stand-in daemon here runs each move for a real length of time and lists it as running until then, the way the real one does.
/// </summary>
public class RobotParkingTests
{
    private sealed class FakeDaemon : HttpMessageHandler
    {
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly List<(string What, long At)> Calls = new();
        private readonly Dictionary<string, long> _endsAt = new();
        private int _next;
        public long HomeMs = 400, SleepMs = 1500;
        public bool SleepFails;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            long now = Clock.ElapsedMilliseconds;
            HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            lock (Calls)
            {
                if (path == "/api/move/running")
                    return Task.FromResult(Json("[" + string.Join(",", _endsAt.Where(kv => kv.Value > now).Select(kv => $"{{\"uuid\":\"{kv.Key}\"}}")) + "]"));
                Calls.Add((path, now));
                if (path == "/api/move/goto" || path == "/api/move/play/goto_sleep")
                {
                    if (path.EndsWith("goto_sleep") && SleepFails) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                    var id = $"move-{++_next}";
                    _endsAt[id] = now + (path.EndsWith("goto_sleep") ? SleepMs : HomeMs);
                    return Task.FromResult(Json($"{{\"uuid\":\"{id}\"}}"));
                }
                return Task.FromResult(Json("{\"status\":\"ok\"}"));
            }
        }

        public long At(string path) => Calls.Last(c => c.What == path).At;
        public long EndOf(string uuid) => _endsAt[uuid];
    }

    private static (ReachyMiniClient Robot, FakeDaemon Daemon) Robot()
    {
        var daemon = new FakeDaemon();
        return (new ReachyMiniClient(new HttpClient(daemon) { BaseAddress = new Uri("http://fake-reachy:8000") }), daemon);
    }

    [Test]
    public async Task MotorsGoOffOnlyAfterTheSleepMoveHasFinished_AndSleepOnlyAfterHome()
    {
        var (robot, daemon) = Robot();
        await robot.ParkAsync();

        var paths = daemon.Calls.Select(c => c.What).ToList();
        Assert.That(paths, Is.EqualTo(new[] { "/api/move/goto", "/api/move/play/goto_sleep", "/api/motors/set_mode/disabled" }), "home, then sleep, then motors off");
        var homeEnds = daemon.EndOf("move-1");
        var sleepEnds = daemon.EndOf("move-2");
        Assert.That(daemon.At("/api/move/play/goto_sleep"), Is.GreaterThanOrEqualTo(homeEnds - 5), "sleep is not started while the head is still going home");
        Assert.That(daemon.At("/api/motors/set_mode/disabled"), Is.GreaterThanOrEqualTo(sleepEnds), "the motors stay on until the sleep move is over: cutting them earlier drops the head");
    }

    [Test]
    public void AFailedMoveStillReleasesTheMotors_AndTheFailureIsReported()
    {
        var (robot, daemon) = Robot();
        daemon.SleepFails = true;
        Assert.ThrowsAsync<HttpRequestException>(async () => await robot.ParkAsync());
        Assert.That(daemon.Calls.Select(c => c.What), Does.Contain("/api/motors/set_mode/disabled"), "the motors are released even when sleep failed");
    }

    [Test]
    public async Task WaitingForAMoveThatNeverEndsGivesUpAtTheTimeout()
    {
        var (robot, daemon) = Robot();
        daemon.HomeMs = 60_000;
        var handle = await robot.GoHomeAsync();
        var clock = Stopwatch.StartNew();
        var finished = await robot.WaitForMoveAsync(handle, TimeSpan.FromMilliseconds(600));
        Assert.That(finished, Is.False);
        Assert.That(clock.ElapsedMilliseconds, Is.InRange(550, 1500));
    }
}
