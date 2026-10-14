using SpawnDev.Reachy;
using Vertex.AI;
using Vertex.Core;

namespace Vertex.Reachy;

/// <summary>
/// The Reachy Mini as a voice channel: its 4-microphone array in (WebRTC through <see cref="RoseAudioLink"/>, 16 kHz,
/// with the XVF3800's own echo cancellation), endpointed by Silero VAD and transcribed by Whisper; Kokoro speech out
/// through the robot's speaker.
/// </summary>
/// <remarks>
/// The speaker path is Rose's, byte for byte, because TJ and Aubs confirmed it by ear and the daemon validates
/// nothing (a wrong format is silence or wrong pitch, never an error): 16-bit mono PCM WAV at Kokoro's own 24 kHz,
/// peak-normalised and gently compressed (<see cref="Loudify"/>), uploaded, then played, then a wait for the clip's
/// length plus 250 ms - <c>play_sound</c> only QUEUES, and the next clip started early cuts this one off.
/// The head lifts clear of the chest speaker before each line (it muffles the sound otherwise).
/// </remarks>
public sealed class ReachyChannel : IVoiceChannel
{
    private readonly ReachyMiniClient _robot;
    private readonly RoseAudioLink _link;
    private readonly VertexAi _ai;
    private readonly SpeakerTracker _speakers;
    private MicrophoneEars? _ears;
    private int _clip;

    public string Name => "Reachy";
    public string HeardThrough => "to your robot body";
    /// <summary>
    /// Answer everything heard in the room (off: Vertex is called by name to start, then the conversation carries on).
    /// Off by default: the first live run (2026-10-12) answered room speech that was not meant for it ("and I hope you
    /// enjoyed this ..." - it sounded like a video playing nearby).
    /// </summary>
    public bool AnswersEverything { get; set; }
    public bool CanSpeak => true;
    /// <summary>A clip started (true) or finished (false) playing: the robot's body language follows it.</summary>
    public event Action<bool>? Playing;
    /// <summary>Lift the head clear of the chest speaker before speaking (Rose's recipe).</summary>
    public bool LiftHeadToSpeak { get; set; } = true;
    /// <summary>Who a voice nobody has been introduced to is called ("someone in the room").</summary>
    public string RoomSpeakerName { get; set; } = "someone in the room";

    /// <summary>Whether each utterance is matched to a voice. On by default: the voices Vertex has been introduced to are named.</summary>
    public bool IdentifySpeakers
    {
        get => _ears?.IdentifySpeakers ?? true;
        set { if (_ears != null) _ears.IdentifySpeakers = value; }
    }

    /// <summary>The voice tracker: who is talking, introductions, the people Vertex knows.</summary>
    public SpeakerTracker Speakers => _speakers;

    /// <summary>"I'm Nikki": the voice that just spoke is learned as that name from what it says next. Returns what to tell the model.</summary>
    public string IntroduceSpeaker(string name) => _speakers.BeginIntroduction(name, forNextVoice: false, Environment.TickCount64);

    /// <summary>"This is Nikki" about someone who has not spoken yet: the next voice nobody knows is learned as that name.</summary>
    public string IntroduceNextVoice(string name) => _speakers.BeginIntroduction(name, forNextVoice: true, Environment.TickCount64);

    public event Action<HeardLine>? Heard;
    public event Action<string>? Log;

    public ReachyMiniClient Robot => _robot;

    private ReachyChannel(string host, VertexAi ai)
    {
        _robot = new ReachyMiniClient(host);
        _link = new RoseAudioLink(host);
        _ai = ai;
        _speakers = new SpeakerTracker(ai.Speakers.Registry, changed: ai.SaveVoices, log: m => Log?.Invoke("[voice] " + m));
    }

