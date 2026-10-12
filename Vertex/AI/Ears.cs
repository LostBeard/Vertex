using System.Collections.Concurrent;
using SpawnDev.Minecraft.VoiceChat;

namespace Vertex.AI;

/// <summary>Something a player said out loud, as Whisper heard it.</summary>
/// <param name="EndedTick">When the speaker stopped (<see cref="Environment.TickCount64"/>): where a turn's clock starts.</param>
/// <param name="SttMs">How long speech to text took.</param>
public sealed record HeardSpeech(Guid Speaker, string Text, double Seconds, long EndedTick = 0, double SttMs = 0);

/// <summary>
/// Turns proximity voice into text: collects each speaker's 20 ms frames into an utterance, which ends when the
/// speaker's own client sends its stop packet (Simple Voice Chat's voice activation does the endpointing) or after
/// a silence gap, then transcribes it with Whisper. One utterance at a time goes to the GPU.
/// </summary>
public sealed class Ears : IDisposable
{
    private readonly SimpleVoiceChat _voice;
    private readonly Func<float[], CancellationToken, Task<string>> _transcribe;
    private readonly ConcurrentDictionary<Guid, Utterance> _open = new();
    private readonly SemaphoreSlim _gpu = new(1, 1);
    private readonly Timer _gapTimer;

    private sealed class Utterance
    {
        public readonly List<short> Pcm = new(48000 * 4);
        public long LastFrameMs = Environment.TickCount64;
    }

    /// <summary>Shortest utterance worth transcribing (Whisper hallucinates words into very short noise).</summary>
    public double MinSeconds { get; set; } = 0.35;
    /// <summary>Longest utterance kept (Whisper's window is 30 s).</summary>
    public double MaxSeconds { get; set; } = 25;
    /// <summary>Silence after which an utterance is considered finished even without a stop packet.</summary>
    public int GapMs { get; set; } = 900;
    /// <summary>Quietest utterance (RMS) treated as speech.</summary>
    public double MinRms { get; set; } = 0.005;

    public event Action<HeardSpeech>? Heard;
    public event Action<string>? Log;

    public Ears(SimpleVoiceChat voice, Func<float[], CancellationToken, Task<string>> transcribe)
    {
        _voice = voice;
        _transcribe = transcribe;
        _voice.AudioReceived += OnFrame;
        _gapTimer = new Timer(_ => FlushStale(), null, 200, 200);
    }

    private void OnFrame(VoiceFrame f)
    {
        if (f.Pcm.Length == 0)
        {
            if (_open.TryRemove(f.Sender, out var done)) Finish(f.Sender, done);
            return;
        }
        var u = _open.GetOrAdd(f.Sender, _ => new Utterance());
        lock (u)
        {
            u.Pcm.AddRange(f.Pcm);
            u.LastFrameMs = Environment.TickCount64;
            if (u.Pcm.Count >= MaxSeconds * SimpleVoiceChat.SampleRate && _open.TryRemove(f.Sender, out var full)) Finish(f.Sender, full);
        }
    }

    private void FlushStale()
    {
        var now = Environment.TickCount64;
        foreach (var (speaker, u) in _open)
            if (now - u.LastFrameMs > GapMs && _open.TryRemove(speaker, out var stale)) Finish(speaker, stale);
    }

    private void Finish(Guid speaker, Utterance u)
    {
        short[] pcm;
        long endedTick;
        lock (u) { pcm = u.Pcm.ToArray(); endedTick = u.LastFrameMs; }
        var seconds = pcm.Length / (double)SimpleVoiceChat.SampleRate;
        if (seconds < MinSeconds || Audio.Rms(pcm) < MinRms) return;
        _ = Task.Run(async () =>
        {
            await _gpu.WaitAsync();
            try
            {
                var stt = System.Diagnostics.Stopwatch.StartNew();
                var text = await _transcribe(Audio.Downsample48To16(pcm), CancellationToken.None);
                stt.Stop();
                if (string.IsNullOrWhiteSpace(text) || IsNoise(text)) return;
                Log?.Invoke($"heard {speaker}: \"{text}\" ({seconds:0.0} s, text in {stt.ElapsedMilliseconds} ms)");
                Heard?.Invoke(new HeardSpeech(speaker, text, seconds, endedTick, stt.Elapsed.TotalMilliseconds));
            }
            catch (Exception e)
            {
                Log?.Invoke("transcription failed: " + e.Message);
            }
            finally
            {
                _gpu.Release();
            }
        });
    }

    // Whisper's well-known outputs for silence/noise.
    internal static bool IsNoise(string text)
    {
        var t = text.Trim().Trim('.', '!', '?', ' ').ToLowerInvariant();
        return t.Length == 0 || t is "you" or "thank you" or "thanks for watching" or "[blank_audio]" or "[music]" || t.StartsWith('[') || t.StartsWith('(');
    }

    public void Dispose()
    {
        _voice.AudioReceived -= OnFrame;
        _gapTimer.Dispose();
    }
}
