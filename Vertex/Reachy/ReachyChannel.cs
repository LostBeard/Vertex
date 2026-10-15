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
public sealed class ReachyChannel : IPreparingVoiceChannel
{
    private readonly ReachyMiniClient _robot;
    private readonly RoseAudioLink _link;
    private readonly VertexAi _ai;
    private readonly SpeakerTracker _speakers;
    private MicrophoneEars? _ears;
    private bool _faces;
    private bool _streamSpeech;
    private Vertex.People.FaceWatcher? _watcher;
    private int _looking;
    private readonly CancellationTokenSource _voiceCts = new();
    private Task? _voicePoll;
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

    /// <summary>Keep every heard utterance (audio, words, who) in this folder; null (the default) keeps nothing. See <see cref="MicrophoneEars.SaveFolder"/>.</summary>
    public string? SaveHeardFolder
    {
        get => _ears?.SaveFolder;
        set { if (_ears != null) _ears.SaveFolder = value; }
    }

    /// <summary>The voice tracker: who is talking, introductions, the people Vertex knows.</summary>
    public SpeakerTracker Speakers => _speakers;

    /// <summary>"I'm Nikki": the voice that just spoke is learned as that name from what it says next. Returns what to tell the model.</summary>
    public string IntroduceSpeaker(string name) => _speakers.BeginIntroduction(name, forNextVoice: false, Environment.TickCount64);

    /// <summary>"This is Nikki" about someone who has not spoken yet: the next voice nobody knows is learned as that name.</summary>
    public string IntroduceNextVoice(string name) => _speakers.BeginIntroduction(name, forNextVoice: true, Environment.TickCount64);

    public event Action<HeardLine>? Heard;
    public event Action<string>? Log;
    /// <summary>The words of what is being said right now, as they are said (text so far; true when the utterance is over). Only with live words on.</summary>
    public event Action<string, bool>? Hearing;

    public ReachyMiniClient Robot => _robot;

    /// <summary>The people in view of the robot's camera (empty when faces are off or the head is down).</summary>
    public IReadOnlyList<Vertex.People.VisiblePerson> InView => _watcher?.Visible(Environment.TickCount64) ?? [];

    /// <summary>The faces in the latest look at the camera (this look only, with their places in the picture), raised after every look.</summary>
    public event Action<IReadOnlyList<Vertex.People.VisiblePerson>, long>? FacesSeen;

    /// <summary>Someone the robot knows came back after a long absence (see <see cref="Vertex.People.FaceWatcher"/>): the moment to say hi.</summary>
    public event Action<Vertex.People.Arrival>? Arrived;

    /// <summary>Where the voice the robot is hearing comes from (the microphone array's direction of arrival, smoothed).</summary>
    public VoiceDirection Voice { get; } = new();

    private bool _smartTurn = true;
    private bool _liveWords;

    private ReachyChannel(string host, VertexAi ai)
    {
        _robot = new ReachyMiniClient(host);
        _link = new RoseAudioLink(host);
        _ai = ai;
        _speakers = new SpeakerTracker(ai.Speakers.Registry, changed: ai.SaveVoices, log: m => Log?.Invoke("[voice] " + m));
    }

