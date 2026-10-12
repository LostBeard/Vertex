using SpawnDev.AI;
using SpawnDev.Reachy;
using Vertex.Brain;
using Vertex.Core;

namespace Vertex.Reachy;

/// <summary>
/// Vertex's body at home: the Reachy Mini on the desk. It wakes up when Vertex arrives and parks when Vertex leaves,
/// shows what Vertex is doing for someone who cannot see a screen (listening, thinking, speaking: SpawnDev.Reachy's
/// <see cref="ReachyPresence"/>), and acts out the stage directions in Vertex's replies.
/// </summary>
/// <remarks>
/// Parking follows the confirmed recipe (SpawnDev.Reachy CLAUDE.md): home FIRST, then goto_sleep, wait for the head
/// to stop, THEN motors off. Speaking leaves the head lifted, and sleeping from there throws the head back.
/// </remarks>
public sealed class RobotBody : IBody
{
    private readonly ReachyChannel _channel;
    private readonly Companion _companion;
    private readonly ReachyBody _body;
    private readonly ReachyPresence _presence;
    private readonly GestureStyle _style = GestureStyle.Default;
    private readonly SemaphoreSlim _moodGate = new(1, 1);
    private volatile bool _thinking, _playing, _parked = true;

    public string Name => "Reachy Mini";

    public string Setting => "You have a small robot body at home, a Reachy Mini on the desk: a head that turns and tilts, two antennas, a body that " +
                             "turns, and a speaker (no arms, hands or legs). People in the room talk to you through it.";

    public IReadOnlyList<string> Rules { get; } =
    [
        "You may show a feeling with a very short stage direction in asterisks, such as *antennas perk up* or *tilts head*. Your robot body acts it out; it is never read out loud.",
    ];

    public IReadOnlyList<IAiTool> Tools { get; } = [];

    public event Action<string>? Log;

    public RobotBody(ReachyChannel channel, Companion companion)
    {
        _channel = channel;
        _companion = companion;
        _body = new ReachyBody(channel.Robot);
        _body.Log += m => Log?.Invoke("[body] " + m);
        _presence = new ReachyPresence(_body, _style);
        _presence.Log += m => Log?.Invoke(m);
        _channel.Playing += OnPlaying;
        _companion.Thinking += OnThinking;
    }

    public string DescribeNow() => "";

    public void Act(string action)
    {
        if (!_parked) _ = _body.PerformAsync(action, _style); // never throws; dropped when already moving
    }

    /// <summary>Motors on, the daemon's own wake-up move, then alive and listening.</summary>
    public async Task WakeAsync(CancellationToken ct = default)
    {
        var robot = _channel.Robot;
        await robot.SetMotorModeAsync(MotorMode.Enabled, ct);
        await robot.WakeUpAsync(ct);
        await Task.Delay(2500, ct); // the move is only queued when the call returns (Rose waits the same)
        _parked = false;
        _body.StartIdle(_style);
        await UpdateMoodAsync();
        Log?.Invoke("awake");
    }

    /// <summary>Home, sleep, wait for the head to settle, motors off (the confirmed parking order).</summary>
    public async Task ParkAsync()
    {
        if (_parked) return;
        _parked = true;
        await _presence.DisposeAsync();
        _body.Idle = false;
        var robot = _channel.Robot;
        try
        {
            await robot.GoHomeAsync(duration: 1.0);
            await WaitForHeadStillAsync(TimeSpan.FromSeconds(2));
            await robot.GotoSleepAsync();
            await WaitForHeadStillAsync(TimeSpan.FromSeconds(4));
        }
        catch (Exception e) { Log?.Invoke("parking moves failed: " + e.Message); /* release the motors regardless */ }
        try { await robot.SetMotorModeAsync(MotorMode.Disabled); }
        catch (Exception e) { Log?.Invoke("motors off failed: " + e.Message); }
        Log?.Invoke("parked");
    }

    /// <summary>Waits until the head stops moving (or the timeout), by watching its pose.</summary>
    private async Task WaitForHeadStillAsync(TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        XyzRpyPose? last = null;
        while (clock.Elapsed < timeout)
        {
            XyzRpyPose? now;
            try { now = await _channel.Robot.GetHeadPoseAsync(); }
            catch { return; } // cannot read: do not stall the parking
            if (now != null && last != null && Math.Abs(now.Z - last.Z) + Math.Abs(now.Pitch - last.Pitch) + Math.Abs(now.Roll - last.Roll) < 0.003) return;
            last = now;
            await Task.Delay(150);
        }
    }

    private void OnThinking(bool thinking)
    {
        _thinking = thinking;
        _ = UpdateMoodAsync();
    }

    private void OnPlaying(bool playing)
    {
        _playing = playing;
        _ = UpdateMoodAsync();
    }

    /// <summary>
    /// Shows the mood that fits right now. Back to listening only after a short pause: between two sentences of one
    /// reply nothing plays for a moment, and a "listening" perk there would be a twitch in the middle of speaking.
    /// </summary>
    private async Task UpdateMoodAsync()
    {
        if (_parked) return;
        try
        {
            await _moodGate.WaitAsync();
            try
            {
                var mood = _playing ? ReachyMood.Speaking : _thinking ? ReachyMood.Thinking : ReachyMood.Listening;
                if (mood == ReachyMood.Listening && _presence.Mood != ReachyMood.Listening)
                {
                    await Task.Delay(600);
                    if (_playing || _thinking || _parked) return;
                }
                if (!_parked) await _presence.SetAsync(mood);
            }
            finally { _moodGate.Release(); }
        }
        catch (Exception e) { Log?.Invoke("mood failed: " + e.Message); } // body language never breaks the conversation
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Playing -= OnPlaying;
        _companion.Thinking -= OnThinking;
        await ParkAsync();
        await _body.DisposeAsync();
    }
}
