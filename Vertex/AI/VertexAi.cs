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
    public ModelRegistry Registry { get; }
    public AiChatEngine Chat { get; }
    public AiSpeechEngine Speech { get; }
    public AiVoiceEngine Voice { get; }
    /// <summary>Who is talking: voice embeddings and the voices Vertex has been introduced to.</summary>
    public AiSpeakerEngine Speakers { get; }
    /// <summary>Where the introduced voices are kept between runs.</summary>
    public string VoicesFile { get; }
    public AiToolRegistry Tools { get; } = new();
    public Action<string>? Log { get; set; }
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
            OnLoadProgress = (stage, pct) => { if (pct % 50 == 0) Log?.Invoke($"speaker model {stage} {pct}%"); },
        };
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
        using (await Lane.EnterAsync(ct: ct))
        {
            async Task Delta(string d)
            {
                await Lane.YieldAsync(ct);
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
        Intents?.Dispose();
        Speech.Dispose();
        Speakers.Dispose();
        Voice.Dispose();
        await Registry.DisposeAsync();
        Accelerator.Dispose();
        _context.Dispose();
        _http.Dispose();
    }
}