    /// <summary>Connects the robot's microphone and loads the voice activity detector.</summary>
    /// <param name="smartTurn">Ask the end-of-turn model whether a pause is the end of what someone is saying (see <see cref="TurnAssembler"/>), instead of always waiting half a second.</param>
    /// <param name="liveWords">Also recognise the words WHILE they are spoken (streaming speech to text), reported through <see cref="Hearing"/>.</param>
    /// <param name="faces">Look at the camera: who is in view, learn the faces of people who are introduced, notice someone coming back after a long absence.</param>
    /// <param name="streamSpeech">Send speech to the robot as a live stream over WebRTC (no upload wait; Opus) instead of uploading each sentence as a WAV.</param>
    public static async Task<ReachyChannel> ConnectAsync(string host, VertexAi ai, bool smartTurn = true, bool liveWords = false, bool faces = false, bool streamSpeech = false, CancellationToken ct = default)
    {
        var channel = new ReachyChannel(host, ai) { _smartTurn = smartTurn, _liveWords = liveWords, _faces = faces, _streamSpeech = streamSpeech };
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
        _ears = await MicrophoneEars.CreateAsync(_ai, smartTurn: _smartTurn, liveWords: _liveWords, ct: ct);
        _ears.Log += m => Log?.Invoke("[ears] " + m);
        _ears.Hearing += (text, final) => Hearing?.Invoke(text, final);
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
        _link.StreamSpeech = _streamSpeech;
        if (_faces)
        {
            await _ai.WarmFacesAsync(ct);
            _watcher = new Vertex.People.FaceWatcher(_ai.Faces.Registry, _ai.SaveFaces, m => Log?.Invoke("[faces] " + m));
            _watcher.Arrived += a => Arrived?.Invoke(a);
            _speakers.Introduced += (name, forNext) => { _watcher.BeginIntroduction(name, forNext, Environment.TickCount64); _ = Task.Run(_ai.SaveFaces); };
            // Subscribed BEFORE connecting, like the microphone: the picture decoder only runs when a handler is there. Two looks a second is plenty for a room.
            _link.VideoMaxFramesPerSecond = 2;
            _link.OnVideoFrame += OnPicture;
        }
        await _link.ConnectAsync(ct);
        _voicePoll = Task.Run(() => PollVoiceDirectionAsync(_voiceCts.Token));
    }

