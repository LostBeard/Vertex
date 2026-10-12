using System.Text;

namespace Vertex.Brain;

/// <summary>
/// Where one turn's time went, from the moment the speaker stopped to the moment Vertex is heard answering: speech to
/// text, waiting for the GPU or an earlier turn, the language model's first token, the first sentence, and the voice.
/// Every responsiveness number is read from this, never guessed (PLAN M9). One line per turn in the log.
/// </summary>
public sealed class TurnClock
{
    private long _thinkTick, _firstTokenTick, _firstSentenceTick, _firstSoundTick;
    private int _reported;

    /// <summary>When the turn's clock started: the end of the speech that caused it, or the event.</summary>
    public long StartTick { get; }
    /// <summary>Speech to text, already spent before <see cref="StartTick"/> was known (part of the wait).</summary>
    public double SttMs { get; }
    /// <summary>The language model's own report of its passes (prompt size, reused prefix, time to first token).</summary>
    public List<string> Passes { get; } = [];

    public TurnClock(long startTick = 0, double sttMs = 0)
    {
        StartTick = startTick > 0 ? startTick : Environment.TickCount64;
        SttMs = sttMs;
    }

    public void Thinking() => Interlocked.CompareExchange(ref _thinkTick, Environment.TickCount64, 0);
    public void FirstToken() => Interlocked.CompareExchange(ref _firstTokenTick, Environment.TickCount64, 0);
    public void FirstSentence() => Interlocked.CompareExchange(ref _firstSentenceTick, Environment.TickCount64, 0);
    public bool FirstSound() => Interlocked.CompareExchange(ref _firstSoundTick, Environment.TickCount64, 0) == 0;

    /// <summary>Milliseconds from the end of speech to Vertex being heard, or null if it has not spoken yet.</summary>
    public long? ToFirstSoundMs => _firstSoundTick == 0 ? null : _firstSoundTick - StartTick;

    /// <summary>True the first time it is called: the turn is reported once (at the first sound, or at its end).</summary>
    public bool TakeReport() => Interlocked.Exchange(ref _reported, 1) == 0;

    /// <summary>"spoke 2.10 s after you stopped: text 0.40 s, wait 0.05 s, first word 1.05 s, sentence 0.20 s, voice 0.40 s; pass prompt=..."</summary>
    public string Describe(string? outcome = null)
    {
        static string S(double ms) => (ms / 1000).ToString("0.00") + " s";
        var sb = new StringBuilder();
        var end = _firstSoundTick != 0 ? _firstSoundTick : Environment.TickCount64;
        sb.Append(_firstSoundTick != 0 ? $"spoke {S(end - StartTick)} after the speaker stopped" : $"{outcome ?? "no reply"} after {S(end - StartTick)}");
        var parts = new List<string>();
        if (SttMs > 0) parts.Add($"text {S(SttMs)}");
        if (_thinkTick != 0) parts.Add($"wait {S(Math.Max(0, _thinkTick - StartTick - SttMs))}");
        if (_firstTokenTick != 0 && _thinkTick != 0) parts.Add($"first word {S(_firstTokenTick - _thinkTick)}");
        if (_firstSentenceTick != 0 && _firstTokenTick != 0) parts.Add($"sentence {S(_firstSentenceTick - _firstTokenTick)}");
        if (_firstSoundTick != 0 && _firstSentenceTick != 0) parts.Add($"voice {S(_firstSoundTick - _firstSentenceTick)}");
        if (parts.Count > 0) sb.Append(": ").Append(string.Join(", ", parts));
        lock (Passes)
            foreach (var p in Passes) sb.Append(" | ").Append(p.Trim());
        return sb.ToString();
    }
}
