using System.Text.Json;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Reachy;

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
    /// <summary>Join the Minecraft server on start (Vertex runs without a game too: through the robot and this panel).</summary>
    public bool Minecraft { get; set; }
    /// <summary>Use the Reachy Mini as Vertex's body at home: its microphone, speaker and body language.</summary>
    public bool Reachy { get; set; } = true;
    /// <summary>The robot's address on the LAN.</summary>
    public string ReachyHost { get; set; } = "reachy-mini.local";
    /// <summary>Who talks to Vertex through the robot, until voices can be told apart ("someone in the room").</summary>
    public string ReachySpeaker { get; set; } = "";
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

/// <summary>Idle: Vertex is off. Playing: Vertex is on (with the robot, the game, both, or only this panel).</summary>
public enum HostState { Idle, Starting, Playing, Stopping }

/// <summary>
/// Runs Vertex for the control panel and the tray: the AI (loaded once, kept), the companion (Vertex's mind, while
/// on), and the places it is: the Reachy Mini at home and a Minecraft world, each joined and left on its own while
/// the conversation goes on. Everything the panel shows comes from here; Changed tells it to redraw.
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

    /// <summary>What Vertex learned by experience about this server's blocks (shapes, doors, names, dig times).</summary>
    private string WorldTouchedFile => WorldFileBase + ".touched.json";

    private void SaveTouched()
    {
        if (_client is { LearnedStates.Count: > 0, BlockRegistryFingerprint: { } fp } c)
            Mods.TouchedBlocks.Save(WorldTouchedFile, new Mods.TouchedBlocks(fp, c.LearnedStates.ToDictionary()));
    }

    /// <summary>What Vertex has learned about this server's modded blocks (next to its world memory).</summary>
    private string WorldBlocksFile => WorldFileBase + ".blocks.json";

    /// <summary>worlds/&lt;server_port&gt; under the data folder (the world memory is worlds/&lt;server_port&gt;.json).</summary>
    private string WorldFileBase => System.IO.Path.Combine(_dataFolder, "worlds",
        string.Concat($"{Settings.Server}_{Settings.Port}".ToLowerInvariant().Split(System.IO.Path.GetInvalidFileNameChars())));
    private VertexAi? _ai;
    private Companion? _companion;
    private MinecraftClient? _client;
    private SimpleVoiceChat? _voice;
    private VertexBrain? _brain;
    private ReachyChannel? _reachy;
    private RobotBody? _robot;

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

    /// <summary>What Vertex is doing right now (the tray dot): off, starting, listening, thinking or speaking.</summary>
    public VertexActivity Activity => State switch
    {
        HostState.Starting => VertexActivity.Starting,
        HostState.Playing when _companion?.Mouth.IsSpeaking == true => VertexActivity.Speaking,
        HostState.Playing when _companion?.IsThinking == true => VertexActivity.Thinking,
        HostState.Playing => VertexActivity.Listening,
        _ => VertexActivity.Off,
    };
    public string Status { get; private set; } = "Off";
    /// <summary>Shown while a Microsoft sign-in waits for the device code to be approved.</summary>
    public DeviceCodePrompt? SignInPrompt { get; private set; }
    public IReadOnlyList<string> Models { get; private set; } = [];
    public Companion? Companion => _companion;
    public MinecraftClient? Client => _client;
    public SimpleVoiceChat? Voice => _voice;
    public VertexBrain? Brain => _brain;
    /// <summary>In a Minecraft world right now.</summary>
    public bool InGame => _brain != null;
    /// <summary>The Reachy Mini is connected and awake.</summary>
    public bool RobotConnected => _robot != null;

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

    /// <summary>The companion's and the bodies' log lines, sorted for the panel.</summary>
    private void AddLog(string m)
    {
        if (m.StartsWith("> ")) Add(LineKind.Heard, m[2..]);
        else if (m.StartsWith("< ")) Add(LineKind.Said, m[2..]);
        else if (m.TrimStart().StartsWith("tool ")) Add(LineKind.Tool, m.Trim());
        else Add(LineKind.System, m);
    }

    private void SetStatus(HostState state, string status)
    {
        State = state;
        Status = status;
        Changed?.Invoke();
    }

    /// <summary>"On: Reachy Mini + Minecraft (mc.spawndev.com) as Vertex".</summary>
    private string OnStatus()
    {
        var where = new List<string>();
        if (_robot != null) where.Add("Reachy Mini");
        if (_brain != null && _client != null) where.Add($"Minecraft ({_client.Profile.Name} on {Settings.Server})");
        var name = _companion?.Character.Name ?? Settings.Character;
        return where.Count == 0 ? $"On as {name} (talk to it here)" : $"On as {name}: {string.Join(" + ", where)}";
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

    /// <summary>Turns Vertex on: the AI, then the robot and the game as the settings say.</summary>
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
            await _ai!.EnsureChatReadyAsync(Settings.Model);
            await LoadIntentsAsync();
            var character = Characters.Find(Settings.Character) ?? Character.BuiltIn[0];
            SetStatus(HostState.Starting, "Warming up ears and voice...");
            await _ai.WarmUpAsync(character.Voice);
            _companion = new Companion(_ai, Characters, character, Settings.Model);
            _companion.Log += AddLog;
            _companion.Start();
            if (Settings.Reachy) await ConnectRobotCoreAsync();
            if (Settings.Minecraft)
            {
                // A game that cannot be joined leaves Vertex on without it (the robot, this panel): say why.
                try { await JoinGameCoreAsync(); }
                catch (Exception e) { SignInPrompt = null; Add(LineKind.Error, $"could not join {Settings.Server}: {e.Message}"); }
            }
            SetStatus(HostState.Playing, OnStatus());
            if (_robot != null && _brain == null) _companion.Say($"Hi! It's {character.Name}. I'm here.");
        }
        catch (Exception e)
        {
            SignInPrompt = null;
            Add(LineKind.Error, e.Message);
            await TearDownAsync();
            SetStatus(HostState.Idle, "Off (" + e.Message + ")");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Joins the Minecraft server while Vertex is on.</summary>
    public Task JoinGameAsync() => WhileOnAsync("Joining the game...", async () =>
    {
        Settings.Minecraft = true;
        Settings.Save();
        await JoinGameCoreAsync();
    });

    /// <summary>Leaves the Minecraft server; Vertex stays on (robot, panel).</summary>
    public Task LeaveGameAsync() => WhileOnAsync("Leaving the game...", async () =>
    {
        Settings.Minecraft = false;
        Settings.Save();
        await LeaveGameCoreAsync();
    });

    /// <summary>Connects and wakes the Reachy Mini while Vertex is on.</summary>
    public Task ConnectRobotAsync() => WhileOnAsync("Waking the robot...", async () =>
    {
        Settings.Reachy = true;
        Settings.Save();
        await ConnectRobotCoreAsync();
    });

    /// <summary>Parks and disconnects the Reachy Mini; Vertex stays on.</summary>
    public Task DisconnectRobotAsync() => WhileOnAsync("Parking the robot...", async () =>
    {
        Settings.Reachy = false;
        Settings.Save();
        await DisconnectRobotCoreAsync();
    });

    private async Task WhileOnAsync(string doing, Func<Task> work)
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (State != HostState.Playing) return;
            SetStatus(HostState.Playing, doing);
            try { await work(); }
            catch (Exception e) { SignInPrompt = null; Add(LineKind.Error, e.Message); }
            SetStatus(HostState.Playing, OnStatus());
        }
        finally { _gate.Release(); }
    }

    private async Task ConnectRobotCoreAsync()
    {
        if (_robot != null || _companion == null) return;
        SetStatus(HostState.Starting, $"Connecting to the robot ({Settings.ReachyHost})...");
        try
        {
            _reachy = await ReachyChannel.ConnectAsync(Settings.ReachyHost, _ai!);
            if (!string.IsNullOrWhiteSpace(Settings.ReachySpeaker)) _reachy.RoomSpeakerName = Settings.ReachySpeaker.Trim();
            _reachy.Log += m => Add(LineKind.System, "[reachy] " + m);
            _robot = new RobotBody(_reachy, _companion);
            _robot.Log += m => Add(LineKind.System, "[reachy] " + m);
            await _robot.WakeAsync();
            _companion.Attach(_robot);
            _companion.AddChannel(_reachy);
            Add(LineKind.System, "the robot is awake and listening");
        }
        catch (Exception e)
        {
            // Vertex works without its robot: say why, and go on.
            Add(LineKind.Error, $"the robot is not available ({Settings.ReachyHost}): {e.Message}");
            await DisconnectRobotCoreAsync();
        }
    }

    private async Task DisconnectRobotCoreAsync()
    {
        if (_reachy != null) _companion?.RemoveChannel(_reachy);
        if (_robot != null)
        {
            _companion?.Detach(_robot);
            await _robot.DisposeAsync(); // parks it: home, sleep, motors off
            _robot = null;
        }
        if (_reachy != null) { await _reachy.DisposeAsync(); _reachy = null; }
    }

    private async Task JoinGameCoreAsync()
    {
        if (_brain != null || _companion == null) return;
        GameProfile profile;
        if (string.IsNullOrWhiteSpace(Settings.Account)) profile = GameProfile.Offline(Settings.OfflineName);
        else
        {
            SetStatus(State, "Signing in...");
            var auth = new MicrosoftAuth(DpapiAccountStore.ForAccount("Vertex", Settings.Account), p =>
            {
                SignInPrompt = p;
                Changed?.Invoke();
                return Task.CompletedTask;
            });
            profile = await auth.SignInAsync();
            SignInPrompt = null;
        }

        SetStatus(State, $"Joining {Settings.Server}...");
        try
        {
            // Brand "vertex": honest, and not "vanilla" (AubsCraft's proxy keeps vanilla clients off modded servers
            // because they cannot answer Fabric's registry sync; Vertex can).
            var touched = Mods.TouchedBlocks.Load(WorldTouchedFile);
            _client = new MinecraftClient(profile, new ClientOptions
            {
                Brand = "vertex",
                Log = m => { if (m.StartsWith("disconnected") || m.StartsWith("joined") || m.StartsWith("spawned") || m.StartsWith("modded blocks")) Add(LineKind.System, m); },
                Modded = Mods.ModpackBlocks.LoadWorld(WorldBlocksFile),
                Learned = touched?.States,
                LearnedFingerprint = touched?.Fingerprint,
                UseEmbeddedModdedBlocks = UseEmbeddedModdedBlocks,
            });
            _client.ChatReceived += m => Add(LineKind.Chat, m.Text);
            var client = _client;
            // Kicked or the server went away: leave the game, Vertex stays on.
            _client.Disconnected += r => { Add(LineKind.System, "disconnected: " + r); _ = Task.Run(() => LeftGameAsync(client)); };
            if (Settings.Voice)
            {
                _voice = new SimpleVoiceChat(_client, Settings.Server);
                _voice.Log += m => Add(LineKind.System, "voice: " + m);
            }
            _brain = new VertexBrain(_client, _voice, _companion, $"{Settings.Server}_{Settings.Port}");
            _brain.Log += AddLog;
            await _client.ConnectAsync(Settings.Server, Settings.Port);
            _brain.Start();
            Add(LineKind.System, $"Playing as {_companion.Character.Name} ({profile.Name}) on {Settings.Server}");
            CheckModdedBlocks();
        }
        catch
        {
            await LeaveGameCoreAsync();
            throw;
        }
    }

    /// <summary>The server dropped Vertex: tidy up the game, if it is still the same one.</summary>
    private async Task LeftGameAsync(MinecraftClient client)
    {
        await _gate.WaitAsync();
        try
        {
            if (_client != client) return;
            await LeaveGameCoreAsync();
            if (State == HostState.Playing) SetStatus(HostState.Playing, OnStatus());
        }
        finally { _gate.Release(); }
    }

    private async Task LeaveGameCoreAsync()
    {
        SaveTouched();
        if (_brain != null) { await _brain.DisposeAsync(); _brain = null; }
        if (_voice != null) { await _voice.DisposeAsync(); _voice = null; }
        if (_client != null) { await _client.DisposeAsync(); _client = null; }
    }

    /// <summary>
    /// After joining: blocks from mods Vertex has no data for are solid unknown cubes. Says which, and (once per
    /// session) learns them from the server's modpack, saves that for this world, and uses it at once (no rejoin: chunks keep raw state ids).
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
        SayInGame("Give me a minute, I'm learning this server's modded blocks.");
        _ = Task.Run(async () =>
        {
            try
            {
                var learner = new Mods.ModpackBlocks(ModsCache, m => Add(LineKind.System, m));
                var learned = await learner.DumpAsync(pack);
                var known = Mods.ModpackBlocks.LoadWorld(WorldBlocksFile) ?? SpawnDev.Minecraft.Data.ModdedContent.Empty;
                Mods.ModpackBlocks.SaveWorld(WorldBlocksFile, known.With(learned), pack);
                var missing = unknown.Where(n => !learned.Blocks.ContainsKey(n)).ToList();
                if (missing.Count > 0) Add(LineKind.System, $"the modpack does not have {missing.Count} of them: {string.Join(", ", missing.Take(10))}");
                if (State != HostState.Playing || _client == null) return;
                _client.AddModded(learned); // applies at once: chunks keep raw state ids, no rejoin needed
                SayInGame("Got it, I know this server's blocks now!");
            }
            catch (Exception e)
            {
                Add(LineKind.Error, "could not learn the modded blocks: " + e.Message);
            }
        });
    }

    private void SayInGame(string text)
    {
        if (_brain is { } brain) brain.Companion.Say(text, [brain.Channel]);
    }

    /// <summary>The fast command matcher. Optional: without it every request goes through the language model.</summary>
    private async Task LoadIntentsAsync()
    {
        if (_ai!.Intents != null) return;
        SetStatus(HostState.Starting, "Loading fast commands...");
        try { await _ai.LoadIntentsAsync(); }
        catch (Exception e) { Add(LineKind.Error, "fast commands unavailable (the language model handles everything): " + e.Message); }
    }

    /// <summary>Turns Vertex off: leaves the game, parks the robot.</summary>
    public async Task StopAsync()
    {
        if (State is HostState.Idle or HostState.Stopping) return;
        await _gate.WaitAsync();
        try
        {
            if (State is HostState.Idle or HostState.Stopping) return;
            SetStatus(HostState.Stopping, "Stopping...");
            await TearDownAsync();
            SetStatus(HostState.Idle, "Off");
        }
        finally { _gate.Release(); }
    }

    private async Task TearDownAsync()
    {
        await LeaveGameCoreAsync();
        await DisconnectRobotCoreAsync();
        if (_companion != null) { await _companion.DisposeAsync(); _companion = null; }
    }

    /// <summary>The operator talks to Vertex (as if in chat, always addressed).</summary>
    public void Tell(string text)
    {
        if (_companion == null || string.IsNullOrWhiteSpace(text)) return;
        Add(LineKind.Chat, "You: " + text);
        _companion.Post(new SaidEvent(Guid.Empty, "TJ", text, false));
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
        var result = _companion?.SwitchCharacter(name) ?? $"will play as {name}";
        Add(LineKind.System, result);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync();
        if (_ai != null) await _ai.DisposeAsync();
    }
}
