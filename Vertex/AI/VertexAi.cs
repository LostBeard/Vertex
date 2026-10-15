using ILGPU;
using ILGPU.Runtime;
using SpawnDev.AI;
using SpawnDev.AI.Server;

namespace Vertex.AI;

/// <summary>
/// Vertex's on-device AI, all on one GPU through the SpawnDev libraries: the language model (SpawnDev.AI's
/// AiChatEngine over GGUF models, with tool calling), speech-to-text (Whisper) and text-to-speech (Kokoro voices),
/// both from SpawnDev.ILGPU.ML via SpawnDev.AI.Server. Models come from the local Ollama cache or the SpawnDev hub
/// (cached on disk).
/// </summary>
public sealed class VertexAi : IAsyncDisposable
{
    private readonly Context _context;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SpawnDev.ILGPU.ML.Hub.IModelSource _models;

    public Accelerator Accelerator { get; }

    private Accelerator? _cpu;
    /// <summary>
    /// A CPU accelerator of its own, for the small models that must never wait for the GPU lane: the voice detector has to hear the next sentence WHILE the language model
    /// prefills the answer to the last one (about 1.5 s, GPU held), or a person who carries on cannot be told from one who stopped.
    /// </summary>
    public Accelerator CpuAccelerator => _cpu ??= _context.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(_context);
    public ModelRegistry Registry { get; }
    public AiChatEngine Chat { get; }
    public AiSpeechEngine Speech { get; }
    public AiVoiceEngine Voice { get; }
    /// <summary>Who is talking: voice embeddings and the voices Vertex has been introduced to.</summary>
    public AiSpeakerEngine Speakers { get; }
    /// <summary>Has the speaker finished their turn: Smart Turn v3.2, which lets the microphone answer sooner on a finished sentence and wait on an unfinished one.</summary>
    public AiTurnEngine Turns { get; }
    /// <summary>
    /// Speech to text that keeps up with the speaker (Nemotron Speech Streaming): the words appear while they are being said. The fast pass for reacting early;
    /// <see cref="Speech"/> still gives the final, more accurate text.
    /// </summary>
    public AiStreamingSpeechEngine Streaming { get; }
    /// <summary>
    /// Who is in front of the camera: YuNet finds faces, SFace gives each a fingerprint, and a registry of the faces that have been introduced says whose it is. The same shape as
    /// <see cref="Speakers"/>; strangers are "Visitor N" (a voice's strangers are "Guest N": they are not known to be the same person).
    /// </summary>
    public AiFaceEngine Faces { get; }
    /// <summary>The people Vertex knows - voice and face together by name - and the one place they are listed and removed from. Everything is local to this PC.</summary>
    public Vertex.People.PeopleDirectory People { get; }
    /// <summary>Where the introduced voices are kept between runs.</summary>
    public string VoicesFile { get; }
    /// <summary>Where the introduced faces are kept between runs (next to the voices; never uploaded).</summary>
    public string FacesFile { get; }
    public AiToolRegistry Tools { get; } = new();
    public Action<string>? Log { get => _log; set { _log = value; Lane.Log = value; } }
    private Action<string>? _log;
    /// <summary>Every model call goes through here, one at a time (see <see cref="GpuLane"/>).</summary>
    public GpuLane Lane { get; } = new();

    /// <summary>Speech to text: NVIDIA Parakeet TDT 0.6B v3 (the istupakov ONNX export, from the hub).</summary>
    /// <remarks>
    /// MEASURED on LibriSpeech test-clean (60 utterances, RTX 4070, idle): 3.75% word error rate at 168 ms median, against
    /// Whisper tiny's 11.53% at 177 ms (which this replaced; Whisper tiny is what mangled names). fp16-stored weights (needs
    /// SpawnDev.ILGPU.ML 5.3.9-local.2: before it the opt-in silently did nothing for this model): the same 3.75% at 197 ms
    /// against 163 ms, and 2,012 MiB loaded against 3,012 MiB - a GB back, which matters here: the language model, Kokoro and the sentence encoder share
    /// the card (a first live session spilled to system RAM and took 5.5 minutes to answer).
    /// </remarks>
    public const string SpeechRepo = "istupakov/parakeet-tdt-0.6b-v3-onnx";

