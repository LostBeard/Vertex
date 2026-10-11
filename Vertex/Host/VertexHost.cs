using System.Text.Json;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.AI;
using Vertex.Brain;

namespace Vertex.Host;

/// <summary>What the dashboard remembers between runs (%APPDATA%\Vertex\settings.json).</summary>
public sealed class VertexSettings
{
    public string Server { get; set; } = "mc.spawndev.com";
    public int Port { get; set; } = 25565;
    /// <summary>Signed-in Microsoft account slot ("default"), or empty to play offline as OfflineName.</summary>
    public string Account { get; set; } = "default";
    public string OfflineName { get; set; } = "Vertex";
    public string Character { get; set; } = "Vertex";
    public string Model { get; set; } = "";
    public bool Voice { get; set; } = true;
    /// <summary>
    /// The server's modpack (a Modrinth .mrpack URL or file), for learning its modded blocks the way a player gets
    /// them. Empty: AubsCraft's pack on mc.spawndev.com, none elsewhere.
    /// </summary>
    public string Modpack { get; set; } = "";

    /// <summary>The modpack to learn this server's blocks from, or null.</summary>
    public string? ModpackForServer() =>
        !string.IsNullOrWhiteSpace(Modpack) ? Modpack.Trim()
        : Server.Equals("mc.spawndev.com", StringComparison.OrdinalIgnoreCase) ? "https://map.spawndev.com:44365/api/pc/modpack/1.21.5"
        : null;

    private string _path = "";