    /// <summary>A camera picture: one look at a time (a picture that arrives while the last is still being looked at is dropped - the room has not changed in 500 ms).</summary>
    private void OnPicture(SpawnDev.RTC.Video.RTCVideoFrame frame)
    {
        if (_watcher == null || Interlocked.Exchange(ref _looking, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var look = await _ai.LookAsync(frame.Bgra, frame.Width, frame.Height, _voiceCts.Token);
                var tick = Environment.TickCount64;
                _watcher.Observe(look.Results, look.PreviousLastSeen, tick, frameWidth: frame.Width);
                FacesSeen?.Invoke(_watcher.Visible(tick).Where(v => v.LastSeenTick == tick).ToList(), tick);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log?.Invoke("[faces] look failed: " + e.Message); }
            finally { Volatile.Write(ref _looking, 0); }
        });
    }

    /// <summary>
    /// The daemon reads the direction lazily: its first answer is null and values only come to a client that keeps asking, so this asks five times a second for as long
    /// as the robot is connected. Reads are a few bytes over the same network as the audio; a failed one is simply skipped.
    /// </summary>
    private async Task PollVoiceDirectionAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await _robot.GetDoaAsync(ct) is { } doa) Voice.Add(doa.Angle, doa.SpeechDetected, Environment.TickCount64);
                failures = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                if (++failures == 5) Log?.Invoke("[voice direction] unavailable: " + e.Message);
            }
            try { await Task.Delay(failures >= 5 ? 2000 : 200, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Ears closed while Vertex talks here (with the robot's own echo cancellation on top).</summary>
    public bool Muted
    {
        get => _ears?.Muted ?? false;
        set { if (_ears != null) _ears.Muted = value; }
    }

    /// <summary>A clip uploaded to the robot and ready to play.</summary>
    private sealed record Prepared(string Name, int Samples);

    public async Task PlayAsync(float[] pcm24k, CancellationToken ct)
    {
        if (pcm24k.Length == 0) return;
        await PlayPreparedAsync(await PrepareAsync(pcm24k, ct), ct);
    }

    /// <summary>A clip uploaded to the robot over its data channel (the way Pollen's SDK and the SpawnDev.AI demo send speech) and ready to play.</summary>
    private sealed record DataPrepared(string UploadId, int Samples, short[] Pcm, bool Flac);

    /// <summary>Set once the robot refuses a FLAC clip: from then on clips go up as WAV.</summary>
    private volatile bool _flacRefused;

    /// <summary>When the last clip finished playing (Environment.TickCount64), to log the gap before the next one.</summary>
    private long _lastClipEndTick;

    /// <summary>A clip ready to be streamed: loudened, at the stream's 48 kHz.</summary>
    private sealed record StreamClip(short[] Pcm48k);

    /// <summary>Loudens the clip and uploads it to the robot (a few seconds of Wi-Fi for a long sentence), so playing it later is only a command. When speech is streamed there is nothing to upload.</summary>
    public async Task<object> PrepareAsync(float[] pcm24k, CancellationToken ct)
    {
        if (pcm24k.Length == 0) return new Prepared("", 0);
        var pcm = Loudify(pcm24k);
        if (_link.Speech != null) return new StreamClip(Upsample2x(pcm));
        // The robot's data channel first: the same upload the SDK and the Hugging Face demo use, with no multipart parsing on the robot (the REST upload measured 50 KB/s on this robot). The sound is the
        // same WAV played by the same daemon call; only the transfer differs. REST stays as the fallback.
        if (_link.Data is { IsOpen: true } data)
        {
            try { return await UploadOverDataChannelAsync(data, pcm, _flacRefused, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log?.Invoke($"data channel upload failed ({e.Message}); using the REST upload");
            }
        }
        var name = $"vertex_{Interlocked.Increment(ref _clip) % 64}.wav"; // a small ring of names: the robot keeps every upload
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using (var wav = new MemoryStream(BuildWav(pcm, 24000)))
            await _robot.UploadSoundAsync(name, wav, ct);
        Log?.Invoke($"uploaded {name} ({pcm.Length * 2 / 1024} KB) in {clock.ElapsedMilliseconds} ms");
        return new Prepared(name, pcm.Length);
    }

    /// <summary>Uploads over the data channel. FLAC is lossless and about half the bytes of the WAV, and the bytes are the wait: the robot stores the file under the extension in the encoding name and decodes it by content.</summary>
    private async Task<DataPrepared> UploadOverDataChannelAsync(RobotDataChannel data, short[] pcm, bool wavOnly, CancellationToken ct)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var wav = BuildWav(pcm, 24000);
        var flac = wavOnly ? null : FlacEncoder.Encode(pcm, 24000);
        var useFlac = flac != null && flac.Length < wav.Length;
        var bytes = useFlac ? flac! : wav;
        var id = await data.UploadAudioAsync(bytes, useFlac ? "flac-base64" : "wav-base64", ct: ct);
        Log?.Invoke($"uploaded {id} over the data channel ({bytes.Length / 1024} KB{(useFlac ? $" FLAC, {100 * bytes.Length / wav.Length}% of the WAV" : "")}) in {timer.ElapsedMilliseconds} ms");
        return new DataPrepared(id, pcm.Length, pcm, useFlac);
    }

    /// <summary>Plays a clip made ready by <see cref="PrepareAsync"/>.</summary>
    public async Task PlayPreparedAsync(object prepared, CancellationToken ct)
    {
        if (prepared is StreamClip stream) { await StreamAsync(stream.Pcm48k, ct); return; }
        if (prepared is DataPrepared uploaded) { await PlayUploadedAsync(uploaded, ct); return; }
        var (name, samples) = (Prepared)prepared;
        if (samples == 0) return;
        if (LiftHeadToSpeak)
            try { await _robot.GotoAsync(headPose: new XyzRpyPose(Z: ReachyBody.HeadLiftMax, Pitch: -0.05), duration: 0.4, interpolation: Interpolation.EaseInOut, ct: ct); }
            catch (Exception e) when (e is not OperationCanceledException) { /* posture never blocks speech */ }
        Playing?.Invoke(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _robot.PlaySoundAsync(name, ct);
            await Task.Delay(TimeSpan.FromSeconds(samples / 24000.0) + TimeSpan.FromMilliseconds(250), ct);
        }
        catch (OperationCanceledException)
        {
            try { await _robot.StopSoundAsync(CancellationToken.None); } catch { }
            throw;
        }
        finally
        {
            // Start and end of every clip, so non-overlap can be checked from the log, not only by ear (Reachy CLAUDE.md).
            Log?.Invoke($"played {name} ({samples / 24000.0:0.00} s) in {sw.Elapsed.TotalSeconds:0.00} s");
            Playing?.Invoke(false);
        }
    }

    /// <summary>Plays a clip uploaded over the data channel: head up, the daemon plays it, and the wait is the clip's own length (there is no "finished" event for standalone audio).</summary>
    private async Task PlayUploadedAsync(DataPrepared clip, CancellationToken ct)
    {
        var data = _link.Data!;
        if (LiftHeadToSpeak)
            try { await _robot.GotoAsync(headPose: new XyzRpyPose(Z: ReachyBody.HeadLiftMax, Pitch: -0.05), duration: 0.4, interpolation: Interpolation.EaseInOut, ct: ct); }
            catch (Exception e) when (e is not OperationCanceledException) { /* posture never blocks speech */ }
        Playing?.Invoke(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var commandTick = Environment.TickCount64;
            try { await data.PlayUploadedAudioAsync(clip.UploadId, ct: ct); }
            catch (InvalidOperationException e) when (clip.Flac && e.Message.Contains("would not play"))
            {
                // This robot's player will not take FLAC: remember it, send the same clip as a WAV and carry on.
                _flacRefused = true;
                Log?.Invoke($"the robot would not play a FLAC clip ({e.Message}); sending WAV from now on");
                clip = await UploadOverDataChannelAsync(data, clip.Pcm, wavOnly: true, ct);
                await data.PlayUploadedAudioAsync(clip.UploadId, ct: ct);
            }
            // Two numbers that say where a pause between sentences came from: how long the robot took to confirm it had the clip and started it (the part of the transfer that
            // was NOT done ahead of time), and how long the speaker was silent since the previous clip.
            var startedTick = Environment.TickCount64;
            var gap = _lastClipEndTick == 0 ? -1 : startedTick - _lastClipEndTick;
            Log?.Invoke($"clip {clip.UploadId} started {startedTick - commandTick} ms after the play command" + (gap >= 0 && gap < 60_000 ? $"; the speaker was silent for {gap} ms before it" : ""));
            await Task.Delay(TimeSpan.FromSeconds(clip.Samples / 24000.0) + TimeSpan.FromMilliseconds(250), ct);
            _lastClipEndTick = Environment.TickCount64;
        }
        catch (OperationCanceledException)
        {
            data.CancelAudio(clip.UploadId);
            throw;
        }
        finally
        {
            Log?.Invoke($"played {clip.UploadId} ({clip.Samples / 24000.0:0.00} s) in {sw.Elapsed.TotalSeconds:0.00} s");
            Playing?.Invoke(false);
        }
    }

    /// <summary>Speaks a clip through the live stream to the robot: head up, audio out in real time, returns when it has been heard. An interruption drops what is not yet sent AND what the robot has buffered.</summary>
    private async Task StreamAsync(short[] pcm48k, CancellationToken ct)
    {
        var speech = _link.Speech!;
        if (LiftHeadToSpeak)
            try { await _robot.GotoAsync(headPose: new XyzRpyPose(Z: ReachyBody.HeadLiftMax, Pitch: -0.05), duration: 0.4, interpolation: Interpolation.EaseInOut, ct: ct); }
            catch (Exception e) when (e is not OperationCanceledException) { /* posture never blocks speech */ }
        Playing?.Invoke(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { await speech.SpeakAsync(pcm48k, ct); }
        catch (OperationCanceledException)
        {
            speech.Flush();
            try { await _robot.ClearIncomingAudioAsync(CancellationToken.None); } catch { }
            throw;
        }
        finally
        {
            Log?.Invoke($"streamed a clip ({pcm48k.Length / (double)RobotSpeechStreamer.SampleRate:0.00} s) in {sw.Elapsed.TotalSeconds:0.00} s");
            Playing?.Invoke(false);
        }
    }

    /// <summary>24 kHz to 48 kHz by linear interpolation (the stream's rate is the WebRTC Opus rate).</summary>
    internal static short[] Upsample2x(short[] pcm24k)
    {
        var output = new short[pcm24k.Length * 2];
        for (var i = 0; i < pcm24k.Length; i++)
        {
            var a = pcm24k[i];
            var b = i + 1 < pcm24k.Length ? pcm24k[i + 1] : a;
            output[2 * i] = a;
            output[2 * i + 1] = (short)((a + b) / 2);
        }
        return output;
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
        _voiceCts.Cancel();
        if (_voicePoll != null) { try { await _voicePoll; } catch { } }
        if (_ears != null) await _ears.DisposeAsync();
        _robot.Dispose();
    }
}
