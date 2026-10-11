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

    public async Task<FastIntents> LoadIntentsAsync(CancellationToken ct = default) =>
        Intents ??= await FastIntents.LoadAsync(_models, Accelerator, ct);

    /// <summary>The chat models available locally (Ollama cache) by name.</summary>
    public async Task<IReadOnlyList<string>> ModelNamesAsync(CancellationToken ct = default) =>
        (await Chat.ListModelsAsync(ct)).Select(m => m.Name).ToList();

    /// <summary>Whisper on 16 kHz mono float PCM.</summary>
    public async Task<string> TranscribeAsync(float[] pcm16k, CancellationToken ct = default)
    {
        var result = await Speech.TranscribeAsync(pcm16k, 16000, ct);
        return result.Text.Trim();
    }

    /// <summary>Kokoro (built-in voices, "kokoro:af_heart" etc.): text in, 24 kHz mono float PCM out.</summary>
    public async Task<(float[] Samples, int SampleRate)> SpeakAsync(string text, string voiceId, CancellationToken ct = default)
    {
        var speech = await Voice.SpeakWithVoiceAsync(text, voiceId, ct: ct);
        return (speech.Samples, speech.SampleRate);
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
