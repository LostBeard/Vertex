using SpawnDev.AI;

namespace Vertex.Core;

/// <summary>
/// One way Vertex hears and speaks: Simple Voice Chat in a Minecraft world, the Reachy Mini's microphone array and
/// speaker, or the control panel's text box. Several can be live at once (PLAN M13); every line heard carries the
/// channel it came from, and the answer goes back the same way.
/// </summary>
public interface IVoiceChannel : IAsyncDisposable
{
    /// <summary>"Minecraft voice", "Reachy", "Control panel".</summary>
    string Name { get; }

    /// <summary>Lines heard, already transcribed (or typed).</summary>
    event Action<HeardLine>? Heard;

    /// <summary>Whether this channel can play audio (false for text-only channels).</summary>
    bool CanSpeak { get; }

    /// <summary>Plays 24 kHz mono speech and returns when it has been heard (playback is serialized by the caller).</summary>
    Task PlayAsync(float[] pcm24k, CancellationToken ct);

    /// <summary>Shows the words (game chat, the control panel); a speaker-only channel ignores it.</summary>
    Task ShowTextAsync(string text, CancellationToken ct);

    /// <summary>Ears closed while Vertex is talking on this channel, so it never hears itself.</summary>
    bool Muted { get; set; }
}

/// <summary>A line heard on a channel.</summary>
/// <param name="Channel">Where it was heard (the answer goes back there).</param>
/// <param name="SpeakerKey">Who, as the channel knows them (a Minecraft UUID; "room" for the robot's microphone).</param>
/// <param name="SpeakerName">Their name, for the prompt and for addressing.</param>
/// <param name="Text">What they said.</param>
/// <param name="Voice">Said out loud (true) or typed.</param>
/// <param name="EndedTick">When they stopped (<see cref="Environment.TickCount64"/>): where the turn's clock starts.</param>
/// <param name="SttMs">Speech to text time.</param>
public sealed record HeardLine(IVoiceChannel Channel, string SpeakerKey, string SpeakerName, string Text, bool Voice,
    long EndedTick, double SttMs);

/// <summary>
/// A place Vertex is, and what it can do there: a Minecraft world (position, inventory, the game's tools and
/// reflexes) or the Reachy Mini on the desk (its head, antennas and body language). The companion core asks each
/// live body for its part of the prompt and its tools.
/// </summary>
public interface IBody : IAsyncDisposable
{
    /// <summary>"Minecraft (mc.spawndev.com)", "Reachy Mini".</summary>
    string Name { get; }

    /// <summary>One or two sentences for the system prompt: where Vertex is and how friends talk to it there.
    /// Must not change from turn to turn (it is part of the prompt prefix the language model reuses).</summary>
    string Setting { get; }

    /// <summary>Rules that only apply in this body. Stable, like <see cref="Setting"/>.</summary>
    IReadOnlyList<string> Rules { get; }

    /// <summary>What is going on here right now, for the note before each message.</summary>
    string DescribeNow();

    /// <summary>What Vertex can do here, as tools for the language model.</summary>
    IReadOnlyList<IAiTool> Tools { get; }
}
