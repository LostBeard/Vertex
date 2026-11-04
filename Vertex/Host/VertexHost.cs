using System.Text.Json;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Reachy;

namespace Vertex.Host;

/// <summary>Where a language-model install is, for the dashboard: what to say, how far along (0 to 1), whether it is still running, and why it failed (null if it did not).</summary>
public sealed record ModelInstallStatus(string Model, string Text, double Fraction, bool Running, string? Error);

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
    /// <summary>
    /// Who is typing on the dashboard (its chat box, from a PC or a tablet), by name: what is typed there is said to Vertex as this person. Blank: the robot's "Who talks to it"
    /// name if one is set, otherwise "Someone".
    /// </summary>
    public string OperatorName { get; set; } = "";
    /// <summary>
    /// Start Vertex (load the AI, connect the robot, join the game if that is on) as soon as the app starts, without anyone pressing Start: for a PC that is Vertex's own, started by
    /// Windows at login. Off by default, because a started Vertex holds most of the GPU.
    /// </summary>
    public bool StartOnLaunch { get; set; }
    public bool Voice { get; set; } = true;
    /// <summary>Join the Minecraft server on start (Vertex runs without a game too: through the robot and this panel).</summary>
    public bool Minecraft { get; set; }
    /// <summary>
    /// Let Vertex play on its own in the game: when nobody has needed it for a while and it has no goal, it chooses one small project (build something little, explore, gather, fish, farm)
    /// and does it. Only in survival, by day, healthy and fed; a person talking to it, "stay" or a goal always outrank it. Off by default.
    /// </summary>
    public bool SelfDirectedPlay { get; set; }
    /// <summary>
    /// When the server (EssentialsX) says god mode is on for his account, he turns it off himself with /god and asks again to confirm: with it on nothing can hurt him, so nothing he does is
    /// ever tested and the household cannot see how he copes. The owner's request for HIS account (TJ, 2026-11-03: "handing it to me is you refusing to do what I asked"). Off by default.
    /// </summary>
    public bool TurnOffGodMode { get; set; }
    /// <summary>
    /// Long-term memory: jobs Vertex finishes are written to a log on this PC (the memory folder in the data folder), and what a friend says is looked up in it before he answers, so he can say
    /// what he built last week. Off: nothing is written and nothing is looked up (what is already stored stays until deleted).
    /// </summary>
    public bool LongTermMemory { get; set; } = true;
    /// <summary>Use the Reachy Mini as Vertex's body at home: its microphone, speaker and body language.</summary>
    public bool Reachy { get; set; } = true;
    /// <summary>The robot's address on the LAN.</summary>
    public string ReachyHost { get; set; } = "reachy-mini.local";
    /// <summary>Who talks to Vertex through the robot, until voices can be told apart ("someone in the room").</summary>
    public string ReachySpeaker { get; set; } = "";
    /// <summary>Answer everything said near the robot, not only when Vertex is called by name.</summary>
    public bool ReachyAnswersEverything { get; set; }
    /// <summary>
    /// Tell the people who talk to the robot apart by their voice: the people Vertex has been introduced to are called by name,
    /// a stranger is "Guest 1". Voices stay on this PC (voices.json in the data folder).
    /// </summary>
    public bool ReachyRecognizeVoices { get; set; } = true;
    /// <summary>
    /// Decide whether a pause is the end of someone's turn by listening to HOW they said it (Smart Turn), instead of always waiting half a second:
    /// a finished sentence is answered sooner, "I would like... um" is waited on. Off: the plain half-second silence rule.
    /// </summary>
    public bool ReachySmartTurn { get; set; } = true;
    /// <summary>
    /// Show (and let Vertex react to) the words WHILE they are being said, like voice typing on a phone: a streaming recogniser runs over the sentence in progress,
    /// and the finished sentence is still transcribed by the more accurate one. Costs about 1.2 GB of GPU memory.
    /// </summary>
    public bool ReachyLiveWords { get; set; } = true;
    /// <summary>
    /// Recognise faces through the robot's camera: it learns the face of anyone who is introduced ("this is Nikki"), knows who is in the room, can look at who is talking, and says hi to someone it knows
    /// who walks in after a long absence. Faces and voices are kept only on this PC, for people who were introduced until they are removed on the People page; a stranger nobody introduces is
    /// forgotten after 24 hours. This is the point of a companion that knows its people; turn it off and Vertex treats everyone as a stranger.
    /// </summary>
    public bool ReachyFaces { get; set; } = true;
    /// <summary>Turn the robot to face whoever is talking to it: the way the voice came from, and the face that matches it. It turns back to the front after a minute and a half of quiet.</summary>
    public bool ReachyFaceSpeaker { get; set; } = true;
    /// <summary>
    /// Send the robot's speech as a live stream over the WebRTC connection instead of uploading each sentence to it first. No wait for an upload (3 to 4 s per sentence over Wi-Fi, measured), the
    /// speech starts at once; the sound is Opus (the WebRTC speech codec) rather than the uploaded WAV. Off: upload and play, the way it worked before.
    /// </summary>
    public bool ReachyStreamSpeech { get; set; }
    /// <summary>Keep looking at the person who is talking to the robot as they move around (a small correction after each look at the camera). Needs faces on.</summary>
    public bool ReachyFollowFaces { get; set; } = true;
    /// <summary>
    /// Keep every utterance the robot hears (the audio, the words Vertex heard, who it sounded like) in the data folder's "heard"
    /// folder, to measure and improve listening on this household's own voices. Off by default; never uploaded anywhere.
    /// </summary>
    public bool ReachySaveHeard { get; set; }
    /// <summary>
    /// Write the words of everything the robot hears into the log (and so onto the dashboard, which can be opened from any device on the network). Off by default: the robot hears the
    /// whole room, including conversations nobody meant for it. What is said to Vertex is always logged.
    /// </summary>
    public bool ReachyLogWords { get; set; }
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

    /// <summary>
    /// Version of the settings file. Vertex SAVES the settings every time it starts, so a default that was current when someone ran it is written into their file as if they had chosen it - and
    /// changing the default later does nothing for them (live 2026-10-15: speech streaming was on by default for one build, got saved, and stayed on after the default went off, so the robot
    /// was silent). A change of default that must reach people who never touched the setting is a MIGRATION here: bump <see cref="CurrentVersion"/> and reset it in <see cref="Migrate"/>.
    /// </summary>
    public int SettingsVersion { get; set; }

    /// <summary>The settings version this build writes.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Brings settings saved by an older build up to date. Only things that were DEFAULTS are reset; a choice made after the migration is kept.</summary>
    internal void Migrate()
    {
        if (SettingsVersion < 2)
        {
            // 2: live Opus speech streaming became opt-in (experimental). It had been the default for one build, which Vertex then saved for everyone who ran it.
            ReachyStreamSpeech = false;
        }
        SettingsVersion = CurrentVersion;
    }

    /// <summary>The settings in a data folder (default %APPDATA%\Vertex).</summary>
    public static VertexSettings Load(string? folder = null)
    {
        var path = System.IO.Path.Combine(folder ?? DefaultFolder, "settings.json");
        VertexSettings settings;
        try { settings = JsonSerializer.Deserialize<VertexSettings>(File.ReadAllText(path)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { settings = new(); }
        settings._path = path;
        settings.Migrate();
        return settings;
    }

    public static string DefaultFolder => Environment.GetEnvironmentVariable("VERTEX_DATA_FOLDER") is { Length: > 0 } own
        ? own // tests (and a second copy on one PC) keep their settings, people and voices apart from the user's
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vertex");

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
        // The models are known before the AI (and the GPU) is loaded, so the dashboard can say at once that one has to be installed.
        RefreshModels();
    }

    /// <summary>Tests: the Ollama-layout model cache to read and install into (default: the user's, the one VertexAi reads).</summary>
    internal string? ModelsRoot { get; set; }

    /// <summary>Tests: where the language model may be fetched from (default: models.spawndev.com, then registry.ollama.ai).</summary>
    internal IReadOnlyList<SpawnDev.AI.Server.OllamaModelSource>? ModelSources { get; set; }

    /// <summary>Reads the chat models in the cache again; picks the default model when none is chosen (or the chosen one is gone).</summary>
    internal void RefreshModels()
    {
        Models = ModelSetup.Installed(ModelsRoot);
        if (Models.Count > 0 && (string.IsNullOrEmpty(Settings.Model) || !Models.Contains(Settings.Model)))
            Settings.Model = Models.FirstOrDefault(m => m == ModelSetup.DefaultModel) ?? Models.FirstOrDefault(m => m.StartsWith("llama3.1")) ?? Models[0];
    }

    /// <summary>Where the language-model install is (null: none started in this run).</summary>
    public ModelInstallStatus? ModelInstall { get; private set; }
    private CancellationTokenSource? _installCts;
    private IReadOnlyList<SpawnDev.AI.Server.OllamaInstallableModel> _installable = [];

    /// <summary>The models that can be installed (what models.spawndev.com lists), the default first. Falls back to just the default when the list cannot be reached.</summary>
    public IReadOnlyList<string> Installable => _installable.Count == 0
        ? [ModelSetup.DefaultModel]
        : _installable.Select(m => m.Name).OrderBy(n => n == ModelSetup.DefaultModel ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The size of an installable model, or 0 when the list does not give one.</summary>
    public long InstallableSize(string name) => _installable.FirstOrDefault(m => m.Name == name)?.SizeBytes ?? 0;

    /// <summary>No chat model is installed at all: the first thing to do is install one.</summary>
    public bool NeedsModel => Models.Count == 0;

    /// <summary>Reads the list of installable models (a few KB). A failure leaves the default as the only choice.</summary>
    public async Task LoadInstallableAsync()
    {
        try { _installable = await ModelSetup.ListInstallableAsync(modelsRoot: ModelsRoot, sources: ModelSources); }
        catch (Exception e) { Add(LineKind.System, $"could not read the list of installable models: {e.Message}"); }
        Changed?.Invoke();
    }

    /// <summary>
    /// Installs a language model into the Ollama-layout cache (default llama3.1:8b) from models.spawndev.com, then registry.ollama.ai, without Ollama. Progress is on
    /// <see cref="ModelInstall"/>; <see cref="CancelModelInstall"/> stops it and keeps the partial file, so the next call resumes. Runs one install at a time.
    /// </summary>
    public async Task InstallModelAsync(string? name = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? ModelSetup.DefaultModel : name.Trim();
        if (ModelInstall is { Running: true }) return;
        using var cts = _installCts = new CancellationTokenSource();
        ModelInstall = new ModelInstallStatus(name, $"Looking up {name}...", 0, true, null);
        Changed?.Invoke();
        try
        {
            var model = await Task.Run(() => ModelSetup.InstallAsync(name,
                p => { ModelInstall = new ModelInstallStatus(name, ModelSetup.Describe(p), ModelSetup.Fraction(p), true, null); Changed?.Invoke(); },
                cts.Token, ModelsRoot, ModelSources));
            RefreshModels();
            if (_ai != null) Models = await _ai.ModelNamesAsync();
            if (!Models.Contains(Settings.Model)) Settings.Model = model.Name;
            Settings.Save();
            ModelInstall = new ModelInstallStatus(name, $"{model.Name} is installed.", 1, false, null);
            Add(LineKind.System, $"installed {model.Name}");
        }
        catch (OperationCanceledException)
        {
            ModelInstall = new ModelInstallStatus(name, "Stopped. What was downloaded is kept; Install carries on from there.", ModelInstall?.Fraction ?? 0, false, null);
        }
        catch (Exception e)
        {
            ModelInstall = new ModelInstallStatus(name, "", ModelInstall?.Fraction ?? 0, false, e.Message);
            Add(LineKind.Error, $"could not install {name}: {e.Message}");
        }
        finally { _installCts = null; Changed?.Invoke(); }
    }

    /// <summary>Turns self-directed play on or off, now (the brain in the game follows at once) and for next time.</summary>
    public void SetSelfDirectedPlay(bool on)
    {
        Settings.SelfDirectedPlay = on;
        Settings.Save();
        if (_brain != null) _brain.SelfDirectedPlay = on;
        Add(LineKind.System, on ? "playing on my own when nobody needs me" : "not playing on my own any more");
    }

    /// <summary>
    /// Turns the long-term memory on or off, now (the brain in the game follows at once) and for next time. Off writes and looks up nothing; what is already stored stays on disk.
    /// </summary>
    public async Task SetLongTermMemoryAsync(bool on)
    {
        Settings.LongTermMemory = on;
        Settings.Save();
        if (on) await OpenMemoryAsync(); // only when the AI is loaded; otherwise the next Start opens it
        if (_brain != null) _brain.LongTerm = on ? _memory : null;
        Add(LineKind.System, on ? "remembering what I do" : "not writing to my long-term memory any more");
        Changed?.Invoke();
    }

    /// <summary>Whether the long-term memory is open (it is while Vertex is on and the Memory switch is on): the list below and the two deletes work only then.</summary>
    public bool MemoryOpen => _memory != null;

    /// <summary>What Vertex remembers, newest first (empty when the memory is not open).</summary>
    public IReadOnlyList<Vertex.Memory.MemoryRow> MemoryRows => _memory?.Rows() ?? [];

    /// <summary>Forgets one memory for good (and whatever was digested from it): gone from the list, the searches and the disk.</summary>
    public async Task ForgetMemoryAsync(string id)
    {
        if (_memory is not { } memory) return;
        var n = await memory.ForgetAsync([id]);
        Add(LineKind.System, n == 1 ? "forgot one memory" : $"forgot {n} memories");
        Changed?.Invoke();
    }

    /// <summary>Forgets everything Vertex remembers.</summary>
    public async Task ForgetAllMemoriesAsync()
    {
        if (_memory is not { } memory) return;
        var n = await memory.ForgetAllAsync();
        Add(LineKind.System, $"forgot everything I remembered ({n} memories)");
        Changed?.Invoke();
    }

    /// <summary>Stops a running model install; the downloaded part is kept.</summary>
    public void CancelModelInstall() => _installCts?.Cancel();
    /// <summary>Where the robot's heard utterances are kept when <see cref="VertexSettings.ReachySaveHeard"/> is on.</summary>
    public string HeardFolder => System.IO.Path.Combine(_dataFolder, "heard");

    /// <summary>The voices Vertex knows (people it was introduced to, and provisional guests). Empty until the AI is up.</summary>
    public IReadOnlyList<SpawnDev.AI.SpeakerProfile> Voices => _ai?.Speakers.Registry.Profiles ?? [];

    /// <summary>"Add a person": the next new voice the robot hears, over their next few sentences, is learned as this name.</summary>
    public string LearnVoice(string name)
    {
        if (_reachy == null) return "Wake the robot first: voices are learned through its microphone.";
        var told = _reachy.IntroduceNextVoice(name);
        Changed?.Invoke();
        return told;
    }

    /// <summary>Names a provisional guest (or merges it into someone who has that name).</summary>
    public void RenameVoice(string from, string to)
    {
        if (_ai == null || string.IsNullOrWhiteSpace(to)) return;
        if (_ai.Speakers.Registry.Rename(from, to)) _ai.SaveVoices();
        Changed?.Invoke();
    }

    /// <summary>Forgets a voice.</summary>
    public void ForgetVoice(string name)
    {
        if (_ai == null) return;
        if (_ai.Speakers.Registry.Forget(name)) _ai.SaveVoices();
        Changed?.Invoke();
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

    /// <summary>The server's clock for a time of day in ticks: 24000 ticks make a day, 1000 an hour, and tick 0 is 6:00 in the morning ("5700" is 11:42 AM).</summary>
    public static string ClockText(long dayTimeTicks)
    {
        var tod = ((dayTimeTicks % 24000) + 24000) % 24000;
        var minutes = (int)((tod * 3L / 50 + 6 * 60) % (24 * 60));
        var hour = minutes / 60;
        return $"{(hour % 12 == 0 ? 12 : hour % 12)}:{minutes % 60:00} {(hour < 12 ? "AM" : "PM")}";
    }

    /// <summary>What the Minecraft tab shows about the game right now, as the server last said it.</summary>
    public sealed record InGameState(string Mode, bool Invulnerable, bool MayFly, string Health, string Food, string Time, string Phase, long Day, string Weather, string Where, bool? GodMode = null);

    /// <summary>
    /// The state of the game he is in: game mode, whether the server made him invulnerable, health and food, the server's time of day (tick 0 is 6:00 in the morning) and weather, and where he is.
    /// Null when he is not in a world. (TJ, 2026-11-03: "the mode he is in should be on his Minecraft status page, along with server time of day and weather".)
    /// </summary>
    public InGameState? InGameNow()
    {
        if (_client is not { IsSpawned: true } c) return null;
        var ticks = c.World.DayTime;
        var tod = ((ticks % 24000) + 24000) % 24000;
        var time = ClockText(ticks);
        var phase = tod < 12000 ? "day" : tod < 13000 ? "sunset" : tod < 23000 ? "night" : "sunrise";
        var (x, y, z) = c.Position;
        return new InGameState(c.GameMode.ToString().ToLowerInvariant(), c.Invulnerable, c.MayFly, $"{c.Health / 2:0.#} of 10 hearts", $"{c.Food / 2:0.#} of 10", time, phase, ticks / 24000 + 1, c.Weather,
            $"{x:0} {y:0} {z:0} in the {c.World.Dimension.Bare().Replace("the_", "")}", _brain?.GodMode);
    }
    public SimpleVoiceChat? Voice => _voice;
    public VertexBrain? Brain => _brain;
    /// <summary>In a Minecraft world right now.</summary>
    public bool InGame => _brain != null;
    /// <summary>The Reachy Mini is connected and awake.</summary>
    public bool RobotConnected => _robot != null;

    /// <summary>The people Vertex knows (voice and face by name) and the place they are removed from. Empty until the AI is up.</summary>
    public IReadOnlyList<Vertex.People.PersonRecord> KnownPeople => _ai?.People.List() ?? [];

    /// <summary>Names a stranger Vertex noticed (a voice "Guest N" or a face "Visitor N"): from then on they are introduced and kept until removed.</summary>
    public bool NamePerson(string from, string to)
    {
        var r = !string.IsNullOrWhiteSpace(to) && (_ai?.People.Rename(from, to.Trim()) ?? false);
        Changed?.Invoke();
        return r;
    }

    /// <summary>Removes one person (their voiceprint and their face).</summary>
    public bool ForgetPerson(string name) { var r = _ai?.People.Forget(name) ?? false; Changed?.Invoke(); return r; }

    /// <summary>Removes every stranger nobody introduced.</summary>
    public int ForgetGuests() { var n = _ai?.People.ForgetGuests() ?? 0; Changed?.Invoke(); return n; }

    /// <summary>Removes everyone: every voiceprint and every face.</summary>
    public int ForgetEveryone() { var n = _ai?.People.ForgetAll() ?? 0; Changed?.Invoke(); return n; }

    /// <summary>Who the robot's camera sees right now ("Nikki, a visitor"), or empty.</summary>
    public string InView => _reachy == null ? "" : string.Join(", ", _reachy.InView.Select(v => v.Name == null ? "someone too far to recognise" : v.IsGuest ? $"{v.Name} (not introduced)" : v.Name));

    /// <summary>What is being said right now, as the live recogniser hears it (empty when nobody is talking or live words are off).</summary>
    public string LiveCaption { get; private set; } = "";

    /// <summary>Where the voice the robot just heard came from ("left, about 10:30 o'clock"), or empty when it has been quiet for a few seconds or the robot is not connected.</summary>
    public string VoiceFrom => _reachy?.Voice.Current(Environment.TickCount64)?.ToString() ?? "";

    /// <summary>Whether someone is talking right now (the microphone array hears speech).</summary>
    public bool VoiceSpeakingNow => _reachy?.Voice.Speaking(Environment.TickCount64) ?? false;

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

    /// <summary>A Microsoft sign-in is saved for this Windows user and can be opened (tokens are encrypted per user, so another PC's or user's file does not count). False for offline play.</summary>
    public bool HasSavedSignIn => !string.IsNullOrWhiteSpace(Settings.Account) && DpapiAccountStore.ForAccount("Vertex", Settings.Account).CanRead();

    /// <summary>"On: Reachy Mini + Minecraft (mc.spawndev.com) as Vertex".</summary>
    private string OnStatus()
    {
        var where = new List<string>();
        if (_robot != null) where.Add("Reachy Mini");
        if (_brain != null && _client != null) where.Add($"Minecraft ({_client.Profile.Name} on {Settings.Server})");
        var name = _companion?.Character.Name ?? Settings.Character;
        var status = where.Count == 0 ? $"On as {name} (talk to it here)" : $"On as {name}: {string.Join(" + ", where)}";
        return _rejoinNote == null ? status : $"{status} - {_rejoinNote} ({Settings.Server})";
    }

    /// <summary>Loads the AI (GPU) once and lists the chat models.</summary>
    public async Task EnsureAiAsync()
    {
        if (_ai != null) return;
        _ai = new VertexAi(m => Add(LineKind.System, m), System.IO.Path.Combine(_dataFolder, "voices.json"));
        Models = await _ai.ModelNamesAsync();
        if (string.IsNullOrEmpty(Settings.Model) || !Models.Contains(Settings.Model))
            Settings.Model = Models.FirstOrDefault(m => m.StartsWith("llama3.1")) ?? Models.FirstOrDefault() ?? "";
        Changed?.Invoke();
    }

    /// <summary>
    /// The tray app calls this once when it starts. With <see cref="VertexSettings.StartOnLaunch"/> on it does what the Start button does, so a PC that is Vertex's own comes up
    /// running by itself. It says in the log why it did not (no language model installed yet) instead of failing, and a failed start is a log line, never an exception.
    /// </summary>
    public async Task StartOnLaunchAsync()
    {
        if (!Settings.StartOnLaunch) return;
        if (NeedsModel)
        {
            Add(LineKind.System, "not starting by itself: the language model is not installed yet (press Install on the Start tab)");
            return;
        }
        try { await StartAsync(); }
        catch (Exception e) { Add(LineKind.Error, $"could not start by itself: {e.Message}"); }
    }

    /// <summary>Turns Vertex on: the AI, then the robot and the game as the settings say.</summary>
    public async Task StartAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (State != HostState.Idle) return;
            CancelUnload(); // started again within the grace period: the models are still loaded, this start is quick
            Settings.Save();
            SetStatus(HostState.Starting, "Loading the AI...");
            await EnsureAiAsync();
            if (string.IsNullOrEmpty(Settings.Model)) throw new InvalidOperationException($"the language model is not installed yet: press Install on the Start tab, or run `vertex install-models` ({ModelSetup.DefaultModel}, from models.spawndev.com)");
            SetStatus(HostState.Starting, $"Loading {Settings.Model}...");
            await _ai!.EnsureChatReadyAsync(Settings.Model);
            await LoadIntentsAsync();
            await OpenMemoryAsync();
            var character = Characters.Find(Settings.Character) ?? Character.BuiltIn[0];
            SetStatus(HostState.Starting, "Warming up ears and voice...");
            await _ai.WarmUpAsync(character.Voice);
            _companion = new Companion(_ai, Characters, character, Settings.Model);
            _companion.Log += AddLog;
            if (Settings.Reachy) await ConnectRobotCoreAsync();
            string? joinFailure = null;
            if (Settings.Minecraft)
            {
                // A game that cannot be joined leaves Vertex on without it (the robot, this panel): say why.
                try { await JoinGameCoreAsync(); }
                catch (Exception e) { SignInPrompt = null; joinFailure = e.Message; Add(LineKind.Error, $"could not join {Settings.Server}: {e.Message}"); }
            }
            // Started once its bodies are attached: the prompt and every body's tools are prefilled once, not again per
            // body (each Attach to a running companion prefills again).
            _companion.Start();
            // The first prefill of the system prompt and tools takes most of a minute in a new process (kernels compile on first use) and holds the GPU: speak only after it, or the
            // greeting stops after its first word and finishes a minute later.
            SetStatus(HostState.Starting, "Getting the language model ready (the first time takes about a minute)...");
            await Task.WhenAny(_companion.WhenPrimed, Task.Delay(TimeSpan.FromMinutes(6)));
            SetStatus(HostState.Playing, OnStatus());
            // The first join failed for a reason that may pass (live 2026-11-02: the server answered "An internal error occurred in your connection" a few seconds after he had been
            // disconnected, and he stayed out until someone looked): try again by the same schedule as a dropped game. Scheduled only now, once he is Playing, which the retry requires.
            if (joinFailure != null && Settings.Minecraft && ShouldRejoin(joinFailure)) ScheduleRejoin();
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

    private Vertex.Memory.VertexMemory? _memory;

    /// <summary>Opens the long-term memory (once per loaded AI). A memory that cannot be opened is a log line: Vertex plays on without it.</summary>
    private async Task OpenMemoryAsync()
    {
        if (!Settings.LongTermMemory || _memory != null || _ai == null) return;
        try
        {
            _memory = await _ai.OpenMemoryAsync(System.IO.Path.Combine(_dataFolder, "memory"));
            Add(LineKind.System, $"long-term memory: {_memory.Store.Count} memories");
        }
        catch (Exception e) { Add(LineKind.Error, "long-term memory is off: " + e.Message); }
    }

    /// <summary>Joins the Minecraft server while Vertex is on.</summary>
    public Task JoinGameAsync() => WhileOnAsync("Joining the game...", async () =>
    {
        CancelRejoin(); // a person pressing Join takes over from a pending automatic rejoin
        Settings.Minecraft = true;
        Settings.Save();
        await JoinGameCoreAsync();
    });

    /// <summary>Leaves the Minecraft server; Vertex stays on (robot, panel).</summary>
    public Task LeaveGameAsync() => WhileOnAsync("Leaving the game...", async () =>
    {
        CancelRejoin(); // leaving on purpose means staying out
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
            _reachy = await ReachyChannel.ConnectAsync(Settings.ReachyHost, _ai!, Settings.ReachySmartTurn, Settings.ReachyLiveWords, Settings.ReachyFaces, Settings.ReachyStreamSpeech);
            _reachy.Arrived += a => { if (_companion?.TryGreet(a.Name, a.Absence, _reachy) == true) Changed?.Invoke(); };
            _reachy.Hearing += (text, final) => { LiveCaption = final ? "" : text; Changed?.Invoke(); };
            if (!string.IsNullOrWhiteSpace(Settings.ReachySpeaker)) _reachy.RoomSpeakerName = Settings.ReachySpeaker.Trim();
            _reachy.AnswersEverything = Settings.ReachyAnswersEverything;
            _reachy.IdentifySpeakers = Settings.ReachyRecognizeVoices;
            _reachy.SaveHeardFolder = Settings.ReachySaveHeard ? HeardFolder : null;
            _reachy.LogWords = Settings.ReachyLogWords;
            _reachy.Log += m => Add(LineKind.System, "[reachy] " + m);
            _robot = new RobotBody(_reachy, _companion) { FaceSpeaker = Settings.ReachyFaceSpeaker, FollowFaces = Settings.ReachyFollowFaces };
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
            // Not the action bar: a plugin can rewrite it many times a second (RealisticSeasons' temperature), which pushed everything else out of the log in 20 seconds. The brain reads it and logs what matters.
            _client.ChatReceived += m => { if (m.Kind != ChatKind.ActionBar) Add(LineKind.Chat, m.Text); };
            var client = _client;
            // Kicked or the server went away: leave the game, Vertex stays on.
            _client.Disconnected += r => { Add(LineKind.System, "disconnected: " + r); _ = Task.Run(() => LeftGameAsync(client, r)); };
            if (Settings.Voice)
            {
                _voice = new SimpleVoiceChat(_client, Settings.Server);
                _voice.Log += m => Add(LineKind.System, "voice: " + m);
            }
            _brain = new VertexBrain(_client, _voice, _companion, $"{Settings.Server}_{Settings.Port}")
            {
                SelfDirectedPlay = Settings.SelfDirectedPlay,
                TurnOffGodMode = Settings.TurnOffGodMode,
                LongTerm = Settings.LongTermMemory ? _memory : null,
                MemoryScope = $"{Settings.Server}:{Settings.Port}",
            };
            _brain.Log += AddLog;
            _brain.LearnFromDanger();
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
    private async Task LeftGameAsync(MinecraftClient client, string? reason)
    {
        await _gate.WaitAsync();
        try
        {
            if (_client != client) return;
            await LeaveGameCoreAsync();
            if (State == HostState.Playing) SetStatus(HostState.Playing, OnStatus());
            // The server went away (a restart, a network drop, a proxy cutting the connection) while Vertex was meant to be in the game: go back by itself, instead of waiting for
            // someone to press Join. Not after a ban, a kick or a missing sign-in, which are the server's answer, and never after a Leave or Stop (they cancel this).
            if (State == HostState.Playing && Settings.Minecraft && ShouldRejoin(reason)) ScheduleRejoin();
            else if (Settings.Minecraft) Add(LineKind.System, "not rejoining by itself: " + reason);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Whether a disconnect reason is worth going back after. A ban, a kick, a whitelist or a sign-in problem is an answer, not an accident.</summary>
    internal static bool ShouldRejoin(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return true;
        var r = reason.ToLowerInvariant();
        string[] answers = ["banned", "ban ", "white-list", "whitelist", "sign in", "online mode", "invalid session", "outdated", "incompatible", "requires", "kicked", "not authorized", "disconnected by client"];
        return !answers.Any(r.Contains);
    }

    /// <summary>The wait before rejoin attempt number <c>attempt</c> (0 is the first): 15 s, 30 s, 1 min, 2 min, then every 5 min. Tests shorten it.</summary>
    internal Func<int, TimeSpan> RejoinDelay { get; set; } = attempt => attempt switch { 0 => TimeSpan.FromSeconds(15), 1 => TimeSpan.FromSeconds(30), 2 => TimeSpan.FromMinutes(1), 3 => TimeSpan.FromMinutes(2), _ => TimeSpan.FromMinutes(5) };

    private CancellationTokenSource? _rejoinCts;
    private string? _rejoinNote;

    private void CancelRejoin()
    {
        Interlocked.Exchange(ref _rejoinCts, null)?.Cancel();
        _rejoinNote = null;
    }

    private void ScheduleRejoin()
    {
        CancelRejoin();
        var cts = _rejoinCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; !cts.IsCancellationRequested; attempt++)
            {
                var delay = RejoinDelay(attempt);
                _rejoinNote = $"rejoining in {delay.TotalSeconds:0} s";
                SetStatus(State, OnStatus());
                try { await Task.Delay(delay, cts.Token); }
                catch (OperationCanceledException) { return; }
                try
                {
                    await _gate.WaitAsync(cts.Token);
                    try
                    {
                        // Everything may have changed while waiting: Stop, Leave, a person pressing Join.
                        if (cts.IsCancellationRequested || State != HostState.Playing || !Settings.Minecraft) return;
                        if (_client != null) return;
                        _rejoinNote = "rejoining...";
                        SetStatus(HostState.Playing, OnStatus());
                        await JoinGameCoreAsync();
                        _rejoinNote = null;
                        Add(LineKind.System, "rejoined " + Settings.Server);
                        SetStatus(HostState.Playing, OnStatus());
                        return;
                    }
                    finally { _gate.Release(); }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Add(LineKind.System, $"could not rejoin {Settings.Server} yet: {e.Message}"); }
            }
        });
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
            ScheduleUnload();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// How long the AI (the language model, speech, voice and face models: about 11 GB of an RTX 2060's 12) stays loaded on the GPU after Stop. A Stop followed by a Start within this time
    /// is quick; after it Vertex gives the GPU back, so "off" really is off on a PC that plays games too (measured 2026-11-01: after Stop the card still held 11.3 GB until the app quit).
    /// <see cref="Timeout.InfiniteTimeSpan"/> keeps it loaded until the app quits (the old behaviour).
    /// </summary>
    internal TimeSpan UnloadAfterStop { get; set; } = TimeSpan.FromMinutes(2);

    private CancellationTokenSource? _unloadCts;

    private void ScheduleUnload()
    {
        CancelUnload();
        if (UnloadAfterStop == Timeout.InfiniteTimeSpan || _ai == null) return;
        var cts = _unloadCts = new CancellationTokenSource();
        var delay = UnloadAfterStop;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token); await UnloadAiAsync(cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Add(LineKind.Error, $"could not give the GPU back: {e.Message}"); }
        });
    }

    private void CancelUnload() => Interlocked.Exchange(ref _unloadCts, null)?.Cancel();

    /// <summary>Disposes the AI while Vertex is off. Shown as "Stopping" so a Start cannot begin halfway through; a Start that wins the gate first cancels it.</summary>
    private async Task UnloadAiAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State != HostState.Idle || _ai == null || ct.IsCancellationRequested) return;
            var ai = _ai;
            SetStatus(HostState.Stopping, "Giving the GPU back...");
            _ai = null;
            _memory?.Dispose();
            _memory = null;
            await ai.DisposeAsync();
            SetStatus(HostState.Idle, "Off");
            Add(LineKind.System, "gave the GPU back (Vertex is off); the next Start loads the models again");
        }
        finally { _gate.Release(); }
    }

    private async Task TearDownAsync()
    {
        CancelRejoin();
        // The conversation stops first (no more turns, the mouth falls silent), so taking the bodies away does not
        // prefill a prompt for nobody; then the game is left and the robot parked.
        var companion = _companion;
        if (companion != null) await companion.DisposeAsync();
        await LeaveGameCoreAsync();
        await DisconnectRobotCoreAsync();
        _companion = null;
    }

    /// <summary>The name what is typed on the dashboard is said under: the operator's own name, else the robot's usual speaker, else "Someone". (It was hard-coded to a person's name.)</summary>
    internal static string OperatorLabel(VertexSettings settings)
        => !string.IsNullOrWhiteSpace(settings.OperatorName) ? settings.OperatorName.Trim()
         : !string.IsNullOrWhiteSpace(settings.ReachySpeaker) ? settings.ReachySpeaker.Trim()
         : "Someone";

    /// <summary>The operator talks to Vertex (as if in chat, always addressed).</summary>
    public void Tell(string text)
    {
        if (_companion == null || string.IsNullOrWhiteSpace(text)) return;
        Add(LineKind.Chat, "You: " + text);
        _companion.Post(new SaidEvent(Guid.Empty, OperatorLabel(Settings), text, false));
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
        CancelUnload();
        CancelRejoin();
        await TearDownAsync();
        _memory?.Dispose();
        _memory = null;
        if (_ai != null) await _ai.DisposeAsync();
    }
}
