using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SpawnDev.AI;
using SpawnDev.AI.Server;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.AI;

namespace Vertex.Brain;

/// <summary>Something the brain should consider: speech, chat, or a finished task.</summary>
public abstract record BrainEvent;
public sealed record SaidEvent(Guid Speaker, string SpeakerName, string Text, bool Voice) : BrainEvent;
public sealed record TaskEvent(TaskOutcome Outcome) : BrainEvent;

/// <summary>
/// Vertex's mind. Listens to voice (Whisper) and chat, decides when it is being talked to, and answers as the
/// current character through the language model, which acts in the game by calling tools (follow, collect,
/// give...). Long jobs run in the background and the brain reports back when they end. A fast reflex layer
/// (eat when hungry, fight back or flee when hit, come up for air) runs every tick without the language model.
/// </summary>
public sealed class VertexBrain : IAsyncDisposable
{
    private readonly Channel<BrainEvent> _events = Channel.CreateUnbounded<BrainEvent>();
    private readonly List<AiChatMessage> _history = [];
    private readonly List<string> _memory = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly CharacterStore _characters;
    private readonly string _memoryFile;
    private Task? _loop;
    private DateTime _lastExchange = DateTime.MinValue;
    private Guid? _lastPartner;

    public MinecraftClient Client { get; }
    public SimpleVoiceChat? Voice { get; }
    public VertexAi Ai { get; }
    public Mouth Mouth { get; }
    public Ears? Ears { get; }
    public Skills Skills { get; }
    public TaskRunner Tasks { get; } = new();
    public Reflexes Reflexes { get; }
    public Character Character { get; private set; }
    /// <summary>The chat model (an Ollama-cache name such as "llama3.1:8b").</summary>
    public string Model { get; set; }
    /// <summary>Name of whoever spoke last (what "me" means in a request).</summary>
    public string? CurrentSpeaker { get; private set; }

    public event Action<string>? Log;

