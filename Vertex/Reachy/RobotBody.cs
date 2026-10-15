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

    /// <summary>What the language model is told about this body (a constant so a test can put exactly this in front of the model).</summary>
    public const string SettingText = "You have a small robot body at home, a Reachy Mini on the desk: a head that turns and tilts, two antennas, a body that " +
                                      "turns, and a speaker (no arms, hands or legs). People in the room talk to you through it.";

    public string Setting => SettingText;

    /// <summary>
    /// The rules this body adds. The last one exists because of a live session (2026-10-14): asked "What is the capital of France?" the 8B model
    /// answered "I don't know that one. I can look it up on the computer, though!" - it took a question for a job it had no tool for (the general rule
    /// "if you are asked to do something you have no tool for, say so") and invented a capability. See RobotKnowledgeTests for the measured A/B.
    /// </summary>
    public static readonly IReadOnlyList<string> RuleLines =
    [
        "You may show a feeling with a very short stage direction in asterisks, such as *antennas perk up* or *tilts head*. Your robot body acts it out; it is never read out loud.",
        "You tell people apart by their voice. When someone tells you their name (\"I'm Nikki\") you are told that you already learned their voice, so just greet them; when someone introduces a person who has not spoken yet (\"this is Nikki\"), call next_voice_is.",
        "A question is not a job. When someone asks an ordinary question (places, animals, numbers, how things work), give the answer itself in a sentence, not a comment about the question: asked \"What is the biggest animal?\" you say \"The blue whale!\". Say so honestly when you are not sure. You have no computer or internet: never say you will look something up.",
        "You are a robot on a desk: you cannot walk, fetch or build anything yourself, so never say you are \"on my way\" or about to go and do something. When someone asks what to do or build, answer with your idea in a sentence.",
    ];

    public IReadOnlyList<string> Rules => RuleLines;

    private const string NameSchema = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"the person's first name\"}},\"required\":[\"name\"]}";

    public IReadOnlyList<IAiTool> Tools { get; }

    public event Action<string>? Log;

    public RobotBody(ReachyChannel channel, Companion companion)
    {
        _channel = channel;
        _companion = companion;
        Tools =
        [
            new GameTool("next_voice_is",
                "Someone introduced a person who has not spoken yet (\"this is Nikki\", \"say hi to Aubs\"). Call this so the next new voice you hear is learned as that name.",
                NameSchema, a => Task.FromResult(_channel.IntroduceNextVoice(a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))) { OnRun = m => Log?.Invoke(m) },
        ];
        _body = new ReachyBody(channel.Robot);
        _body.Log += m => Log?.Invoke("[body] " + m);
        _presence = new ReachyPresence(_body, _style);
        _presence.Log += m => Log?.Invoke(m);
        _channel.Playing += OnPlaying;
        _companion.Thinking += OnThinking;
    }

    public string DescribeNow() => "";

    /// <summary>Turn to face whoever talks to the robot (from the voice's direction and the faces in the camera). Off: it keeps looking where it was.</summary>
    public bool FaceSpeaker { get; set; } = true;

    private CancellationTokenSource? _recentre;

    /// <summary>
    /// A friend started talking to the robot: turn to face them, then, once it has been quiet for a minute and a half, turn back to the front. A voice from straight ahead, a face already
    /// centred, or no direction at all makes no turn (see <see cref="Vertex.People.SpeakerGaze"/>).
    /// </summary>
    public void OnFriendSpoke(SaidEvent e)
    {
        if (_parked || !FaceSpeaker) return;
        if (e.Channel != null && !ReferenceEquals(e.Channel, _channel)) return;
        var target = Vertex.People.SpeakerGaze.Choose(_channel.Voice.Current(Environment.TickCount64), _channel.InView);
        if (target == null) return;
        Log?.Invoke($"facing {(target.Name ?? "the voice")}: {target.YawLeft * 180 / Math.PI:+0;-0;0} degrees ({(target.FromFace ? "a face matched the voice" : "from the voice direction")})");
        _ = _body.TurnTowardAsync(target.YawLeft, _style);
        // Back to the front after a quiet spell; each new turn restarts the wait.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _recentre, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(90), cts.Token);
                if (!_parked) await _body.RecentreAsync(_style, ct: cts.Token);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>
    /// "I'm Nikki", "my name is Nikki", "call me Nikki": the first name of someone introducing themselves, or null. The name must be capitalised (the
    /// speech recogniser capitalises names, not "I'm tired"), and "I'm Todd's friend" is not an introduction.
    /// </summary>
    public static string? ParseSelfIntroduction(string text)
    {
        var m = SelfIntroduction.Match(text);
        return m.Success ? m.Groups["name"].Value : null;
    }

    private static readonly System.Text.RegularExpressions.Regex SelfIntroduction = new(
        @"\b(?i:I'?m|I am|my name is|my name's|call me)\s+(?<name>[A-Z][a-z]{1,14})\b(?!['’])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A self-introduction is acted on here, not left to the language model: live, the 8B model answered "Vertex, hi, I'm Todd." with only
    /// "*antennas perk up*" and never called my_name_is, so the voice was never learned. The model then words the reply (Companion hands it
    /// "[You already did it: ...]" and no tools).
    /// </summary>
    public Task<string?> FastActAsync(SaidEvent e)
    {
        if (e.Channel != null && !ReferenceEquals(e.Channel, _channel)) return Task.FromResult<string?>(null);
        var name = ParseSelfIntroduction(e.Text);
        return Task.FromResult(name == null ? null : _channel.IntroduceSpeaker(name));
    }

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
