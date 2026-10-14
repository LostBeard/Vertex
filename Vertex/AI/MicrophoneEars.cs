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
    private readonly TurnAssembler? _turns;
    private readonly Task _pump;
    private int _pendingTranscriptions;

    private volatile bool _muted;

    /// <summary>Ears closed (while Vertex talks): audio is dropped, not queued, and a turn being judged is forgotten.</summary>
    public bool Muted
    {
        get => _muted;
        set { _muted = value; if (value) _turns?.Clear(); }
    }

    /// <summary>Whether each utterance is also matched to a voice (Vertex's introduced people, else a provisional guest).</summary>
    public bool IdentifySpeakers { get; set; } = true;

    /// <summary>
    /// When set, every utterance is kept in this folder as a WAV (16 kHz mono) with the words Vertex heard (.txt) and who it sounded
    /// like (.json): a real-voice test set. Off (null) by default and never uploaded anywhere; correct the .txt files by hand and run
    /// the speech-to-text bench on the folder to measure listening on the household's own voices.
    /// </summary>
    public string? SaveFolder { get; set; }

    /// <summary>A finished utterance, transcribed, and (when <see cref="IdentifySpeakers"/>) whose voice it was.</summary>
    public event Action<HeardUtterance>? Heard;
    public event Action<string>? Log;

    private MicrophoneEars(VertexAi ai, VoiceActivityDetector vad, bool smartTurn)
    {
        _ai = ai;
        _vad = vad;
        if (smartTurn)
        {
            _turns = new TurnAssembler((pcm, ct) => ai.PredictTurnEndAsync(pcm, ct));
            _turns.Log += m => Log?.Invoke(m);
            _turns.Completed += OnTurn;
        }
        _vad.OnSegment += OnSegment;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>The silence that closes a VAD segment when the end-of-turn model decides whether the turn is over (the plain rule is half a second).</summary>
    public static readonly TimeSpan SmartTurnSilence = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Loads Silero VAD (embedded) on the AI's accelerator, inside the GPU lane.
    /// </summary>
    /// <param name="smartTurn">
    /// Ask Smart Turn whether each pause is the end of a turn (see <see cref="TurnAssembler"/>) instead of always waiting half a second. The VAD then closes
    /// segments after <see cref="SmartTurnSilence"/> unless <paramref name="options"/> says otherwise.
    /// </param>
    public static async Task<MicrophoneEars> CreateAsync(VertexAi ai, VadOptions? options = null, bool smartTurn = true, CancellationToken ct = default)
    {
        using (await ai.Lane.EnterAsync(ct: ct))
        {
            if (smartTurn && options == null) options = new VadOptions { MinSilenceDuration = SmartTurnSilence };
            var vad = SileroVad.Create(ai.Accelerator, SileroModel());
            return new MicrophoneEars(ai, new VoiceActivityDetector(vad, options, ownsVad: true), smartTurn);
        }
    }

    /// <summary>Hands over 16 kHz mono audio (any chunk size).</summary>
    public void Feed(short[] pcm16k)
    {
        if (!Muted) _audio.Writer.TryWrite(pcm16k);
    }

    /// <summary>True while nothing is queued, being endpointed or being transcribed (tests wait on it).</summary>
    public bool IsIdle => _audio.Reader.Count == 0 && Volatile.Read(ref _pendingTranscriptions) == 0 && !_vad.IsSpeechActive && _turns?.HasPending != true;

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
                _turns?.SetSpeaking(_vad.IsSpeechActive);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log?.Invoke("microphone stopped: " + e.Message); }
    }

    private void OnSegment(SpeechSegment segment)
    {
        if (Muted) return;
        if (_turns != null) _turns.Add(segment.Samples, segment.StartSample, Environment.TickCount64);
        else Process(segment.Samples, Environment.TickCount64, "silence");
    }

    private void OnTurn(CompletedTurn turn)
    {
        if (turn.Probability is { } p) Log?.Invoke($"turn over ({turn.Reason}, p={p:0.00}, {turn.Samples.Length / 16000.0:0.0} s)");
        if (Muted) return;     // Vertex started talking while the turn was being judged: that is its own voice or a room it is not listening to
        Process(turn.Samples, turn.EndedTick, turn.Reason);
    }

    private void Process(float[] samples, long ended, string why)
    {
        var seconds = samples.Length / 16000.0;
        Interlocked.Increment(ref _pendingTranscriptions);
        _ = Task.Run(async () =>
        {
            try
            {
                var stt = System.Diagnostics.Stopwatch.StartNew();
                var text = await _ai.TranscribeAsync(samples, _cts.Token);
                stt.Stop();
                if (string.IsNullOrWhiteSpace(text) || Ears.IsNoise(text)) return;
                AiSpeakerResult? speaker = null;
                if (IdentifySpeakers)
                {
                    // A voice that cannot be identified is not a reason to lose the words: the transcript goes out either way.
                    try { speaker = await _ai.ObserveSpeakerAsync(samples, _cts.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { Log?.Invoke("speaker identification failed: " + e.Message); }
                }
                Log?.Invoke($"heard{(speaker?.Name is { } who ? " (" + who + (speaker.Reliable ? "" : ", short") + ")" : "")}: \"{text}\" ({seconds:0.0} s, text in {stt.ElapsedMilliseconds} ms{(speaker != null ? ", voice in " + speaker.EmbedMs.ToString("0") + " ms" : "")}, turn ended: {why})");
                if (SaveFolder is { Length: > 0 } folder) SaveHeard(folder, samples, text, seconds, speaker, stt.Elapsed.TotalMilliseconds);
                Heard?.Invoke(new HeardUtterance(text, seconds, ended, stt.Elapsed.TotalMilliseconds, speaker));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log?.Invoke("transcription failed: " + e.Message); }
            finally { Interlocked.Decrement(ref _pendingTranscriptions); }
        });
    }

    /// <summary>Writes one heard utterance beside the others, off the listening path (a slow disk never delays the answer).</summary>
    private void SaveHeard(string folder, float[] samples, string text, double seconds, AiSpeakerResult? speaker, double sttMs)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                var pcm = new short[samples.Length];
                for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
                var path = Path.Combine(folder, stamp);
                File.WriteAllBytes(path + ".wav", Reachy.ReachyChannel.BuildWav(pcm, 16000));
                File.WriteAllText(path + ".txt", text + Environment.NewLine);
                File.WriteAllText(path + ".json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    seconds, sttMs,
                    speaker = speaker?.Name, score = speaker?.Match.Score, known = speaker?.Match.Known,
                    guest = speaker?.Guest?.Name, reliable = speaker?.Reliable,
                }));
            }
            catch (Exception e) { Log?.Invoke("could not keep what was heard: " + e.Message); }
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
        _turns?.Dispose();
        _vad.Dispose();
        _cts.Dispose();
    }
}
