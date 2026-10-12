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
    public AiToolRegistry Tools { get; } = new();
    public Action<string>? Log { get; set; }
    /// <summary>Every model call goes through here, one at a time (see <see cref="GpuLane"/>).</summary>
    public GpuLane Lane { get; } = new();

    public VertexAi(Action<string>? log = null)
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
            OnLoadProgress = (stage, pct) => { if (pct % 50 == 0) Log?.Invoke($"speech model {stage} {pct}%"); },
        };
        Voice = new AiVoiceEngine(modelSource, _http, Accelerator)
        {
            OnLoadProgress = (stage, pct) => { if (pct % 50 == 0) Log?.Invoke($"voice model {stage} {pct}%"); },
        };
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
        Voice.Dispose();
        await Registry.DisposeAsync();
        Accelerator.Dispose();
        _context.Dispose();
        _http.Dispose();
    }
}