    /// <summary>The settings in a data folder (default %APPDATA%\Vertex).</summary>
    public static VertexSettings Load(string? folder = null)
    {
        var path = System.IO.Path.Combine(folder ?? DefaultFolder, "settings.json");
        VertexSettings settings;
        try { settings = JsonSerializer.Deserialize<VertexSettings>(File.ReadAllText(path)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { settings = new(); }
        settings._path = path;
        return settings;
    }

    public static string DefaultFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vertex");

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public enum LineKind { System, Chat, Heard, Said, Tool, Error }

public sealed record LogLine(DateTime Time, LineKind Kind, string Text);

public enum HostState { Idle, Starting, Playing, Stopping }

/// <summary>
/// Runs Vertex for the dashboard: the AI (loaded once, kept), and per play session the Minecraft client, voice chat
/// and brain. Everything the dashboard shows comes from here; Changed tells it to redraw.
/// </summary>
public sealed class VertexHost : IAsyncDisposable
{
    private readonly List<LogLine> _lines = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _dataFolder;
    private bool _learnedThisSession;

    /// <summary>Tests: start from the modded blocks this world has learned only (no built-in catalog).</summary>
    internal bool UseEmbeddedModdedBlocks { get; set; } = true;
    /// <summary>Where downloaded mods, the Fabric server and Java are kept.</summary>
    internal string ModsCache { get; set; } = Mods.ModpackBlocks.DefaultCache;

    /// <summary>What Vertex has learned about this server's modded blocks (next to its world memory).</summary>
    private string WorldBlocksFile => System.IO.Path.Combine(_dataFolder, "worlds",
        string.Concat($"{Settings.Server}_{Settings.Port}".ToLowerInvariant().Split(System.IO.Path.GetInvalidFileNameChars())) + ".blocks.json");
    private VertexAi? _ai;
    private MinecraftClient? _client;
    private SimpleVoiceChat? _voice;
    private VertexBrain? _brain;

    public VertexSettings Settings { get; }
    public CharacterStore Characters { get; }

    public VertexHost() : this(null) { }

    /// <summary>A host keeping its settings, characters and world memory in `dataFolder` (default %APPDATA%\Vertex).</summary>
    public VertexHost(string? dataFolder)
    {
        _dataFolder = dataFolder ?? VertexSettings.DefaultFolder;
        Settings = VertexSettings.Load(dataFolder);
        Characters = new CharacterStore(System.IO.Path.Combine(dataFolder ?? VertexSettings.DefaultFolder, "characters"));
    }
    public HostState State { get; private set; } = HostState.Idle;
    public string Status { get; private set; } = "Not playing";
    /// <summary>Shown while a Microsoft sign-in waits for the device code to be approved.</summary>
    public DeviceCodePrompt? SignInPrompt { get; private set; }
    public IReadOnlyList<string> Models { get; private set; } = [];
    public MinecraftClient? Client => _client;
    public SimpleVoiceChat? Voice => _voice;
    public VertexBrain? Brain => _brain;

    public event Action? Changed;

    public IReadOnlyList<LogLine> Lines { get { lock (_lines) return _lines.ToList(); } }

    private void Add(LineKind kind, string text)
    {
        lock (_lines)
        {
            _lines.Add(new LogLine(DateTime.Now, kind, text));
            if (_lines.Count > 500) _lines.RemoveRange(0, _lines.Count - 500);
        }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {kind}: {text}");
        Changed?.Invoke();
    }

    private void SetStatus(HostState state, string status)
    {
        State = state;
        Status = status;
        Changed?.Invoke();
    }

    /// <summary>Loads the AI (GPU) once and lists the chat models.</summary>
    public async Task EnsureAiAsync()
    {
        if (_ai != null) return;
        _ai = new VertexAi(m => Add(LineKind.System, m));
        Models = await _ai.ModelNamesAsync();
        if (string.IsNullOrEmpty(Settings.Model) || !Models.Contains(Settings.Model))
            Settings.Model = Models.FirstOrDefault(m => m.StartsWith("llama3.1")) ?? Models.FirstOrDefault() ?? "";
        Changed?.Invoke();
    }

    public async Task StartAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (State != HostState.Idle) return;
            Settings.Save();
            SetStatus(HostState.Starting, "Loading the AI...");
            await EnsureAiAsync();
            if (string.IsNullOrEmpty(Settings.Model)) throw new InvalidOperationException("no chat model in the Ollama cache (ollama pull llama3.1:8b)");
            SetStatus(HostState.Starting, $"Loading {Settings.Model}...");
            await _ai!.Chat.EnsureReadyAsync(Settings.Model);
            await LoadIntentsAsync();

            GameProfile profile;
            if (string.IsNullOrWhiteSpace(Settings.Account)) profile = GameProfile.Offline(Settings.OfflineName);
            else
            {
                SetStatus(HostState.Starting, "Signing in...");
                var auth = new MicrosoftAuth(DpapiAccountStore.ForAccount("Vertex", Settings.Account), p =>
                {
                    SignInPrompt = p;
                    Changed?.Invoke();
                    return Task.CompletedTask;
                });
                profile = await auth.SignInAsync();
                SignInPrompt = null;
            }

            SetStatus(HostState.Starting, $"Joining {Settings.Server}...");
            // Brand "vertex": honest, and not "vanilla" (AubsCraft's proxy keeps vanilla clients off modded servers
            // because they cannot answer Fabric's registry sync; Vertex can).
            _client = new MinecraftClient(profile, new ClientOptions
            {
                Brand = "vertex",
                Log = m => { if (m.StartsWith("disconnected") || m.StartsWith("joined") || m.StartsWith("spawned") || m.StartsWith("modded blocks")) Add(LineKind.System, m); },
                ModdedBlocks = Mods.ModpackBlocks.LoadWorld(WorldBlocksFile),
                UseEmbeddedModdedBlocks = UseEmbeddedModdedBlocks,
            });
            _client.ChatReceived += m => Add(LineKind.Chat, m.Text);
            _client.Disconnected += r => { Add(LineKind.System, "disconnected: " + r); _ = StopAsync(); };
            if (Settings.Voice)
            {
                _voice = new SimpleVoiceChat(_client, Settings.Server);
                _voice.Log += m => Add(LineKind.System, "voice: " + m);
            }
            var character = Characters.Find(Settings.Character) ?? Character.BuiltIn[0];
            _brain = new VertexBrain(_client, _voice, _ai, Characters, character, Settings.Model, $"{Settings.Server}_{Settings.Port}");
            _brain.Log += m =>
            {
                if (m.StartsWith("> ")) Add(LineKind.Heard, m[2..]);
                else if (m.StartsWith("< ")) Add(LineKind.Said, m[2..]);
                else if (m.TrimStart().StartsWith("tool ")) Add(LineKind.Tool, m.Trim());
                else Add(LineKind.System, m);
            };
            await _client.ConnectAsync(Settings.Server, Settings.Port);
            _brain.Start();
            SetStatus(HostState.Playing, $"Playing as {character.Name} ({profile.Name}) on {Settings.Server}");
            CheckModdedBlocks();
        }
        catch (Exception e)
        {
            SignInPrompt = null;
            Add(LineKind.Error, e.Message);
            await TearDownAsync();
            SetStatus(HostState.Idle, "Not playing (" + e.Message + ")");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// After joining: blocks from mods Vertex has no data for are solid unknown cubes. Says which, and (once per
    /// session) learns them from the server's modpack, saves that for this world, and rejoins to use it.
    /// </summary>
    private void CheckModdedBlocks()
    {
        var unknown = _client?.UnknownModdedBlocks ?? [];
        if (unknown.Count == 0) return;
        var mods = string.Join(", ", unknown.Select(n => n.Split(':')[0]).Distinct());
        Add(LineKind.System, $"{unknown.Count} modded blocks I have no data for (mods: {mods}): treating them as solid blocks");
        var pack = Settings.ModpackForServer();
        if (pack == null || _learnedThisSession)
        {
            if (pack == null) Add(LineKind.System, "set this server's modpack (a Modrinth .mrpack link) in settings and I can learn them");
            return;
        }
        _learnedThisSession = true;
        _brain?.Mouth.Say("Give me a minute, I'm learning this server's modded blocks.");
        _ = Task.Run(async () =>
        {
            try
            {
                var learner = new Mods.ModpackBlocks(ModsCache, m => Add(LineKind.System, m));
                var learned = await learner.DumpAsync(pack);
                var known = Mods.ModpackBlocks.LoadWorld(WorldBlocksFile)?.Values ?? [];
                Mods.ModpackBlocks.SaveWorld(WorldBlocksFile, known.Where(k => !learned.ContainsKey(k.Name)).Concat(learned.Values), pack);
                var missing = unknown.Where(n => !learned.ContainsKey(n)).ToList();
                if (missing.Count > 0) Add(LineKind.System, $"the modpack does not have {missing.Count} of them: {string.Join(", ", missing.Take(10))}");
                if (State != HostState.Playing) return;
                _brain?.Mouth.Say("I learned this server's blocks! Be right back.");
                await Task.Delay(TimeSpan.FromSeconds(3));
                await StopAsync();
                await StartAsync();
            }
            catch (Exception e)
            {
                Add(LineKind.Error, "could not learn the modded blocks: " + e.Message);
            }
        });
    }

    /// <summary>The fast command matcher. Optional: without it every request goes through the language model.</summary>
    private async Task LoadIntentsAsync()
    {
        if (_ai!.Intents != null) return;
        SetStatus(HostState.Starting, "Loading fast commands...");
        try { await _ai.LoadIntentsAsync(); }
        catch (Exception e) { Add(LineKind.Error, "fast commands unavailable (the language model handles everything): " + e.Message); }
    }

    public async Task StopAsync()
    {
        if (State is HostState.Idle or HostState.Stopping) return;
        SetStatus(HostState.Stopping, "Leaving...");
        await TearDownAsync();
        SetStatus(HostState.Idle, "Not playing");
    }

    private async Task TearDownAsync()
    {
        if (_brain != null) { await _brain.DisposeAsync(); _brain = null; }
        if (_voice != null) { await _voice.DisposeAsync(); _voice = null; }
        if (_client != null) { await _client.DisposeAsync(); _client = null; }
    }

    /// <summary>The operator talks to Vertex (as if in chat, always addressed).</summary>
    public void Tell(string text)
    {
        if (_brain == null || string.IsNullOrWhiteSpace(text)) return;
        Add(LineKind.Chat, "You: " + text);
        _brain.Post(new SaidEvent(Guid.Empty, "TJ", text, false));
    }

    public void StopTask()
    {
        _brain?.Tasks.Stop();
        _client?.Controls.Clear();
        Add(LineKind.System, "stopped the current task");
    }

    public string SwitchCharacter(string name)
    {
        Settings.Character = name;
        Settings.Save();
        var result = _brain?.SwitchCharacter(name) ?? $"will play as {name}";
        Add(LineKind.System, result);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync();
        if (_ai != null) await _ai.DisposeAsync();
    }
}