    public VertexBrain(MinecraftClient client, SimpleVoiceChat? voice, VertexAi ai, CharacterStore characters, Character character, string model)
    {
        Client = client;
        Voice = voice;
        Ai = ai;
        Model = model;
        _characters = characters;
        Character = character;
        Skills = new Skills(client);
        Mouth = new Mouth(client, voice, ai.SpeakAsync) { VoiceId = character.Voice };
        Mouth.Log += m => Log?.Invoke("[mouth] " + m);
        if (voice != null)
        {
            Ears = new Ears(voice, ai.TranscribeAsync);
            Ears.Log += m => Log?.Invoke("[ears] " + m);
            Ears.Heard += h => _events.Writer.TryWrite(new SaidEvent(h.Speaker, NameOf(h.Speaker), h.Text, true));
        }
        Reflexes = new Reflexes(this);
        _memoryFile = Path.Combine(characters.Folder, "..", "memory.json");
        try { if (File.Exists(_memoryFile)) _memory.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_memoryFile)) ?? []); }
        catch (JsonException) { }

        foreach (var tool in GameTools.Create(this))
        {
            if (tool is GameTool g) g.OnRun = m => Log?.Invoke("  " + m);
            ai.Tools.Register(tool);
        }
        client.ChatReceived += OnChat;
        Tasks.Finished += o => _events.Writer.TryWrite(new TaskEvent(o));
    }

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private string NameOf(Guid id) => Client.Players.TryGetValue(id, out var p) ? p.Name : "someone";

    private void OnChat(ChatMessage m)
    {
        if (m.Kind != ChatKind.Player || m.SenderId == Client.Profile.Id || m.SenderId == null) return;
        _events.Writer.TryWrite(new SaidEvent(m.SenderId.Value, m.SenderName ?? NameOf(m.SenderId.Value), m.Content, false));
    }

    /// <summary>Lets code (and tests) hand the brain something to consider.</summary>
    public void Post(BrainEvent e) => _events.Writer.TryWrite(e);

    // ---------------------------------------------------------------- deciding to answer

    /// <summary>
    /// Talk back when our name is said, when we are mid-conversation with this player (within 45 s), or when
    /// they are the only other player around and spoke out loud (they can only be talking to us).
    /// </summary>
    public bool IsAddressed(SaidEvent e)
    {
        if (e.Speaker == Guid.Empty) return true; // the operator's console talks only to us
        var text = e.Text.ToLowerInvariant();
        var name = Character.Name.ToLowerInvariant();
        if (text.Contains(name) || text.Contains("vertex")) return true;
        if (_lastPartner == e.Speaker && DateTime.UtcNow - _lastExchange < TimeSpan.FromSeconds(45)) return true;
        var others = Client.Entities.Values.Count(x => x.IsPlayer && x.Uuid != Client.Profile.Id && x.DistanceTo(Client.Position.X, Client.Position.Y, Client.Position.Z) < 32);
        return e.Voice && others <= 1;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        await foreach (var e in _events.Reader.ReadAllAsync(ct))
        {
            try
            {
                switch (e)
                {
                    case SaidEvent s when IsAddressed(s):
                        CurrentSpeaker = s.SpeakerName;
                        _lastPartner = s.Speaker;
                        _lastExchange = DateTime.UtcNow;
                        Mouth.Hush();
                        await RespondAsync($"{s.SpeakerName} says{(s.Voice ? " (out loud)" : " (in chat)")}: {s.Text}", ct);
                        break;
                    case TaskEvent t:
                        Log?.Invoke($"task ended: {t.Outcome.Task}: {t.Outcome.Summary}");
                        // Report back in character (failures always; successes unless very quiet).
                        if (!t.Outcome.Success || Character.Style.Chattiness >= 0.2)
                            await RespondAsync($"[Game update, not a friend talking: you finished {t.Outcome.Task}. Result: {t.Outcome.Summary}] Tell your friends how it went, in one short first-person sentence.", ct, allowTools: false);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log?.Invoke("brain error: " + ex.Message);
            }
        }
    }

    // ---------------------------------------------------------------- talking

    public string SystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Character.Persona);
        sb.AppendLine();
        sb.AppendLine($"Your name is {Character.Name}. You are playing Minecraft Java Edition together with your friends, as a real player in the world. " +
                      "Your friends talk to you out loud or in chat.");
        sb.AppendLine("Rules:");
        sb.AppendLine("- Speak as yourself in the first person (\"I\"), to your friends (\"you\"). Answer in one or two short spoken sentences, friendly and simple (a child may be listening). No lists, emojis or markdown.");
        sb.AppendLine("- Tool results describe what YOU are doing. If a job has only started, say you are on your way or working on it, not that it is done.");
        sb.AppendLine("- To DO anything in the game, call a tool. Never claim you did something unless a tool did it.");
        sb.AppendLine("- If you are asked to do something you have no tool for, say so kindly and suggest something you can do.");
        sb.AppendLine("- Never be mean, never use bad words, and never attack players.");
        sb.AppendLine();
        sb.AppendLine("Right now:");
        var (x, y, z) = Client.Position;
        sb.AppendLine($"- You are at x {x:0} y {y:0} z {z:0} in {Client.World.Dimension.Replace("minecraft:", "")}, " +
                      $"health {Client.Health / 2:0.#} of 10 hearts, food {Client.Food / 2:0.#} of 10, game mode {Client.GameMode.ToString().ToLowerInvariant()}.");
        var time = Client.World.DayTime % 24000;
        sb.AppendLine($"- It is {(time is >= 0 and < 12000 ? "daytime" : time < 13000 ? "sunset" : time < 23000 ? "night" : "sunrise")}.");
        sb.AppendLine($"- You are carrying: {Skills.InventorySummary()}. Holding: {(Client.Inventory.MainHand.IsEmpty ? "nothing" : Client.Inventory.MainHand.ToString())}.");
        sb.AppendLine($"- You are {(Tasks.Current is string task ? task : "not doing anything special")}.");
        var near = Client.Entities.Values.Where(e => e.DistanceTo(x, y, z) < 24 && e.Uuid != Client.Profile.Id).OrderBy(e => e.DistanceTo(x, y, z)).Take(12)
            .Select(e => e.IsPlayer ? $"{e.PlayerName} (player, {e.DistanceTo(x, y, z):0} blocks)" : $"{e.Name.Replace("minecraft:", "").Replace('_', ' ')} ({e.DistanceTo(x, y, z):0} blocks)").ToList();
        sb.AppendLine($"- Nearby: {(near.Count == 0 ? "nobody" : string.Join(", ", near))}.");
        if (_memory.Count > 0) sb.AppendLine("- Things you remember: " + string.Join("; ", _memory.TakeLast(12)));
        var others = _characters.All().Where(c => c.Name != Character.Name).Select(c => c.Name);
        sb.AppendLine($"- Other characters you can switch to: {string.Join(", ", others)}.");
        return sb.ToString();
    }

    /// <summary>One turn: prompt + recent history + this message, tools on; speaks the reply.</summary>
    public async Task<string> RespondAsync(string userMessage, CancellationToken ct = default, bool allowTools = true)
    {
        var messages = new List<AiChatMessage> { new("system", SystemPrompt()) };
        lock (_history) messages.AddRange(_history.TakeLast(16));
        messages.Add(new AiChatMessage("user", userMessage));
        Log?.Invoke("> " + userMessage);
        var tools = Ai.Chat.Tools;
        if (!allowTools) Ai.Chat.Tools = null;
        AiChatResult result;
        try
        {
            result = await Ai.Chat.ChatAsync(new AiChatRequest
            {
                Model = Model,
                Messages = messages,
                Options = new AiGenerationOptions { MaxOutputTokens = 160, Strategy = "top_p", Temperature = 0.7f, TopP = 0.9f, RepetitionPenalty = 1.1f },
            }, ct);
        }
        finally
        {
            Ai.Chat.Tools = tools;
        }
        var reply = AiChatEngine.StripToolCalls(AiChatEngine.StripThinking(result.TextWithoutToolCalls)).Trim();
        foreach (var call in result.ToolCalls) Log?.Invoke($"  tool {call.Name}({call.ArgumentsJson})");
        Log?.Invoke("< " + reply);
        lock (_history)
        {
            _history.Add(new AiChatMessage("user", userMessage));
            _history.Add(new AiChatMessage("assistant", reply.Length > 0 ? reply : "(did it)"));
            if (_history.Count > 40) _history.RemoveRange(0, _history.Count - 40);
        }
        // Never speak a fragment ("I", ".") - a model that only called a tool sometimes leaves one behind.
        if (reply.Count(char.IsLetter) >= 4) Mouth.Say(reply);
        return reply;
    }

    // ---------------------------------------------------------------- characters and memory

    public string SwitchCharacter(string name)
    {
        var c = _characters.Find(name);
        if (c == null) return $"no character called {name}; known: {string.Join(", ", _characters.All().Select(x => x.Name))}";
        Character = c;
        Mouth.VoiceId = c.Voice;
        lock (_history) _history.Clear(); // a new character starts a new conversation
        Log?.Invoke($"now playing as {c.Name}");
        return $"you are now {c.Name}";
    }

    public string Remember(string fact)
    {
        if (string.IsNullOrWhiteSpace(fact)) return "nothing to remember";
        lock (_memory)
        {
            _memory.Add(fact.Trim());
            try { File.WriteAllText(_memoryFile, JsonSerializer.Serialize(_memory)); } catch (IOException) { }
        }
        return "remembered";
    }

    public async ValueTask DisposeAsync()
    {
        Client.ChatReceived -= OnChat;
        Tasks.Stop();
        Reflexes.Dispose();
        _events.Writer.TryComplete();
        _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch (OperationCanceledException) { } }
        Ears?.Dispose();
        await Mouth.DisposeAsync();
    }
}
