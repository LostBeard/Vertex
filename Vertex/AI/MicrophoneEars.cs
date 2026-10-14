using System.Threading.Channels;
using SpawnDev.AI.Server;
using SpawnDev.ILGPU.ML.Pipelines;

namespace Vertex.AI;

/// <summary>An utterance heard on an open microphone: what was said and, when identification is on, who sounds like they said it.</summary>
/// <param name="Text">The transcript.</param>
/// <param name="Seconds">How much speech it was.</param>
/// <param name="EndedTick">When it ended (<see cref="Environment.TickCount64"/>).</param>
/// <param name="SttMs">Speech to text time.</param>
/// <param name="Speaker">The voice, or null when identification is off or failed (the transcript never waits on it failing).</param>
public sealed record HeardUtterance(string Text, double Seconds, long EndedTick, double SttMs, AiSpeakerResult? Speaker);

/// <summary>
/// An open microphone (the Reachy Mini's array, a PC microphone) turned into lines of text: 16 kHz audio in any chunk
/// size, endpointed by Silero VAD, each finished utterance transcribed by Whisper. Everything runs in the GPU lane as
/// urgent work, so hearing never waits for a reply to finish generating.
/// </summary>
/// <remarks>
/// Endpointing has to be a real VAD: people pause mid-sentence, and an energy gate either cuts them off or waits
/// forever. Simple Voice Chat does not need this - its clients send their own stop packet (see <see cref="Ears"/>).
/// </remarks>
public sealed class MicrophoneEars : IAsyncDisposable
{
    private readonly VertexAi _ai;
    private readonly Channel<short[]> _audio = Channel.CreateBounded<short[]>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly VoiceActivityDetector _vad;
    private readonly Task _pump;
    private int _pendingTranscriptions;

    /// <summary>Ears closed (while Vertex talks): audio is dropped, not queued.</summary>
    public bool Muted { get; set; }

    /// <summary>Whether each utterance is also matched to a voice (Vertex's introduced people, else a provisional guest).</summary>
    public bool IdentifySpeakers { get; set; } = true;

    /// <summary>A finished utterance, transcribed, and (when <see cref="IdentifySpeakers"/>) whose voice it was.</summary>
    public event Action<HeardUtterance>? Heard;
    public event Action<string>? Log;

    private MicrophoneEars(VertexAi ai, VoiceActivityDetector vad)
    {
        _ai = ai;
        _vad = vad;
        _vad.OnSegment += OnSegment;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Loads Silero VAD (embedded) on the AI's accelerator, inside the GPU lane.</summary>
    public static async Task<MicrophoneEars> CreateAsync(VertexAi ai, VadOptions? options = null, CancellationToken ct = default)
    {
        using (await ai.Lane.EnterAsync(ct: ct))
        {
            var vad = SileroVad.Create(ai.Accelerator, SileroModel());
            return new MicrophoneEars(ai, new VoiceActivityDetector(vad, options, ownsVad: true));
        }
    }

    /// <summary>Hands over 16 kHz mono audio (any chunk size).</summary>
    public void Feed(short[] pcm16k)
    {
        if (!Muted) _audio.Writer.TryWrite(pcm16k);
    }

    /// <summary>True while nothing is queued, being endpointed or being transcribed (tests wait on it).</summary>
    public bool IsIdle => _audio.Reader.Count == 0 && Volatile.Read(ref _pendingTranscriptions) == 0 && !_vad.IsSpeechActive;

    private async Task PumpAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var pcm in _audio.Reader.ReadAllAsync(ct))
            {
                var samples = new float[pcm.Length];
                for (var i = 0; i < pcm.Length; i++) samples[i] = pcm[i] / 32768f;
                using (await _ai.Lane.EnterAsync(urgent: true, ct))
                    await _vad.AcceptWaveformAsync(samples);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log?.Invoke("microphone stopped: " + e.Message); }
    }

    private void OnSegment(SpeechSegment segment)
    {
        var ended = Environment.TickCount64;
        if (Muted) return;
        Interlocked.Increment(ref _pendingTranscriptions);
        _ = Task.Run(async () =>
        {
            try
            {
                var stt = System.Diagnostics.Stopwatch.StartNew();
                var text = await _ai.TranscribeAsync(segment.Samples, _cts.Token);
                stt.Stop();
                if (string.IsNullOrWhiteSpace(text) || Ears.IsNoise(text)) return;
                AiSpeakerResult? speaker = null;
                if (IdentifySpeakers)
                {
                    // A voice that cannot be identified is not a reason to lose the words: the transcript goes out either way.
                    try { speaker = await _ai.ObserveSpeakerAsync(segment.Samples, _cts.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { Log?.Invoke("speaker identification failed: " + e.Message); }
                }
                Log?.Invoke($"heard{(speaker?.Name is { } who ? " (" + who + (speaker.Reliable ? "" : ", short") + ")" : "")}: \"{text}\" ({segment.DurationSeconds:0.0} s, text in {stt.ElapsedMilliseconds} ms{(speaker != null ? ", voice in " + speaker.EmbedMs.ToString("0") + " ms" : "")})");
                Heard?.Invoke(new HeardUtterance(text, segment.DurationSeconds, ended, stt.Elapsed.TotalMilliseconds, speaker));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log?.Invoke("transcription failed: " + e.Message); }
            finally { Interlocked.Decrement(ref _pendingTranscriptions); }
        });
    }

    /// <summary>Silero VAD (MIT, snakers4/silero-vad, 16 kHz model), embedded: see Reachy/PROVENANCE.md.</summary>
    private static byte[] SileroModel()
    {
        using var s = typeof(MicrophoneEars).Assembly.GetManifestResourceStream("Vertex.Reachy.silero_vad.onnx")
            ?? throw new InvalidOperationException("silero_vad.onnx is not embedded");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _audio.Writer.TryComplete();
        try { await _pump; } catch { }
        _vad.Dispose();
        _cts.Dispose();
    }
}
