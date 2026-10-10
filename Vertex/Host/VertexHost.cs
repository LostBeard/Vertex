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

    private static string PathFor() => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vertex", "settings.json");

    public static VertexSettings Load()
    {
        try { return JsonSerializer.Deserialize<VertexSettings>(File.ReadAllText(PathFor())) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFor())!);
        File.WriteAllText(PathFor(), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
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
    private VertexAi? _ai;
    private MinecraftClient? _client;
    private SimpleVoiceChat? _voice;
    private VertexBrain? _brain;

    public VertexSettings Settings { get; } = VertexSettings.Load();
    public CharacterStore Characters { get; } = new();
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
            _client = new MinecraftClient(profile, new ClientOptions { Log = m => { if (m.StartsWith("disconnected") || m.StartsWith("joined") || m.StartsWith("spawned")) Add(LineKind.System, m); } });
            _client.ChatReceived += m => Add(LineKind.Chat, m.Text);
            _client.Disconnected += r => { Add(LineKind.System, "disconnected: " + r); _ = StopAsync(); };
            if (Settings.Voice)
            {
                _voice = new SimpleVoiceChat(_client, Settings.Server);
                _voice.Log += m => Add(LineKind.System, "voice: " + m);
            }
            var character = Characters.Find(Settings.Character) ?? Character.BuiltIn[0];
            _brain = new VertexBrain(_client, _voice, _ai, Characters, character, Settings.Model);
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
