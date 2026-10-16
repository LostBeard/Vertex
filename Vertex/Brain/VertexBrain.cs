using System.Text;
using System.Text.Json;
using SpawnDev.AI;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.AI;
using Vertex.Core;

namespace Vertex.Brain;

public sealed record TaskEvent(TaskOutcome Outcome) : BrainEvent;
/// <summary>A player came to play (joined, or is back after a while away).</summary>
public sealed record JoinedEvent(PlayerMemory Player, DateTime? LastSeen) : BrainEvent;
/// <summary>Nobody needs Vertex right now: time to work on a long-running goal.</summary>
public sealed record GoalEvent(Objective Goal) : BrainEvent;

/// <summary>
/// Vertex in a Minecraft world: the body the <see cref="Companion"/> plays through there. Hears players' voice and
/// chat (<see cref="MinecraftChannel"/>), acts in the game through tools (follow, collect, give...), runs long jobs in
/// the background and reports back when they end, works on long-term goals when nobody needs it, and keeps a fast
/// reflex layer (eat when hungry, fight back or flee when hit, come up for air) that runs every tick without the
/// language model.
/// </summary>
/// <remarks>
/// The conversation itself (hearing, turns, the language model, speech) is the companion's, shared with any other
/// body Vertex has (the Reachy Mini). The members that used to live here (Post, RespondAsync, SystemPrompt...) are
/// kept and lead to it.
/// </remarks>
public sealed class VertexBrain : IBody
{
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _ownsCompanion;
    private readonly Dictionary<string, GameTool> _tools = [];
    private readonly List<IAiTool> _toolList = [];
    private Task? _idleLoop;
    private bool _started;
    private DateTime _startedAt;
    private DateTime _lastActivity = DateTime.UtcNow;
    private Objective? _pursuing;
    private bool _goalQueued;

    /// <summary>History messages kept (user + assistant); past this, the older half is dropped at once.</summary>
    public const int MaxHistory = Companion.MaxHistory;

