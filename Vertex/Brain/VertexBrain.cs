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
/// <summary>A player came to play (joined, or is back after a while away).</summary>
public sealed record JoinedEvent(PlayerMemory Player, DateTime? LastSeen) : BrainEvent;
/// <summary>Nobody needs Vertex right now: time to work on a long-running goal.</summary>
public sealed record GoalEvent(Objective Goal) : BrainEvent;

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
    private readonly CancellationTokenSource _cts = new();
    private readonly CharacterStore _characters;
    private Task? _loop;
    private DateTime _lastExchange = DateTime.MinValue;
    private Guid? _lastPartner;
    private readonly Dictionary<string, GameTool> _tools = [];
    private int _toolRuns;
    private Task? _idleLoop;
    private DateTime _started;
    private DateTime _lastActivity = DateTime.UtcNow;
    private Objective? _pursuing;
    private bool _goalQueued;

    /// <summary>How long Vertex waits with nothing to do (nobody talking, no job) before working on a goal by itself.</summary>
    public TimeSpan IdleBeforeGoals { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>When idle with no goal, study unknown (modded) blocks nearby by touch.</summary>
    public bool StudyWhenIdle { get; set; } = true;
    /// <summary>How often the idle check runs.</summary>
    public TimeSpan IdleCheckEvery { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>The clock (tests move it).</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

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
    /// <summary>Facts and named places for this server's world.</summary>
    public WorldMemory Memory { get; }

    public event Action<string>? Log;

    /// <param name="world">Which world's memory to use (one per server, e.g. "mc.spawndev.com_25565").</param>
    public VertexBrain(MinecraftClient client, SimpleVoiceChat? voice, VertexAi ai, CharacterStore characters, Character character, string model, string world = "default")
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
        Memory = WorldMemory.Load(Path.Combine(characters.Folder, "..", "worlds"), world);

        foreach (var tool in GameTools.Create(this))
        {
            if (tool is GameTool g)
            {
                g.OnRun = m => { Interlocked.Increment(ref _toolRuns); Log?.Invoke("  " + m); };
                _tools[g.Name] = g;
            }
            ai.Tools.Register(tool);
        }
        client.ChatReceived += OnChat;
        client.PlayerAdded += OnPlayerAdded;
        Tasks.Requester = () => _pursuing?.For ?? CurrentSpeaker;
        Tasks.GoalOf = () => _pursuing;
        Tasks.Finished += o => _events.Writer.TryWrite(new TaskEvent(o));
    }

    public void Start()
    {
        _started = Now();
        foreach (var p in Client.Players.Values.Where(p => p.Id != Client.Profile.Id)) Memory.Meet(p.Id, p.Name, _started);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _idleLoop = Task.Run(() => IdleLoopAsync(_cts.Token));
    }

    /// <summary>
    /// A player joined the server. Everyone is remembered; someone who comes to play (first time ever, or back after
    /// a while away) is greeted. Players already on when Vertex arrives, and a proxy's server switch (the list is
    /// sent again), are not greeting moments.
    /// </summary>
    private void OnPlayerAdded(PlayerInfo p)
    {
        if (p.Id == Client.Profile.Id || _loop == null) return;
        var now = Now();
        var (memory, lastSeen) = Memory.Meet(p.Id, p.Name, now);
        if (now - _started > TimeSpan.FromSeconds(10) && (lastSeen == null || now - lastSeen > TimeSpan.FromMinutes(10)))
            _events.Writer.TryWrite(new JoinedEvent(memory, lastSeen));
    }

    /// <summary>
    /// Every few seconds: keeps "last seen" fresh for everyone on, and when nobody has needed Vertex for a while
    /// (nobody talking to it, no job running) and it has a long-running goal, works on that goal by itself.
    /// </summary>
    private async Task IdleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(IdleCheckEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = Now();
                foreach (var p in Client.Players.Values.Where(p => p.Id != Client.Profile.Id)) Memory.Seen(p.Id, now);
                if (Tasks.IsBusy) { _lastActivity = now; continue; }
                if (_goalQueued || !Client.IsSpawned || now - _lastActivity < IdleBeforeGoals) continue;
                if (Memory.ActiveGoals.FirstOrDefault() is not { } goal)
                {
                    // Nothing to do: be curious about the blocks around that Vertex does not know (no language model
                    // needed; nothing is broken, it only walks up to them and feels them out).
                    if (StudyWhenIdle && Skills.UnstudiedBlocks(16).Count > 0)
                    {
                        _lastActivity = now;
                        Tasks.Start("studying blocks I don't know", ct => Skills.StudyBlocksAsync(16, ct));
                    }
                    continue;
                }
                _goalQueued = true;
                _lastActivity = now;
                _events.Writer.TryWrite(new GoalEvent(goal));
            }
        }
        catch (OperationCanceledException) { }
    }

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
                        _lastActivity = Now();
                        _pursuing = null; // a friend asking comes first; the goal waits for the next quiet spell
                        Mouth.Hush();
                        var said = $"{s.SpeakerName} says{(s.Voice ? " (out loud)" : " (in chat)")}: {s.Text}";
                        // System One: an urgent command we are sure of runs NOW; the language model only words the reply.
                        if (await FastActAsync(s) is { } done)
                            await RespondAsync($"{said}\n[You already did it: {done}] Answer in one short sentence; do not call tools.", ct, allowTools: false);
                        else
                            await RespondAsync(said, ct);
                        break;
                    case TaskEvent t:
                        Log?.Invoke($"task ended: {t.Outcome.Task}: {t.Outcome.Summary}");
                        _lastActivity = Now();
                        var forGoal = t.Outcome.Interruption || t.Outcome.Goal is not { Done: null } stepGoal ? null : stepGoal;
                        Remember(t.Outcome);
                        if (forGoal != null)
                            await ReviewGoalStepAsync(forGoal, t.Outcome, ct);
                        // Report back in character (failures always; successes unless very quiet).
                        else if (!t.Outcome.Success || Character.Style.Chattiness >= 0.2)
                            await RespondAsync($"[Game update, not a friend talking: you finished {t.Outcome.Task}. Result: {t.Outcome.Summary}] Tell your friends how it went, in one short first-person sentence.", ct, allowTools: false);
                        break;
                    case JoinedEvent j when Character.Style.Chattiness >= 0.2:
                        await RespondAsync($"[Game update, not a friend talking: {j.Player.Name} just came to play. {Acquaintance(j.Player, j.LastSeen)}] "
                            + $"Say hello to {j.Player.Name}, using the name {j.Player.Name}, in one short, warm sentence (you may mention last time). Do not call tools.", ct, allowTools: false);
                        break;
                    case GoalEvent g:
                        await PursueGoalAsync(g.Goal, ct);
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

    /// <summary>
    /// What a finished job means for memory: a success done for someone goes into their history; a job done for a
    /// goal (asked for while working on it) is a step of that goal's progress.
    /// </summary>
    private void Remember(TaskOutcome o)
    {
        if (o.Interruption) return; // the job it cut into resumes and ends (and is remembered) later
        var now = Now();
        if (o.Success && o.For != null && Memory.Person(o.For) is { } person)
            Memory.AddHistory(person, $"{o.Task}: {o.Summary}", now);
        if (o.Goal is { } goal)
            Memory.AddProgress(goal, $"{(o.Success ? "" : "tried ")}{o.Task}: {o.Summary}");
    }

    /// <summary>
    /// A goal step ended: has the goal been reached? The model decides, with finish_goal at hand (without this it
    /// kept taking steps for a goal it had already met: collected 5 dirt three times over on 2026-10-11).
    /// </summary>
    private async Task ReviewGoalStepAsync(Objective goal, TaskOutcome step, CancellationToken ct)
    {
        _pursuing = goal; // a step it starts from here is still for the goal (the task keeps it)
        try
        {
            await RespondAsync($"[Game update, not a friend talking: you finished {step.Task}, a step toward your goal \"{goal.Text}\". "
                + $"Result: {step.Summary}. Is the goal reached now? If yes, call finish_goal. Then tell your friends how it went, in one short first-person sentence.]", ct);
        }
        finally { _pursuing = null; }
    }

    /// <summary>One step toward a goal, chosen by the language model (one tool call), with nobody asking.</summary>
    private async Task PursueGoalAsync(Objective goal, CancellationToken ct)
    {
        try
        {
            if (Tasks.IsBusy || goal.Done != null) return;
            _pursuing = goal; // tasks started during this turn carry the goal (TaskRunner.GoalOf)
            Log?.Invoke($"working on goal: {goal.Text}");
            var progress = goal.Progress.Count == 0 ? "nothing yet" : string.Join("; ", goal.Progress.TakeLast(5));
            await RespondAsync($"[Game update, not a friend talking: nobody needs you right now, so work on your goal \"{goal.Text}\""
                + (goal.For != null ? $" (for {goal.For})" : "") + $". Done so far: {progress}. "
                + "Call ONE tool for the next step (gather, craft, build, explore...). If the goal is already reached, call finish_goal. "
                + "Then say in one short sentence what you are doing.]", ct);
        }
        finally
        {
            // Not "if no task is running": a quick step can END before the reply does (and then looked like no step
            // at all, seen 2026-10-11). The task carries its goal; the idle check waits for it to end.
            _pursuing = null;
            _goalQueued = false;
        }
    }

    /// <summary>"You met Aubri on Oct 10 and have played together 3 times; last seen 2 days ago; last time: built a house."</summary>
    private string Acquaintance(PlayerMemory p, DateTime? lastSeen)
    {
        if (lastSeen == null) return "You have never met them before.";
        var ago = Now() - lastSeen.Value;
        var when = ago.TotalDays >= 2 ? $"{(int)ago.TotalDays} days ago" : ago.TotalHours >= 20 ? "yesterday" : ago.TotalHours >= 2 ? $"{(int)ago.TotalHours} hours ago" : "a little while ago";
        var last = p.History.Count > 0 ? $" Last time: {p.History[^1][12..]}." : "";
        return $"You met them on {p.FirstMet:MMM d} and have played together {p.Visits} times; you last saw them {when}.{last}";
    }

    /// <summary>
    /// Runs the tool for a request the fast matcher is sure about (Ai.Intents, when loaded). Returns the tool's
    /// result, or null to let the language model decide.
    /// </summary>
    public async Task<string?> FastActAsync(SaidEvent s)
    {
        if (Ai.Intents is not { } intents) return null;
        var match = await intents.MatchAsync(s.Text, Character.Name);
        if (match == null || !_tools.TryGetValue(match.Intent.Tool, out var tool)) return null;
        Log?.Invoke($"  fast: \"{match.Cleaned}\" = {match.Intent.Tool} ({match.Score:0.00} vs {match.RunnerUp:0.00}, {match.Milliseconds:0} ms)");
        var args = match.Intent.AboutSpeaker ? JsonSerializer.Serialize(new { player = s.SpeakerName }) : "{}";
        var result = await tool.ExecuteAsync(args);
        return result.TextForModel;
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
        sb.AppendLine("- When a friend gives you a long-term goal (\"a goal for you\", \"work on it over time\", \"whenever you have time\"), call set_goal so you remember it; you may also start on it.");
        sb.AppendLine("- Never be mean, never use bad words, and never attack players.");
        sb.AppendLine();
        sb.AppendLine("Right now:");
        var (x, y, z) = Client.Position;
        sb.AppendLine($"- You are at x {x:0} y {y:0} z {z:0} in {Client.World.Dimension.Bare()}, " +
                      $"health {Client.Health / 2:0.#} of 10 hearts, food {Client.Food / 2:0.#} of 10, game mode {Client.GameMode.ToString().ToLowerInvariant()}.");
        var time = Client.World.DayTime % 24000;
        sb.AppendLine($"- It is {(time is >= 0 and < 12000 ? "daytime" : time < 13000 ? "sunset" : time < 23000 ? "night" : "sunrise")}.");
        sb.AppendLine($"- You are carrying: {Skills.InventorySummary()}. Holding: {(Client.Inventory.MainHand.IsEmpty ? "nothing" : Client.Inventory.MainHand.ToString())}.");
        sb.AppendLine($"- You are {(Tasks.Current is string task ? task : "not doing anything special")}.");
        var near = Client.Entities.Values.Where(e => e.DistanceTo(x, y, z) < 24 && e.Uuid != Client.Profile.Id).OrderBy(e => e.DistanceTo(x, y, z)).Take(12)
            .Select(e => e.IsPlayer ? $"{e.PlayerName} (player, {e.DistanceTo(x, y, z):0} blocks)" : $"{e.Name.Bare().Replace('_', ' ')} ({e.DistanceTo(x, y, z):0} blocks)").ToList();
        sb.AppendLine($"- Nearby: {(near.Count == 0 ? "nobody" : string.Join(", ", near))}.");
        var playersHere = Client.Entities.Values.Where(e => e.IsPlayer && e.PlayerName != null && e.DistanceTo(x, y, z) < 64).Select(e => e.PlayerName!)
            .Append(CurrentSpeaker ?? "").ToList();
        if (Memory.Facts.Count > 0) sb.AppendLine("- Things you remember: " + string.Join("; ", Memory.FactsFor(playersHere)));
        // The people here, by UUID (the one who is talking too, wherever they are).
        var ids = Client.Entities.Values.Where(e => e.IsPlayer && e.Uuid != Client.Profile.Id && e.DistanceTo(x, y, z) < 64).Select(e => e.Uuid)
            .Concat(Client.Players.Values.Where(p => p.Name == CurrentSpeaker).Select(p => p.Id)).Distinct();
        foreach (var id in ids)
            if (Memory.People.TryGetValue(id, out var person)) sb.AppendLine($"- About {person.Name}: {About(person)}");
        var goals = Memory.ActiveGoals;
        if (goals.Count > 0)
            sb.AppendLine("- Your long-term goals (work on them when nobody needs you): " + string.Join("; ", goals.Select(g =>
                g.Text + (g.For != null ? $" (for {g.For})" : "") + (g.Progress.Count > 0 ? $" - last step: {g.Progress[^1]}" : " - not started"))) + ".");
        if (Memory.Places.Count > 0)
            sb.AppendLine("- Places you know: " + string.Join(", ", Memory.Places.Select(p => p.Dimension == Client.World.Dimension
                ? $"{p.Name} ({Math.Sqrt((p.X + 0.5 - x) * (p.X + 0.5 - x) + (p.Z + 0.5 - z) * (p.Z + 0.5 - z)):0} blocks away)"
                : $"{p.Name} (in the {p.Dimension.Bare().Replace("the_", "")})")) + ".");
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
        var runsBefore = Volatile.Read(ref _toolRuns);
        var reply = await ChatOnceAsync(messages, allowTools, ct);
        // An 8B model sometimes SAYS it is doing something ("I'm on my way") without calling a tool, and then nothing
        // happens in the game (seen 2026-10-11 in BrainTests.ComeHere). Once, tell it so and let it call the tool.
        if (allowTools && Volatile.Read(ref _toolRuns) == runsBefore && ClaimsAction(reply))
        {
            Log?.Invoke("  (said it would act but called no tool: asking again)");
            messages.Add(new AiChatMessage("assistant", reply));
            messages.Add(new AiChatMessage("user", "[You said that, but you did not call any tool, so NOTHING happened in the game. Call the right tool now, then answer in one short sentence.]"));
            reply = await ChatOnceAsync(messages, true, ct);
        }
        // Nothing said and nothing done is no answer at all (seen 2026-10-11 for "remember that I love pink
        // axolotls"): once, tell it so.
        else if (allowTools && Volatile.Read(ref _toolRuns) == runsBefore && reply.Count(char.IsLetter) < 4)
        {
            Log?.Invoke("  (no answer and no tool: asking again)");
            messages.Add(new AiChatMessage("assistant", reply));
            messages.Add(new AiChatMessage("user", "[You gave no answer and called no tool. If you were asked to do something, call the right tool now; then answer in one short sentence.]"));
            reply = await ChatOnceAsync(messages, true, ct);
        }
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

    private async Task<string> ChatOnceAsync(List<AiChatMessage> messages, bool allowTools, CancellationToken ct)
    {
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
        // An empty reply with no tool call: show what the model actually produced (and why it stopped).
        if (reply.Length == 0 && result.ToolCalls.Count == 0)
            Log?.Invoke($"  (empty reply; stop {result.Stop}, {result.GeneratedTokens} tokens, raw: {result.Text.Replace('\n', ' ')})");
        Log?.Invoke("< " + reply);
        return reply;
    }

    private static readonly string[] ActionClaims =
    [
        "on my way", "i'm coming", "i am coming", "coming!", "i'll ", "i will ", "heading ", "i'm going to", "i am going to",
        "let me get", "let me grab", "let me go", "let me find", "i'm getting", "i am getting", "i'm following", "i am following", "i'm on it", "right away", "be right there",
        // Saying it remembered without the remember tool remembers nothing (seen 3 in a row on 2026-10-11:
        // "I remember, Aubri loves pink axolotls." and no tool call).
        "i remember,", "i remember that", "i've remembered", "i have remembered", "i'll remember", "i will remember", "i'll keep that in mind", " noted",
    ];

    /// <summary>Whether a reply says Vertex is doing (or about to do) something in the game.</summary>
    public static bool ClaimsAction(string reply)
    {
        var r = " " + reply.ToLowerInvariant().Replace('’', '\'') + " ";
        return ActionClaims.Any(r.Contains);
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

    /// <summary>What Vertex knows about a player, for the prompt.</summary>
    private static string About(PlayerMemory p)
    {
        var parts = new List<string> { $"friends since {p.FirstMet:MMM d}, {p.Visits} {(p.Visits == 1 ? "visit" : "visits")}" };
        if (p.Facts.Count > 0) parts.Add(string.Join("; ", p.Facts.TakeLast(10)));
        if (p.History.Count > 0) parts.Add("recently together: " + string.Join("; ", p.History.TakeLast(3)));
        return string.Join(". ", parts) + ".";
    }

    /// <summary>Remembers a fact: about a player when one is named (kept with them), else about the world.</summary>
    public string Remember(string fact, string? about = null)
    {
        if (string.IsNullOrWhiteSpace(fact)) return "nothing to remember";
        // The model often names the player in the fact and leaves "about" empty ("Aubri loves pink axolotls",
        // about ""): a fact naming exactly one player we know is about them.
        var person = (string.IsNullOrWhiteSpace(about) ? null : Memory.Person(about)) ?? Memory.PersonNamedIn(fact);
        fact = fact.Trim();
        if (person != null)
        {
            Memory.AddPersonFact(person, fact);
            return $"remembered about {person.Name}";
        }
        Memory.AddFact(fact);
        return "remembered";
    }

    public string SetGoal(string goal, string? forPlayer)
    {
        if (string.IsNullOrWhiteSpace(goal)) return "no goal given";
        // Kept under the player's real name (the model may write "aubri" or "Aubrey").
        var g = Memory.AddGoal(goal, forPlayer != null && (Memory.Person(forPlayer) ?? Memory.PersonNamedIn(forPlayer)) is { } p ? p.Name : forPlayer, Now());
        return $"OK: new long-term goal \"{g.Text}\". You will work on it whenever nobody needs you.";
    }

    public string FinishGoal(string? goal)
    {
        if (Memory.FindGoal(goal) is not { } g)
            return Memory.ActiveGoals.Count == 0 ? "you have no goals" : "no goal like that; goals: " + string.Join("; ", Memory.ActiveGoals.Select(x => x.Text));
        Memory.FinishGoal(g, Now());
        if (g.For != null && Memory.Person(g.For) is { } person) Memory.AddHistory(person, $"finished the goal: {g.Text}", Now());
        return $"OK: goal \"{g.Text}\" is finished.";
    }

    public async ValueTask DisposeAsync()
    {
        Client.ChatReceived -= OnChat;
        Client.PlayerAdded -= OnPlayerAdded;
        Tasks.Stop();
        Reflexes.Dispose();
        _events.Writer.TryComplete();
        _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch (OperationCanceledException) { } }
        if (_idleLoop != null) { try { await _idleLoop; } catch (OperationCanceledException) { } }
        Memory.Save(); // last-seen times
        Ears?.Dispose();
        await Mouth.DisposeAsync();
    }
}