    public VertexAi(Action<string>? log = null, string? voicesFile = null)
    {
        Log = log;
        _context = SpawnDev.ILGPU.ML.MLContext.Create().ToContext();
        // CUDA first: the desktop path. OpenCL/CPU keep it working anywhere, slowly.
        Accelerator = _context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda)?.CreateAccelerator(_context)
            ?? _context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL)?.CreateAccelerator(_context)
            ?? _context.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(_context);
        Log?.Invoke($"accelerator: {Accelerator.Name} ({Accelerator.AcceleratorType})");

        var modelStore = SpawnDev.ILGPU.ML.Hub.FileModelStore.Default();
        var modelSource = new SpawnDev.ILGPU.ML.Hub.HttpClientModelSource(_http, modelStore);
        _models = modelSource;
        Registry = new ModelRegistry(new OllamaCacheModelProvider(new OllamaModelStore()), Accelerator);
        Chat = new AiChatEngine(Registry)
        {
            Tools = Tools,
            // Vertex has its own tools; the demo's image/GitHub shortcuts do not apply to a game.
            ForceImageToolOnIntent = false,
            GroundGitHubOnIntent = false,
            MaxToolRounds = 4,
        };
        Speech = new AiSpeechEngine(modelSource, _http, Accelerator)
        {
            ModelRepo = SpeechRepo,
            ModelName = "parakeet-tdt-0.6b-v3",
            Family = SpeechModelFamily.ParakeetTdt,
            HalfWeights = true,
            OnLoadProgress = (stage, pct) => { if (pct % 50 == 0) Log?.Invoke($"speech model {stage} {pct}%"); },
        };
        Voice = new AiVoiceEngine(modelSource, _http, Accelerator)
        {
            OnLoadProgress = (stage, pct) => { if (pct % 50 == 0) Log?.Invoke($"voice model {stage} {pct}%"); },
        };
        VoicesFile = voicesFile ?? Path.Combine(Vertex.Host.VertexSettings.DefaultFolder, "voices.json");
        Speakers = new AiSpeakerEngine(modelSource, Accelerator)
        {
            Registry = LoadVoices(VoicesFile, log),
            // An embedding of under 2 s of speech is unreliable (the model card's own guidance); filing a "Guest" from household chatter at 1.5 s made four of them in an hour.
            MinGuestSeconds = 2.0,
            OnLoadProgress = (stage, pct) => { if (pct % 50 == 0) Log?.Invoke($"speaker model {stage} {pct}%"); },
        };
        Turns = new AiTurnEngine(modelSource, Accelerator)
        {
            OnLoadProgress = (stage, pct) => { if (pct % 50 == 0) Log?.Invoke($"turn model {stage} {pct}%"); },
        };
        Streaming = new AiStreamingSpeechEngine(modelSource, Accelerator)
        {
            OnLoadProgress = (stage, pct) => { if (pct % 50 == 0) Log?.Invoke($"live words model {stage} {pct}%"); },
        };
        FacesFile = Path.Combine(Path.GetDirectoryName(VoicesFile)!, "faces.json");
        Faces = new AiFaceEngine(modelSource, Accelerator)
        {
            Registry = LoadFaces(FacesFile, log),
            OnLoadProgress = (stage, pct) => { if (pct % 50 == 0) Log?.Invoke($"face model {stage} {pct}%"); },
        };
        // The rule for who is kept: a person who has been introduced stays until someone removes them; a stranger nobody introduces is forgotten after 24 hours.
        People = new Vertex.People.PeopleDirectory(Speakers.Registry, Faces.Registry, SaveVoices, SaveFaces, TimeSpan.FromHours(24));
        var gone = People.ExpireGuests();
        if (gone.Count > 0) log?.Invoke($"forgot {gone.Count} guest(s) nobody introduced in 24 hours: {string.Join(", ", gone)}");
        _guestExpiry = Task.Run(ExpireGuestsPeriodicallyAsync);
    }

    private static SpeakerRegistry LoadFaces(string file, Action<string>? log)
    {
        try
        {
            if (File.Exists(file))
            {
                var registry = SpeakerRegistry.FromJson(File.ReadAllText(file));
                registry.GuestPrefix = "Visitor";      // not part of the saved data
                log?.Invoke($"faces: {registry.Profiles.Count(p => !p.IsGuest)} known, {registry.Profiles.Count(p => p.IsGuest)} visitor(s)");
                return registry;
            }
        }
        catch (Exception e) { log?.Invoke($"faces: could not read {file} ({e.Message}); starting with none"); }
        return AiFaceEngine.NewRegistry();
    }

    private readonly object _facesLock = new();

    /// <summary>Writes the introduced faces (a temp file, then a replace).</summary>
    public void SaveFaces()
    {
        lock (_facesLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FacesFile)!);
                var tmp = FacesFile + ".tmp";
                File.WriteAllText(tmp, Faces.Registry.ToJson());
                File.Move(tmp, FacesFile, overwrite: true);
            }
            catch (Exception e) { Log?.Invoke($"faces: could not save ({e.Message})"); }
        }
    }

    private readonly CancellationTokenSource _stop = new();
    private Task? _guestExpiry;

    private async Task ExpireGuestsPeriodicallyAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var gone = People.ExpireGuests();
                if (gone.Count > 0) Log?.Invoke($"forgot {gone.Count} guest(s) nobody introduced in 24 hours: {string.Join(", ", gone)}");
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>What a look at the camera found, and when each named person had been seen BEFORE it (the look updates them), so an absence is measured against the true previous sighting.</summary>
    public sealed record FaceLook(IReadOnlyList<AiFaceResult> Results, IReadOnlyDictionary<string, long> PreviousLastSeen);

    /// <summary>One look at a camera picture (B, G, R, A): faces found, filed and named. Waits its turn on the GPU lane (not urgent: speech and reflexes go first).</summary>
    public async Task<FaceLook> LookAsync(byte[] bgra, int width, int height, CancellationToken ct = default)
    {
        using (await Lane.EnterAsync(ct: ct))
        {
            var before = Faces.Registry.Profiles.Where(p => !p.IsGuest).ToDictionary(p => p.Name, p => p.LastHeardTicks);
            var results = await Faces.LookAsync(bgra, width, height, observe: true, ct);
            if (results.Any(r => r.Guest != null || r.Match is { Known: true })) SaveFacesThrottled();
            return new FaceLook(results, before);
        }
    }

    private long _lastFacesSave;
    /// <summary>Faces are seen several times a second; the file is written at most every ten seconds (an introduction saves at once).</summary>
    private void SaveFacesThrottled()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastFacesSave) < 10_000) return;
        Interlocked.Exchange(ref _lastFacesSave, now);
        SaveFaces();
    }

    /// <summary>Loads and warms the face models before anyone is looked at (kernels compile on first execution).</summary>
    public async Task WarmFacesAsync(CancellationToken ct = default)
    {
        using (await Lane.EnterAsync(ct: ct))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Faces.EnsureReadyAsync(ct);
            Log?.Invoke($"warmed up: faces {clock.ElapsedMilliseconds} ms");
        }
    }

    private static SpeakerRegistry LoadVoices(string file, Action<string>? log)
    {
        try
        {
            if (File.Exists(file))
            {
                var registry = SpeakerRegistry.FromJson(File.ReadAllText(file));
                log?.Invoke($"voices: {registry.Profiles.Count} known ({string.Join(", ", registry.Profiles.Select(p => p.Name))})");
                return registry;
            }
        }
        catch (Exception e) { log?.Invoke($"voices: could not read {file} ({e.Message}); starting with none"); }
        return new SpeakerRegistry();
    }

    private readonly object _voicesLock = new();

    /// <summary>Writes the introduced voices (a temp file, then a replace: a crash never leaves half a file).</summary>
    public void SaveVoices()
    {
        lock (_voicesLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(VoicesFile)!);
                var tmp = VoicesFile + ".tmp";
                File.WriteAllText(tmp, Speakers.Registry.ToJson());
                File.Move(tmp, VoicesFile, overwrite: true);
            }
            catch (Exception e) { Log?.Invoke($"voices: could not save ({e.Message})"); }
        }
    }

    /// <summary>
    /// Who this speech sounds like (an enrolled person, else a provisional guest), on the GPU lane as urgent work like the
    /// transcription it accompanies. Saves the voices when a guest was created or grew.
    /// </summary>
    public async Task<AiSpeakerResult> ObserveSpeakerAsync(float[] pcm16k, CancellationToken ct = default)
    {
        AiSpeakerResult result;
        using (await Lane.EnterAsync(urgent: true, ct))
            result = await Speakers.ObserveAsync(pcm16k, 16000, ct);
        if (result.Guest != null) SaveVoices();
        return result;
    }

    /// <summary>
    /// P(the speaker has finished their turn) for the audio heard so far (16 kHz mono; the end is "now"), as urgent work on the GPU lane
    /// like the transcription it gates.
    /// </summary>
    public async Task<float> PredictTurnEndAsync(float[] pcm16k, CancellationToken ct = default)
    {
        using (await Lane.EnterAsync(urgent: true, ct))
            return await Turns.TurnCompleteAsync(pcm16k, 16000, ct);
    }

    /// <summary>Loads and warms the live-words model (see <see cref="Streaming"/>): kernels compile on first execution, and the first sentence should not pay for it.</summary>
    public async Task WarmLiveWordsAsync(CancellationToken ct = default)
    {
        using (await Lane.EnterAsync(ct: ct))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Streaming.EnsureReadyAsync(ct);
            Log?.Invoke($"warmed up: live words {clock.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>One piece of audio into a live-words stream, as urgent work on the GPU lane (a chunk is ~67 ms; hearing never waits for a reply to finish).</summary>
    public async Task<SpawnDev.ILGPU.ML.Pipelines.StreamingUpdate> LiveAcceptAsync(AiSpeechStream stream, float[] pcm16k, CancellationToken ct = default)
    {
        using (await Lane.EnterAsync(urgent: true, ct))
            return await stream.AcceptAsync(pcm16k);
    }

    /// <summary>The fast command matcher (System One for speech), loaded on first use.</summary>
    public FastIntents? Intents { get; private set; }

    public async Task<FastIntents> LoadIntentsAsync(CancellationToken ct = default)
    {
        if (Intents != null) return Intents;
        using (await Lane.EnterAsync(ct: ct))
            return Intents ??= await FastIntents.LoadAsync(_models, Accelerator, Lane, ct);
    }

    /// <summary>
    /// Loads and warms speech to text and the voice before anyone talks: kernels compile on FIRST EXECUTION, not at
    /// load, so a model that is only loaded makes the first thing anybody says wait for its compile (Rose's first
    /// reply: 6.4 s cold, 1.0 s warmed). Whisper runs once on silence (its own warm pass); Kokoro says one short
    /// line in <paramref name="voiceId"/> and the audio is thrown away.
    /// </summary>
    public async Task WarmUpAsync(string voiceId, CancellationToken ct = default)
    {
        using (await Lane.EnterAsync(ct: ct))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Speech.EnsureReadyAsync(ct);
            await Speakers.EnsureReadyAsync(ct);
            await Turns.EnsureReadyAsync(ct);
            var ears = clock.ElapsedMilliseconds;
            await Voice.SpeakWithVoiceAsync("Hello there.", voiceId, ct: ct);
            Log?.Invoke($"warmed up: speech to text {ears} ms, voice {clock.ElapsedMilliseconds - ears} ms");
        }
    }

    /// <summary>Loads the chat model (weights to the GPU, a first pass) before anyone talks.</summary>
    public async Task EnsureChatReadyAsync(string model, CancellationToken ct = default)
    {
        using (await Lane.EnterAsync(ct: ct))
            await Chat.EnsureReadyAsync(model);
    }

    /// <summary>
    /// One language-model call (with tools: the engine runs them between passes). Holds the GPU for the whole call
    /// but steps aside between tokens when speech is waiting to be transcribed (<see cref="GpuLane.YieldAsync"/>).
    /// </summary>
    public async Task<AiChatResult> ChatAsync(AiChatRequest request, Func<string, Task>? onDelta, CancellationToken ct = default)
    {
        using (var hold = await Lane.EnterAsync(ct: ct))
        {
            async Task Delta(string d)
            {
                await hold.YieldAsync(ct);
                if (onDelta != null) await onDelta(d);
            }
            return await Chat.ChatStreamAsync(request, Delta, ct);
        }
    }

    /// <summary>The chat models available locally (Ollama cache) by name.</summary>
    public async Task<IReadOnlyList<string>> ModelNamesAsync(CancellationToken ct = default) =>
        (await Chat.ListModelsAsync(ct)).Select(m => m.Name).ToList();

    /// <summary>Whisper on 16 kHz mono float PCM.</summary>
    public async Task<string> TranscribeAsync(float[] pcm16k, CancellationToken ct = default)
    {
        using (await Lane.EnterAsync(urgent: true, ct))
        {
            var result = await Speech.TranscribeAsync(pcm16k, 16000, ct);
            return result.Text.Trim();
        }
    }

    /// <summary>Kokoro (built-in voices, "kokoro:af_heart" etc.): text in, 24 kHz mono float PCM out.</summary>
    public async Task<(float[] Samples, int SampleRate)> SpeakAsync(string text, string voiceId, CancellationToken ct = default)
    {
        using (await Lane.EnterAsync(ct: ct))
        {
            var speech = await Voice.SpeakWithVoiceAsync(text, voiceId, ct: ct);
            return (speech.Samples, speech.SampleRate);
        }
    }

    public static IReadOnlyList<string> Voices => AiVoiceEngine.KokoroVoiceIds;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_guestExpiry != null) { try { await _guestExpiry; } catch { } }
        Intents?.Dispose();
        Speech.Dispose();
        Speakers.Dispose();
        Turns.Dispose();
        Streaming.Dispose();
        Faces.Dispose();
        Voice.Dispose();
        await Registry.DisposeAsync();
        Accelerator.Dispose();
        _context.Dispose();
        _http.Dispose();
    }
}