    /// <summary>Connects the robot's microphone and loads the voice activity detector.</summary>
    public static async Task<ReachyChannel> ConnectAsync(string host, VertexAi ai, CancellationToken ct = default)
    {
        var channel = new ReachyChannel(host, ai);
        try
        {
            await channel.StartAsync(ct);
            return channel;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        _ears = await MicrophoneEars.CreateAsync(_ai, ct: ct);
        _ears.Log += m => Log?.Invoke("[ears] " + m);
        _ears.Heard += h =>
        {
            _speakers.Unknown = RoomSpeakerName;
            // Who sounds like they said it: an introduced person, a provisional guest, or (for a clip too short to tell, and with
            // nobody before it) the room. The key is stable per person, so what Vertex remembers about them follows them.
            var who = _speakers.Resolve(h.Speaker, h.EndedTick);
            Heard?.Invoke(new HeardLine(this, who.Key, who.Name, h.Text, true, h.EndedTick, h.SttMs));
        };
        _link.Log += m => Log?.Invoke("[link] " + m);
        // Subscribed BEFORE connecting: RoseAudioLink only starts its PCM capture when OnMicAudio has a handler.
        _link.OnMicAudio += pcm => _ears.Feed(pcm);
        await _link.ConnectAsync(ct);
    }

    /// <summary>Ears closed while Vertex talks here (with the robot's own echo cancellation on top).</summary>
    public bool Muted
    {
        get => _ears?.Muted ?? false;
        set { if (_ears != null) _ears.Muted = value; }
    }

    public async Task PlayAsync(float[] pcm24k, CancellationToken ct)
    {
        if (pcm24k.Length == 0) return;
        var pcm = Loudify(pcm24k);
        var name = $"vertex_{Interlocked.Increment(ref _clip) % 64}.wav"; // a small ring of names: the robot keeps every upload
        using (var wav = new MemoryStream(BuildWav(pcm, 24000)))
            await _robot.UploadSoundAsync(name, wav, ct);
        if (LiftHeadToSpeak)
            try { await _robot.GotoAsync(headPose: new XyzRpyPose(Z: ReachyBody.HeadLiftMax, Pitch: -0.05), duration: 0.4, interpolation: Interpolation.EaseInOut, ct: ct); }
            catch (Exception e) when (e is not OperationCanceledException) { /* posture never blocks speech */ }
        Playing?.Invoke(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _robot.PlaySoundAsync(name, ct);
            await Task.Delay(TimeSpan.FromSeconds(pcm.Length / 24000.0) + TimeSpan.FromMilliseconds(250), ct);
        }
        catch (OperationCanceledException)
        {
            try { await _robot.StopSoundAsync(CancellationToken.None); } catch { }
            throw;
        }
        finally
        {
            // Start and end of every clip, so non-overlap can be checked from the log, not only by ear (Reachy CLAUDE.md).
            Log?.Invoke($"played {name} ({pcm.Length / 24000.0:0.00} s) in {sw.Elapsed.TotalSeconds:0.00} s");
            Playing?.Invoke(false);
        }
    }

    public Task ShowTextAsync(string text, CancellationToken ct) => Task.CompletedTask;

    // Rose's loudness recipe (RoseVoice.Loudify): peak-normalise to 0.95, 3:1 above 0.25, makeup gain back to 0.95.
    // MEASURED on the robot: +4.1 dB RMS with the peak unchanged; TJ: "audibly louder". Kokoro only (a cloned voice
    // carries a tail the makeup gain turns into reverb).
    private const float PeakTarget = 0.95f, CompressorThreshold = 0.25f, CompressionRatio = 3.0f;

    internal static short[] Loudify(float[] samples)
    {
        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        var outPcm = new short[samples.Length];
        if (peak <= 0.0001f) return outPcm;
        var norm = PeakTarget / peak;
        var shaped = new float[samples.Length];
        var postPeak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i] * norm;
            var mag = Math.Abs(s);
            if (mag > CompressorThreshold) s = Math.Sign(s) * (CompressorThreshold + (mag - CompressorThreshold) / CompressionRatio);
            shaped[i] = s;
            postPeak = Math.Max(postPeak, Math.Abs(s));
        }
        var makeup = postPeak > 0.0001f ? PeakTarget / postPeak : 1f;
        for (var i = 0; i < shaped.Length; i++)
            outPcm[i] = (short)MathF.Round(Math.Clamp(shaped[i] * makeup, -1f, 1f) * 32767f);
        return outPcm;
    }

    internal static byte[] BuildWav(short[] pcm, int rate)
    {
        using var ms = new MemoryStream(44 + pcm.Length * 2);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(pcm.Length * 2);
            foreach (var s in pcm) w.Write(s);
        }
        return ms.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _link.DisposeAsync();
        if (_ears != null) await _ears.DisposeAsync();
        _robot.Dispose();
    }
}