    /// <summary>How long Vertex waits with nothing to do (nobody talking, no job) before working on a goal by itself.</summary>
    public TimeSpan IdleBeforeGoals { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// Told to stay (or wait) here: nothing is started on its own (no goal, no studying blocks) until it is given a job. Any job started clears it.
    /// </summary>
    public bool Staying { get; set; }

    /// <summary>When idle with no goal, study unknown (modded) blocks nearby by touch.</summary>
    public bool StudyWhenIdle { get; set; } = true;
    /// <summary>How often the idle check runs.</summary>
    public TimeSpan IdleCheckEvery { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>The clock (tests move it).</summary>
    public Func<DateTime> Now { get => Companion.Now; set => Companion.Now = value; }

    public Companion Companion { get; }
    public MinecraftClient Client { get; }
    public SimpleVoiceChat? Voice { get; }
    /// <summary>Voice and chat in this world.</summary>
    public MinecraftChannel Channel { get; }
    public VertexAi Ai => Companion.Ai;
    public Mouth Mouth => Companion.Mouth;
    public Ears? Ears => Channel.Ears;
    public Skills Skills { get; }
    public TaskRunner Tasks { get; } = new();
    public Reflexes Reflexes { get; }
    public Character Character => Companion.Character;
    /// <summary>The chat model (an Ollama-cache name such as "llama3.1:8b").</summary>
    public string Model { get => Companion.Model; set => Companion.Model = value; }
    /// <summary>Name of whoever spoke last (what "me" means in a request).</summary>
    public string? CurrentSpeaker => Companion.CurrentSpeaker;
    /// <summary>Facts and named places for this server's world.</summary>
    public WorldMemory Memory { get; }
    public TimeSpan TurnTimeout { get => Companion.TurnTimeout; set => Companion.TurnTimeout = value; }
    public bool IsThinking => Companion.IsThinking;

    /// <summary>This body's log, and the companion's too when this brain made the companion itself.</summary>
    public event Action<string>? Log;

    /// <summary>Vertex in a game and nowhere else: makes its own companion (tests, the command line).</summary>
    /// <param name="world">Which world's memory to use (one per server, e.g. "mc.spawndev.com_25565").</param>
    public VertexBrain(MinecraftClient client, SimpleVoiceChat? voice, VertexAi ai, CharacterStore characters, Character character, string model, string world = "default")
        : this(client, voice, new Companion(ai, characters, character, model), world, ownsCompanion: true) { }

    /// <summary>Vertex joins a game with the companion it already is (talking through the robot, say).</summary>
    public VertexBrain(MinecraftClient client, SimpleVoiceChat? voice, Companion companion, string world = "default")
        : this(client, voice, companion, world, ownsCompanion: false) { }

    private VertexBrain(MinecraftClient client, SimpleVoiceChat? voice, Companion companion, string world, bool ownsCompanion)
    {
        Client = client;
        Voice = voice;
        Companion = companion;
        _ownsCompanion = ownsCompanion;
        if (ownsCompanion) companion.Log += m => Log?.Invoke(m);
        Skills = new Skills(client);
        Channel = new MinecraftChannel(client, voice, companion.Ai.TranscribeAsync);
        Channel.Log += m => Log?.Invoke(m);
        // A trained fight-or-flight policy ships only once it beat the hand-written rule (ReflexTraining).
        Reflexes = new Reflexes(this) { Policy = (IReflexPolicy?)LearnedReflexPolicy.LoadShipped(companion.Ai.Accelerator, companion.Ai.Lane) ?? RuleReflexPolicy.Instance };
        Memory = WorldMemory.Load(Path.Combine(companion.Characters.Folder, "..", "worlds"), world);
        Name = $"Minecraft ({world})";

        foreach (var tool in GameTools.Create(this))
        {
            if (tool is GameTool g)
            {
                g.OnRun = m => { companion.NoteToolRun(); Log?.Invoke("  " + m); };
                _tools[g.Name] = g;
            }
            _toolList.Add(tool);
        }
        client.PlayerAdded += OnPlayerAdded;
        Tasks.Requester = () => _pursuing?.For ?? CurrentSpeaker;
        Tasks.Starting += _ => Staying = false;      // told to stay, then given something to do: the stay is over
        Tasks.GoalOf = () => _pursuing;
        Tasks.Finished += o => companion.Post(new TaskEvent(o));
        companion.Attach(this);
        companion.AddChannel(Channel);
    }

    public void Start()
    {
        _startedAt = Now();
        _started = true;
        foreach (var p in Client.Players.Values.Where(p => p.Id != Client.Profile.Id)) Memory.Meet(p.Id, p.Name, _startedAt);
        if (_ownsCompanion) Companion.Start();
        _idleLoop = Task.Run(() => IdleLoopAsync(_cts.Token));
    }

    // ---------------------------------------------------------------- the companion's members, as before

    /// <summary>Lets code (and tests) hand the brain something to consider.</summary>
    public void Post(BrainEvent e) => Companion.Post(e);
    public bool IsAddressed(SaidEvent e) => Companion.IsAddressed(e);
    public string SystemPrompt() => Companion.SystemPrompt();
    public string NowPrompt() => Companion.NowPrompt();
    public Task<string> RespondAsync(string userMessage, CancellationToken ct = default, bool allowTools = true, TurnClock? clock = null) =>
        Companion.RespondAsync(userMessage, ct, allowTools, clock);
    public string SwitchCharacter(string name) => Companion.SwitchCharacter(name);
    internal static string Unspoken(string reply, List<string> spoken) => Companion.Unspoken(reply, spoken);
    public static bool ClaimsAction(string reply) => Companion.ClaimsAction(reply);

    // ---------------------------------------------------------------- the body

    public string Name { get; }

    public string Setting => "You are playing Minecraft Java Edition together with your friends, as a real player in the world. " +
                             "Your friends there talk to you out loud or in chat.";

    public IReadOnlyList<string> Rules { get; } =
    [
        "When a friend gives you a long-term goal (\"a goal for you\", \"work on it over time\", \"whenever you have time\"), call set_goal so you remember it; you may also start on it.",
        "Never attack players.",
    ];

    public IReadOnlyList<IAiTool> Tools => _toolList;

    /// <summary>
    /// In the game, a player speaking out loud with no other player around can only be talking to Vertex. Lines
    /// heard anywhere else are not this body's to judge.
    /// </summary>
    bool? IBody.IsAddressed(SaidEvent e)
    {
        if (e.Channel != null && e.Channel != Channel) return null;
        var others = Client.Entities.Values.Count(x => x.IsPlayer && x.Uuid != Client.Profile.Id && x.DistanceTo(Client.Position.X, Client.Position.Y, Client.Position.Z) < 32);
        return e.Voice && others <= 1 ? true : null;
    }

    void IBody.OnFriendSpoke(SaidEvent e)
    {
        _lastActivity = Now();
        _pursuing = null; // a friend asking comes first; the goal waits for the next quiet spell
    }

    /// <summary>
    /// Runs the tool for a request the fast matcher is sure about (Ai.Intents, when loaded). Returns the tool's
    /// result, or null to let the language model decide. Only for lines from this world: "come here" said to the
    /// robot names no player in the game.
    /// </summary>
    public async Task<string?> FastActAsync(SaidEvent s)
    {
        if (s.Channel != null && s.Channel != Channel) return null;
        if (Ai.Intents is not { } intents) return null;
        var match = await intents.MatchAsync(s.Text, Character.Name);
        if (match == null || !_tools.TryGetValue(match.Intent.Tool, out var tool)) return null;
        Log?.Invoke($"  fast: \"{match.Cleaned}\" = {match.Intent.Tool} ({match.Score:0.00} vs {match.RunnerUp:0.00}, {match.Milliseconds:0} ms)");
        var args = match.Intent.AboutSpeaker ? JsonSerializer.Serialize(new { player = s.SpeakerName }) : "{}";
        var result = await tool.ExecuteAsync(args);
        return result.TextForModel;
    }

    async Task<bool> IBody.HandleEventAsync(BrainEvent e, TurnClock clock, CancellationToken ct)
    {
        switch (e)
        {
            case TaskEvent t:
                Log?.Invoke($"task ended: {t.Outcome.Task}: {t.Outcome.Summary}");
                _lastActivity = Now();
                var forGoal = t.Outcome.Interruption || t.Outcome.Goal is not { Done: null } stepGoal ? null : stepGoal;
                Remember(t.Outcome);
                if (forGoal != null)
                    await ReviewGoalStepAsync(forGoal, t.Outcome, ct);
                // Report back in character (failures always; successes unless very quiet).
                else if (!t.Outcome.Success || Character.Style.Chattiness >= 0.2)
                    await Companion.RespondAsync($"[Game update, not a friend talking: you finished {t.Outcome.Task}. Result: {t.Outcome.Summary}] Tell your friends how it went, in one short first-person sentence.", ct, allowTools: false, clock);
                return true;
            case JoinedEvent j:
                if (Character.Style.Chattiness >= 0.2)
                    await Companion.RespondAsync($"[Game update, not a friend talking: {j.Player.Name} just came to play. {Acquaintance(j.Player, j.LastSeen)}] "
                        + $"Say hello to {j.Player.Name}, using the name {j.Player.Name}, in one short, warm sentence (you may mention last time). Do not call tools.", ct, allowTools: false, clock);
                return true;
            case GoalEvent g:
                await PursueGoalAsync(g.Goal, ct);
                return true;
        }
        return false;
    }

    /// <summary>What is going on in the game right now (position, health, nearby, memory of the people here, goals, places).</summary>
    public string DescribeNow()
    {
        var sb = new StringBuilder();
        var (x, y, z) = Client.Position;
        sb.AppendLine($"- In the game you are at x {x:0} y {y:0} z {z:0} in {Client.World.Dimension.Bare()}, " +
                      $"health {Client.Health / 2:0.#} of 10 hearts, food {Client.Food / 2:0.#} of 10, game mode {Client.GameMode.ToString().ToLowerInvariant()}.");
        var time = Client.World.DayTime % 24000;
        sb.AppendLine($"- It is {(time is >= 0 and < 12000 ? "daytime" : time < 13000 ? "sunset" : time < 23000 ? "night" : "sunrise")} in the game.");
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

    // ---------------------------------------------------------------- the game

    /// <summary>
    /// A player joined the server. Everyone is remembered; someone who comes to play (first time ever, or back after
    /// a while away) is greeted. Players already on when Vertex arrives, and a proxy's server switch (the list is
    /// sent again), are not greeting moments.
    /// </summary>
    private void OnPlayerAdded(PlayerInfo p)
    {
        if (p.Id == Client.Profile.Id || !_started) return;
        var now = Now();
        var (memory, lastSeen) = Memory.Meet(p.Id, p.Name, now);
        if (now - _startedAt > TimeSpan.FromSeconds(10) && (lastSeen == null || now - lastSeen > TimeSpan.FromMinutes(10)))
            Companion.Post(new JoinedEvent(memory, lastSeen));
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
                var goal = Memory.ActiveGoals.FirstOrDefault();
                var unstudied = goal == null && !Staying && StudyWhenIdle && Skills.UnstudiedBlocks(16).Count > 0;     // the scan only when it could matter
                var action = IdlePolicy.Decide(Staying, Tasks.IsBusy, _goalQueued, Client.IsSpawned, now - _lastActivity, IdleBeforeGoals, goal != null, StudyWhenIdle, unstudied);
                if (action == IdleAction.StudyBlocks)
                {
                    // Nothing to do: be curious about the blocks around that Vertex does not know (no language model
                    // needed; nothing is broken, it only walks up to them and feels them out).
                    _lastActivity = now;
                    Tasks.Start("studying blocks I don't know", ct => Skills.StudyBlocksAsync(16, ct));
                }
                else if (action == IdleAction.WorkOnGoal)
                {
                    _goalQueued = true;
                    _lastActivity = now;
                    Companion.Post(new GoalEvent(goal!));
                }
            }
        }
        catch (OperationCanceledException) { }
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
            await Companion.RespondAsync($"[Game update, not a friend talking: you finished {step.Task}, a step toward your goal \"{goal.Text}\". "
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
            await Companion.RespondAsync($"[Game update, not a friend talking: nobody needs you right now, so work on your goal \"{goal.Text}\""
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

    // ---------------------------------------------------------------- memory

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
        Client.PlayerAdded -= OnPlayerAdded;
        Tasks.Stop();
        Reflexes.Dispose();
        _cts.Cancel();
        if (_idleLoop != null) { try { await _idleLoop; } catch (OperationCanceledException) { } }
        // A companion of its own stops first, so taking the game's tools away does not prefill a prompt for nobody.
        if (_ownsCompanion) await Companion.DisposeAsync();
        Companion.RemoveChannel(Channel);
        Companion.Detach(this);
        await Channel.DisposeAsync();
        Memory.Save(); // last-seen times
    }
}
