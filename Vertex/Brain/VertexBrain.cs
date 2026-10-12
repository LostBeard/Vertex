using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SpawnDev.AI;
using SpawnDev.AI.Server;
using SpawnDev.ILGPU.ML.Preprocessing;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.AI;

namespace Vertex.Brain;

/// <summary>Something the brain should consider: speech, chat, or a finished task.</summary>
public abstract record BrainEvent;
/// <param name="HeardTick">When the speaker stopped (<see cref="Environment.TickCount64"/>; 0 = when it arrived).</param>
/// <param name="SttMs">Speech to text time for a spoken line.</param>
public sealed record SaidEvent(Guid Speaker, string SpeakerName, string Text, bool Voice, long HeardTick = 0, double SttMs = 0) : BrainEvent;
public sealed record TaskEvent(TaskOutcome Outcome) : BrainEvent;
/// <summary>A player came to play (joined, or is back after a while away).</summary>
public sealed record JoinedEvent(PlayerMemory Player, DateTime? LastSeen) : BrainEvent;
/// <summary>Nobody needs Vertex right now: time to work on a long-running goal.</summary>
public sealed record GoalEvent(Objective Goal) : BrainEvent;
/// <summary>Prefill the system prompt and tool list before anyone talks (at start, after a character switch).</summary>
public sealed record PrimeEvent : BrainEvent;

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
    /// <summary>History messages kept (user + assistant); past this, the older half is dropped at once.</summary>
    public const int MaxHistory = 24;
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
            Ears.Heard += h => Hear(new SaidEvent(h.Speaker, NameOf(h.Speaker), h.Text, true, h.EndedTick, h.SttMs));
        }
        // A trained fight-or-flight policy ships only once it beat the hand-written rule (ReflexTraining).
        Reflexes = new Reflexes(this) { Policy = (IReflexPolicy?)LearnedReflexPolicy.LoadShipped(ai.Accelerator, ai.Lane) ?? RuleReflexPolicy.Instance };
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
        _events.Writer.TryWrite(new PrimeEvent());
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
        Hear(new SaidEvent(m.SenderId.Value, m.SenderName ?? NameOf(m.SenderId.Value), m.Content, false, Environment.TickCount64));
    }

    /// <summary>Lets code (and tests) hand the brain something to consider.</summary>
    public void Post(BrainEvent e)
    {
        if (e is SaidEvent s) Hear(s);
        else _events.Writer.TryWrite(e);
    }

    /// <summary>
    /// Something was said. Hearing never waits for thinking: a turn already running is interrupted when this is
    /// addressed to Vertex and either the running turn is not a friend's (a game update, a goal step) or Vertex is
    /// called by name or told to stop. Anything else waits its turn (and a speaker's waiting lines are answered
    /// together). Before 2026-10-12 one slow turn left every later line queued behind it ("not hearing anything").
    /// </summary>
    private void Hear(SaidEvent e)
    {
        if (Volatile.Read(ref _turn) is { } turn && IsAddressed(e) && (!turn.ForPerson || Interrupts(e.Text)))
        {
            Log?.Invoke($"  (interrupted by {e.SpeakerName})");
            turn.Interrupted = true;
            Mouth.Hush();
            try { turn.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        _events.Writer.TryWrite(e);
    }

    /// <summary>Called by name, or told to stop or wait: worth cutting a friend's turn short for.</summary>
    private bool Interrupts(string text)
    {
        var t = " " + text.ToLowerInvariant() + " ";
        return t.Contains(Character.Name.ToLowerInvariant()) || t.Contains("vertex")
            || t.Contains(" stop") || t.Contains(" wait") || t.Contains(" never mind") || t.Contains(" nevermind");
    }

    /// <summary>The turn being thought or spoken right now (null when Vertex is idle).</summary>
    private sealed class Turn(CancellationTokenSource cts, bool forPerson)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public bool ForPerson { get; } = forPerson;
        public volatile bool Interrupted;
    }
    private Turn? _turn;

    /// <summary>True while a turn is being thought about (or its reply generated).</summary>
    public bool IsThinking => Volatile.Read(ref _turn) != null;

    /// <summary>The longest a turn may think before it gives up and says so (a stalled generation must not deafen Vertex).</summary>
    public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromSeconds(60);

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
        await foreach (var first in _events.Reader.ReadAllAsync(ct))
        {
            var e = first;
            // A friend's lines that arrived while Vertex was busy are one turn: answer them together, once.
            if (e is SaidEvent said && IsAddressed(said))
                while (_events.Reader.TryPeek(out var next) && next is SaidEvent more && more.Speaker == said.Speaker && IsAddressed(more)
                       && _events.Reader.TryRead(out _))
                    e = said = said with { Text = said.Text + " " + more.Text, HeardTick = more.HeardTick, SttMs = more.SttMs };

            var forPerson = e is SaidEvent;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TurnTimeout);
            var turn = new Turn(cts, forPerson);
            var clock = e is SaidEvent heard ? new TurnClock(heard.HeardTick, heard.SttMs) : new TurnClock();
            Volatile.Write(ref _turn, turn);
            try
            {
                await HandleAsync(e, clock, cts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException)
            {
                // Interrupted (someone spoke) or out of time: either way the next thing in line gets a turn now.
                if (!turn.Interrupted)
                {
                    Log?.Invoke($"  (gave up thinking after {TurnTimeout.TotalSeconds:0} s)");
                    if (forPerson) Mouth.Say("Sorry, I lost my train of thought. Can you say that again?");
                }
                if (clock.TakeReport()) Log?.Invoke("timing: " + clock.Describe(turn.Interrupted ? "interrupted" : "timed out"));
            }
            catch (Exception ex)
            {
                Log?.Invoke("brain error: " + ex.Message);
            }
            finally
            {
                Interlocked.CompareExchange(ref _turn, null, turn);
            }
        }
    }

    private async Task HandleAsync(BrainEvent e, TurnClock clock, CancellationToken ct)
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
                    await RespondAsync($"{said}\n[You already did it: {done}] Answer in one short sentence; do not call tools.", ct, allowTools: false, clock);
                else
                    await RespondAsync(said, ct, clock: clock);
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
                    await RespondAsync($"[Game update, not a friend talking: you finished {t.Outcome.Task}. Result: {t.Outcome.Summary}] Tell your friends how it went, in one short first-person sentence.", ct, allowTools: false, clock);
                break;
            case JoinedEvent j when Character.Style.Chattiness >= 0.2:
                await RespondAsync($"[Game update, not a friend talking: {j.Player.Name} just came to play. {Acquaintance(j.Player, j.LastSeen)}] "
                    + $"Say hello to {j.Player.Name}, using the name {j.Player.Name}, in one short, warm sentence (you may mention last time). Do not call tools.", ct, allowTools: false, clock);
                break;
            case PrimeEvent:
                await PrimeAsync(ct);
                break;
            case GoalEvent g:
                await PursueGoalAsync(g.Goal, ct);
                break;
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
        sb.AppendLine("- A friend saying hello, thanks or something friendly gets a friendly answer, not a job: only call a tool when you are asked to do something.");
        sb.AppendLine("- Before each message you get a note about what is going on in the game. It is for you to know; never read it out or repeat its words.");
        var others = _characters.All().Where(c => c.Name != Character.Name).Select(c => c.Name);
        sb.AppendLine($"- Other characters you can switch to: {string.Join(", ", others)}.");
        return sb.ToString();
    }

    /// <summary>
    /// What is going on right now (position, health, nearby, memory of the people here, goals, places). A note of its own
    /// just before the newest message, NOT in the system prompt: the system prompt plus the tool list is the long, unchanging
    /// start of every prompt, and the language model reuses it from the last turn only while it stays byte-identical.
    /// With the game state inside it, every turn paid for the whole tool list again.
    /// </summary>
    public string NowPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("What is going on in the game (background for you, not something to say):");
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
        return sb.ToString();
    }

    /// <summary>
    /// One turn: system prompt + recent history + this message (with what is going on right now), tools on. The reply
    /// is spoken sentence by sentence AS IT IS GENERATED, so Vertex starts talking while it is still thinking.
    /// </summary>
    public async Task<string> RespondAsync(string userMessage, CancellationToken ct = default, bool allowTools = true, TurnClock? clock = null)
    {
        clock ??= new TurnClock();
        var messages = new List<AiChatMessage> { new("system", SystemPrompt()) };
        lock (_history) messages.AddRange(_history);
        // The game state is its own note just before the message. Glued to the front of the message, the model began
        // its replies with the note's label ("Right now: I'm on my way to Aubri", BrainTests 2026-10-12).
        messages.Add(new AiChatMessage("system", NowPrompt()));
        messages.Add(new AiChatMessage("user", userMessage));
        Log?.Invoke("> " + userMessage);
        var runsBefore = Volatile.Read(ref _toolRuns);
        var spoken = new List<string>();
        var reply = await ChatOnceAsync(messages, allowTools, ct, clock, spoken);
        // An 8B model sometimes SAYS it is doing something ("I'm on my way") without calling a tool, and then nothing
        // happens in the game (seen 2026-10-11 in BrainTests.ComeHere). Once, tell it so and let it call the tool.
        if (allowTools && Volatile.Read(ref _toolRuns) == runsBefore && ClaimsAction(reply))
        {
            Log?.Invoke("  (said it would act but called no tool: asking again)");
            messages.Add(new AiChatMessage("assistant", reply));
            messages.Add(new AiChatMessage("user", "[You said that, but you did not call any tool, so NOTHING happened in the game. Call the right tool now, then answer in one short sentence.]"));
            // What it already said was heard; the second answer is only spoken when nothing was.
            reply = await ChatOnceAsync(messages, true, ct, clock, spoken.Count > 0 ? null : spoken);
        }
        // Nothing said and nothing done is no answer at all (seen 2026-10-11 for "remember that I love pink
        // axolotls"): once, tell it so.
        else if (allowTools && Volatile.Read(ref _toolRuns) == runsBefore && reply.Count(char.IsLetter) < 4)
        {
            Log?.Invoke("  (no answer and no tool: asking again)");
            messages.Add(new AiChatMessage("assistant", reply));
            messages.Add(new AiChatMessage("user", "[You gave no answer and called no tool. If you were asked to do something, call the right tool now; then answer in one short sentence.]"));
            reply = await ChatOnceAsync(messages, true, ct, clock, spoken.Count > 0 ? null : spoken);
        }
        lock (_history)
        {
            _history.Add(new AiChatMessage("user", userMessage));
            _history.Add(new AiChatMessage("assistant", reply.Length > 0 ? reply : "(did it)"));
            // Old exchanges leave in CHUNKS, not one per turn: the language model reuses the start of the prompt from
            // the last turn only while it is unchanged, and a window that slid every turn made it prefill the whole
            // history again each time (MEASURED 2026-10-12, AiSessionTests: reused 525 of 543 tokens, first word
            // 0.68 s, while the history grew; 26 of 569 and 2.4 s once it slid).
            if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory / 2);
        }
        // The end of the reply that was not a whole sentence while streaming (or all of it, when nothing streamed).
        var rest = Unspoken(reply, spoken);
        if (rest.Count(char.IsLetter) >= 4 && !ChatTemplates.IsOnlyPythonicCalls(rest))
            SayInTurn(rest, clock);
        else if (spoken.Count == 0 && clock.TakeReport())
            Log?.Invoke("timing: " + clock.Describe(reply.Length > 0 ? "answered without speaking" : "acted without speaking"));
        return reply;
    }

    /// <summary>Speaks a line for the current turn; the turn's timing is logged when it is first heard.</summary>
    private void SayInTurn(string line, TurnClock clock)
    {
        clock.FirstSentence();
        Mouth.Say(line, () =>
        {
            if (clock.FirstSound() && clock.TakeReport()) Log?.Invoke("timing: " + clock.Describe());
        });
    }

    /// <summary>What is left of <paramref name="reply"/> once the sentences already spoken are taken off its front.</summary>
    internal static string Unspoken(string reply, List<string> spoken)
    {
        var rest = reply;
        foreach (var s in spoken)
        {
            var at = rest.IndexOf(s, StringComparison.Ordinal);
            if (at < 0) return spoken.Count == 0 ? reply : ""; // the final text differs from the stream: say nothing twice
            rest = rest[(at + s.Length)..];
        }
        return rest.Trim();
    }

    /// <summary>
    /// Prefills the system prompt and the tool list - the long, fixed start of every prompt - so the first thing anybody
    /// says only costs its own words. MEASURED 2026-10-12 (BrainTests): the first turn after a start prefilled 2,951 tokens
    /// and took 16.95 s to its first word; with the prefix already cached, 2,847 tokens were reused and it took 1.2 s.
    /// One throwaway output token, never spoken.
    /// </summary>
    private async Task PrimeAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Ai.ChatAsync(new AiChatRequest
        {
            Model = Model,
            Messages = [new("system", SystemPrompt()), new("user", "(getting ready)")],
            Options = new AiGenerationOptions { MaxOutputTokens = 1, Strategy = "greedy" },
        }, null, ct);
        Log?.Invoke($"ready: prompt and tools prefilled in {sw.Elapsed.TotalSeconds:0.0} s");
    }

    /// <param name="spoken">When given, whole sentences are spoken as they stream in and added here.</param>
    private async Task<string> ChatOnceAsync(List<AiChatMessage> messages, bool allowTools, CancellationToken ct, TurnClock clock, List<string>? spoken)
    {
        var tools = Ai.Chat.Tools;
        if (!allowTools) Ai.Chat.Tools = null;
        var pending = new StringBuilder();
        Task OnDelta(string delta)
        {
            clock.FirstToken();
            if (spoken == null) return Task.CompletedTask;
            pending.Append(delta);
            // A sentence is whole once its end mark is followed by a space; speak it while the rest is generated.
            var text = pending.ToString();
            var cut = -1;
            for (var i = 0; i + 1 < text.Length; i++)
                if (text[i] is '.' or '!' or '?' && char.IsWhiteSpace(text[i + 1])) cut = i + 1;
            if (cut > 0)
            {
                var whole = text[..cut].Trim();
                pending.Remove(0, cut);
                foreach (var sentence in Mouth.Sentences(whole))
                {
                    // Llama writes some tool calls as text ("come_here(player='Aubri')"): never speak one.
                    if (sentence.Count(char.IsLetter) < 2 || ChatTemplates.IsOnlyPythonicCalls(sentence)) continue;
                    spoken.Add(sentence);
                    SayInTurn(sentence, clock);
                }
            }
            return Task.CompletedTask;
        }
        void OnPass(string line)
        {
            if (line.Contains("prompt=", StringComparison.Ordinal)) lock (clock.Passes) clock.Passes.Add(line);
        }
        AiChatResult result;
        clock.Thinking();
        var perf = Ai.Chat.PerfLog;
        Ai.Chat.PerfLog = OnPass;
        try
        {
            result = await Ai.ChatAsync(new AiChatRequest
            {
                Model = Model,
                Messages = messages,
                Options = new AiGenerationOptions { MaxOutputTokens = 160, Strategy = "top_p", Temperature = 0.7f, TopP = 0.9f, RepetitionPenalty = 1.1f },
            }, OnDelta, ct);
        }
        finally
        {
            Ai.Chat.Tools = tools;
            Ai.Chat.PerfLog = perf;
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
        _events.Writer.TryWrite(new PrimeEvent()); // a new persona is a new system prompt: prefill it again
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
