using System.Globalization;

namespace Vertex.Reachy;

/// <summary>Where a voice is, from the robot's point of view.</summary>
/// <param name="Radians">The microphone array's angle: 0 = the robot's left, pi/2 = straight ahead (or straight behind), pi = its right.</param>
/// <param name="Samples">How many readings it was taken from.</param>
public readonly record struct VoiceBearing(double Radians, int Samples)
{
    /// <summary>Degrees from straight ahead: negative to the robot's left, positive to its right.</summary>
    public double DegreesFromAhead => Radians * 180 / Math.PI - 90;

    /// <summary>"left", "ahead" or "right".</summary>
    public string Side => DegreesFromAhead switch { < -20 => "left", > 20 => "right", _ => "ahead" };

    /// <summary>The clock position seen from behind the robot: 12 straight ahead, 9 on its left, 3 on its right (the nearest half hour).</summary>
    public string Clock
    {
        get
        {
            var halfHours = (int)Math.Round(Math.Clamp(DegreesFromAhead, -90, 90) / 15.0);   // 15 degrees = half an hour
            var hour = 12 + halfHours / 2.0;
            if (hour <= 0) hour += 12;
            if (hour >= 13) hour -= 12;   // 12:30 stays 12:30
            var whole = (int)Math.Floor(hour);
            return hour - whole > 0 ? $"{whole}:30" : $"{whole}";
        }
    }

    public override string ToString() => $"{Side}, about {Clock} o'clock ({DegreesFromAhead.ToString("+0;-0;0", CultureInfo.InvariantCulture)}°)";
}

/// <summary>
/// Where the voice the robot is hearing comes from, smoothed. The robot's four microphones sit in one straight line, so the chip reports an angle from 0 to 180
/// degrees (it cannot tell ahead from behind) and only while it hears speech; a voice aimed at a screen or turned away bounces off the room first, so single readings
/// jump. The answer is the MEDIAN of the speech readings in the last second and a half.
/// </summary>
/// <remarks>
/// Measured 2026-10-15 on the robot (daemon 1.11.0, audio firmware 2.1.4): a person at the 10:30 position read a median of 38 degrees on this scale (45 expected), with single readings from 27 to 88.
/// </remarks>
public sealed class VoiceDirection
{
    private readonly List<(double Radians, long Tick)> _speech = new();
    private VoiceBearing? _last;
    private long _lastTick = long.MinValue / 2;

    /// <summary>How far back the speech readings that make up the bearing reach.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long the last bearing is still reported after the voice stops.</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Adds one reading from the chip. Readings without speech are ignored: the chip keeps reporting its last angle when it hears nothing.</summary>
    public void Add(double radians, bool speech, long tick)
    {
        lock (_speech)
        {
            _speech.RemoveAll(r => tick - r.Tick > Window.TotalMilliseconds);
            if (!speech || double.IsNaN(radians)) return;
            _speech.Add((radians, tick));
            var ordered = _speech.Select(r => r.Radians).Order().ToList();
            _last = new VoiceBearing(ordered[ordered.Count / 2], ordered.Count);
            _lastTick = tick;
        }
    }

    /// <summary>The bearing of the voice heard just now, or null when it has been quiet for longer than <see cref="Hold"/>.</summary>
    public VoiceBearing? Current(long tick)
    {
        lock (_speech) return tick - _lastTick <= Hold.TotalMilliseconds ? _last : null;
    }

    /// <summary>Whether speech was heard within the last <see cref="Window"/> (the voice is talking now, not just remembered).</summary>
    public bool Speaking(long tick)
    {
        lock (_speech) return tick - _lastTick <= Window.TotalMilliseconds;
    }
}
