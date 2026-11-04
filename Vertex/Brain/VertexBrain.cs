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
/// <summary>Nobody has needed Vertex for a long while and it has no goal: it may choose a small project of its own (self-directed play).</summary>
public sealed record OwnProjectEvent : BrainEvent;
/// <summary>He got somewhere: a step in getting on in the world (a stone pickaxe, a bed).</summary>
public sealed record MilestoneEvent(string Label) : BrainEvent;
/// <summary>He died; the text says how, from the server's own account.</summary>
public sealed record DeathEvent(string Text) : BrainEvent;

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

    /// <summary>
    /// Offer the language model the build_script tool (it writes a build plan itself). Off: an 8B model garbled long plans and every extra tool makes the choice of tool harder, so it is
    /// offered shapes (build_shape) only. For a bigger model.
    /// </summary>
    public bool AdvancedBuildTools { get; set; }

    /// <summary>When idle with no goal, study unknown (modded) blocks nearby by touch.</summary>
    public bool StudyWhenIdle { get; set; } = true;

    /// <summary>
    /// Self-directed play: with no goal and nobody needing it for <see cref="SelfDirectedIdle"/>, Vertex chooses ONE small project of its own (build something little, explore a direction, gather,
    /// fish, farm) and does it with the same skills and rules as any request. Off by default: letting Vertex play on its own is the household's choice. Only in survival, by day, healthy and fed;
    /// anyone talking to it, any job running, "stay" or a goal it already has outrank it, and it waits <see cref="ProjectCooldown"/> between projects and does not repeat its last few.
    /// </summary>
    public bool SelfDirectedPlay { get; set; }
    /// <summary>How long nothing may have needed Vertex before it chooses a project of its own.</summary>
    public TimeSpan SelfDirectedIdle { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>The wait after choosing a project before it may choose another.</summary>
    public TimeSpan ProjectCooldown { get; set; } = TimeSpan.FromMinutes(3);
    private bool _projectQueued, _choosing;
    private DateTime _lastProjectAt = DateTime.MinValue;
    private readonly List<string> _recentProjects = [];
    /// <summary>
    /// Long-term memory (null: none). Jobs that end are written to it as episodes, and what a friend says is looked up in it before the language model answers. Everything stays on this PC;
    /// the privacy rules (paused learning, people nothing may be learned about, forgetting a person) live in its store.
    /// </summary>
    public Vertex.Memory.VertexMemory? LongTerm { get; set; }
    /// <summary>Which world the memories belong to: "host:port". "Home" on one server must never answer on another.</summary>
    public string MemoryScope { get; set; } = "";
    /// <summary>
    /// How alike (cosine, 0 to 1) a memory must be to what was said to be shown to the language model. Set from measurements (2026-11-02, CPU, all-MiniLM-L6-v2, 7 memories, 8 questions about
    /// them and 8 lines of small talk): questions scored 0.351 to 0.753, small talk 0.084 to 0.240; 0.30 is the middle of the gap. A small sample: VertexMemoryTests.TheRecallCutoff...
    /// asserts it stays between the two groups, and it should be re-measured on real entries.
    /// </summary>
    public const float MinRecallSimilarity = 0.30f;
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
        MemoryFileName = world;

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
        Tasks.Starting += name => { Staying = false; if (_choosing) { lock (_recentProjects) { _recentProjects.Add(name); if (_recentProjects.Count > 5) _recentProjects.RemoveAt(0); } } };      // told to stay, then given something to do: the stay is over
        Tasks.GoalOf = () => _pursuing;
        Tasks.Finished += o => companion.Post(new TaskEvent(o));
        Client.ChatReceived += OnServerText;
        Client.Damaged += OnDamaged;
        Client.HealthChanged += OnHealthChanged;
        Client.Died += OnDied;
        Tasks.Crashed += (job, e) => Log?.Invoke($"job crashed ({job}): {e}");
        Skills.MilestoneReached += m => { Log?.Invoke("reached: " + m.Label); Companion.Post(new MilestoneEvent(m.Label)); };
        Skills.LessonLearned += RecordLesson;
        Skills.Trace += m => Log?.Invoke("  (" + m + ")");
        Skills.PriorLessons = LessonsFor;
        Reflexes.Temperature = () => Temperature;
        Skills.HotAt = () => Temperature == null ? null : Reflexes.HotAt;
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
        _ = Task.Run(() => ExperienceLoopAsync(_cts.Token));
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
    private string MemoryFileName { get; }

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

    /// <summary>The episode a finished job leaves in long-term memory: what he did (or tried), the result in his own words, and where.</summary>
    internal static string EpisodeText(TaskOutcome outcome, (double X, double Y, double Z) at)
    {
        var where = $" (at {at.X:0} {at.Y:0} {at.Z:0})";
        return outcome.Success ? $"I finished {outcome.Task}: {outcome.Summary.TrimEnd('.', ' ')}.{where}" : $"I tried {outcome.Task}, but it did not work: {outcome.Summary.TrimEnd('.', ' ')}.{where}";
    }

    /// <summary>"just now", "20 minutes ago", "3 hours ago", "2 days ago".</summary>
    internal static string Ago(TimeSpan age) => age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} minutes ago"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours} hour{((int)age.TotalHours == 1 ? "" : "s")} ago"
        : $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";

    /// <summary>
    /// Writes what he worked out by trying (a lesson) to long-term memory: kind Lesson, important (0.7: it is how he gets better at being somewhere), tagged with its situation
    /// ("exp:&lt;key&gt;"), whether it worked, and the ways tried ("worked:swim", "failed:dig"), so that "have I tried this?" and "what worked here?" are exact lookups, not guesses from similar words.
    /// </summary>
    private void RecordLesson(Skills.Lesson lesson)
    {
        Log?.Invoke($"learned ({lesson.Key}, {(lesson.Worked ? "worked" : "did not work")}): {lesson.Text}");
        lock (_sessionLessons) _sessionLessons.Add(lesson);
        lock (_triedKeys) _triedKeys.Add(lesson.Key);
        var memory = LongTerm;
        if (memory == null) return;
        var entry = new SpawnDev.AI.AiMemoryEntry
        {
            Kind = SpawnDev.AI.AiMemoryKind.Lesson, Text = lesson.Text + $" (at {Client.Position.X:0} {Client.Position.Y:0} {Client.Position.Z:0})", Scope = MemoryScope, Source = "minecraft",
            Tags = new[] { "lesson", ExperimentTag(lesson.Key), lesson.Worked ? "worked" : "failed" }.Concat(lesson.Tags ?? []).Distinct().ToList(), Importance = 0.7f,
        };
        _ = Task.Run(async () =>
        {
            try { await memory.RecordAsync(entry); }
            catch (Exception e) { Log?.Invoke("memory: could not record a lesson: " + e.Message); }
        });
    }

    private readonly List<Skills.Lesson> _sessionLessons = [];

    /// <summary>
    /// Everything he has learned about one situation: from his memory (this server, earlier runs too) and the ones written this run that the store may not have yet. What the skills ask before
    /// they try.
    /// </summary>
    internal IReadOnlyList<Skills.Lesson> LessonsFor(string key)
    {
        var tag = ExperimentTag(key);
        List<Skills.Lesson> mine; lock (_sessionLessons) mine = _sessionLessons.Where(l => l.Key == key).ToList();
        var all = new List<Skills.Lesson>(mine);
        if (LongTerm is { } memory)
            foreach (var e in memory.Store.List(new SpawnDev.AI.AiMemoryFilter { Scope = MemoryScope, Kinds = [SpawnDev.AI.AiMemoryKind.Lesson] }).Where(e => e.Tags.Contains(tag)))
                if (!mine.Any(l => e.Text.StartsWith(l.Text, StringComparison.Ordinal)))      // the store's copy of a lesson written this run has " (at x y z)" added
                    all.Add(new Skills.Lesson(e.Text, key, e.Tags.Contains("worked"), e.Tags.ToList()));
        return all;
    }

    internal static string ExperimentTag(string key) => "exp:" + key;
    private readonly HashSet<string> _triedKeys = [];

    /// <summary>Has he already learned the answer to this experiment (here, or earlier on this server: it is in his memory)? Then he does not try it again.</summary>
    internal bool AlreadyTried(string key)
    {
        lock (_triedKeys) if (_triedKeys.Contains(key)) return true;
        var memory = LongTerm;
        if (memory == null) return false;
        var tag = ExperimentTag(key);
        return memory.Store.List(new SpawnDev.AI.AiMemoryFilter { Scope = MemoryScope, Kinds = [SpawnDev.AI.AiMemoryKind.Lesson] }).Any(e => e.Tags.Contains(tag));
    }

    /// <summary>
    /// What he could try now, on purpose, for the sake of finding out: the first experiment he has no lesson for yet and whose set-up is here (a pond with a far shore). Null when there is
    /// nothing new to try. Played alone is the best time to learn (TJ, 2026-11-02): nothing is at stake and nobody is waiting on him.
    /// </summary>
    internal (string Name, Func<CancellationToken, Task<(bool, string)>> Run)? NextExperiment()
    {
        if (!AlreadyTried(Skills.SwimAcrossKey) && Skills.FindSwimAcross() is { } t)
            return ($"trying something new: swimming across {t.Width} blocks of water", Skills.SwimAcrossAsync);
        return null;
    }

    /// <summary>Writes a finished job to long-term memory in the background (a memory that cannot be written is a log line, never a problem for the game).</summary>
    private void RecordEpisode(TaskOutcome outcome)
    {
        var memory = LongTerm;
        if (memory == null) return;
        var subjects = new List<string>();
        if (outcome.For is { Length: > 0 } who && Client.Entities.Values.FirstOrDefault(e => e.IsPlayer && e.PlayerName == who) is { } person) subjects.Add("mc:" + person.Uuid);
        var entry = new SpawnDev.AI.AiMemoryEntry
        {
            Kind = SpawnDev.AI.AiMemoryKind.Episode, Text = EpisodeText(outcome, Client.Position), Scope = MemoryScope, Source = "minecraft",
            Tags = ["task", outcome.Success ? "done" : "failed"], Subjects = subjects, Importance = outcome.Success ? 0.5f : 0.35f,
        };
        _ = Task.Run(async () =>
        {
            try { await memory.RecordAsync(entry); }
            catch (Exception e) { Log?.Invoke("memory: could not record: " + e.Message); }
        });
    }

    /// <summary>
    /// The recall tool: looks in his own log for what the question is about and answers with the few best matches, each with how long ago. A tool result is what the 8B model uses most
    /// faithfully (a free-text note beside the friend's message was ignored or parroted back, measured 2026-11-02: 3 of 5 and 2 of 3 attempts).
    /// </summary>
    internal async Task<string> RecallForToolAsync(string question, CancellationToken ct = default)
    {
        var memory = LongTerm;
        if (memory == null) return "I don't keep a long-term log, so I can't look that up.";
        if (string.IsNullOrWhiteSpace(question)) return "Look up what? Tell me what to remember.";
        // laneHeld: this runs as a tool, inside the chat turn that holds the GPU lane (asking for it again would wait on itself forever).
        var hits = await memory.RecallAsync(question, MemoryScope, 3, null, ct, laneHeld: true);
        var good = hits.Where(h => h.Similarity >= MinRecallSimilarity).ToList();
        if (good.Count == 0) return "I don't remember anything about that.";
        Log?.Invoke($"  (remembered {good.Count}: {string.Join(" | ", good.Select(h => $"{h.Similarity:0.00}"))})");
        return "OK, this is what my log says (things I did):\n"
            + string.Join("\n", good.Select(h => $"- {Ago(DateTime.UtcNow - new DateTime(h.Entry.TimeTicks, DateTimeKind.Utc))}: {h.Entry.Text}"))
            + "\nNow tell my friend what it says, in one short sentence, as I. Add nothing the log does not say.";
    }

    async Task<bool> IBody.HandleEventAsync(BrainEvent e, TurnClock clock, CancellationToken ct)
    {
        switch (e)
        {
            case TaskEvent t:
                Log?.Invoke($"task ended: {t.Outcome.Task}: {t.Outcome.Summary}");
                if (t.Outcome.Quiet) return true; // a reflex's small job: not news, not remembered, and it does not count as having been busy
                _lastActivity = Now();
                var forGoal = t.Outcome.Interruption || t.Outcome.Goal is not { Done: null } stepGoal ? null : stepGoal;
                Remember(t.Outcome);
                if (!t.Outcome.Interruption) RecordEpisode(t.Outcome);
                if (forGoal != null)
                    await ReviewGoalStepAsync(forGoal, t.Outcome, ct);
                // Report back in character (failures always; successes unless very quiet).
                else if (!t.Outcome.Success || Character.Style.Chattiness >= 0.2)
                    await Companion.RespondAsync($"[Game update, not a friend talking: {OutcomeReport.ForModel(t.Outcome, null)}] Tell your friends how it went, in one short first-person sentence.", ct, allowTools: false, clock);
                return true;
            case JoinedEvent j:
                if (Character.Style.Chattiness >= 0.2)
                    await Companion.RespondAsync($"[Game update, not a friend talking: {j.Player.Name} just came to play. {Acquaintance(j.Player, j.LastSeen)}] "
                        + $"Say hello to {j.Player.Name}, using the name {j.Player.Name}, in one short, warm sentence (you may mention last time). Do not call tools.", ct, allowTools: false, clock);
                return true;
            case MilestoneEvent m:
                if (Character.Style.Chattiness >= 0.2)
                    await Companion.RespondAsync($"[Game update, not a friend talking: you just got {m.Label}. You have worked your way up from nothing.] Tell your friends in one short, pleased first-person sentence. Do not call tools.", ct, allowTools: false, clock);
                return true;
            case DeathEvent d:
                if (Character.Style.Chattiness >= 0.2)
                    await Companion.RespondAsync($"[Game update, not a friend talking: you just died. {d.Text} Say only what this says.] Tell your friends what happened in one short first-person sentence. Do not call tools.", ct, allowTools: false, clock);
                return true;
            case GoalEvent g:
                await PursueGoalAsync(g.Goal, ct);
                return true;
            case OwnProjectEvent:
                await ChooseOwnProjectAsync(ct);
                return true;
        }
        return false;
    }

    /// <summary>
    /// The conditions for choosing a project of its own that are not about how long it has been idle: survival (the skills' rules are survival's: breaking a block in creative drops nothing),
    /// daytime, at least 7 hearts and 6 of 10 food (the reflexes eat and heal first), and the cooldown since the last project.
    /// </summary>
    private bool FitForOwnProject(DateTime now) => ProjectBlocker(now) == null;

    /// <summary>
    /// What stops him choosing a project of his own right now, in words, or null when nothing does. He sat idle for minutes on 2026-11-03 at 11:43 in the morning with no way to tell from outside
    /// which of these it was: this answers it, and the idle loop says it in the log.
    /// </summary>
    internal string? ProjectBlocker(DateTime now)
    {
        if (Client.GameMode != GameMode.Survival) return $"the game mode is {Client.GameMode.ToString().ToLowerInvariant()}, not survival";
        // Night is no reason to stand still (TJ, 2026-11-03: "he should either stay busy at night or sleep"): at night he sleeps if there is a bed (the idle loop), otherwise he keeps to projects near him.
        if (Client.Health < 14) return $"health is {Client.Health / 2:0.#} of 10 hearts (needs 7)";
        if (Client.Food < 12) return $"food is {Client.Food / 2:0.#} of 10 (needs 6){(Client.Inventory.Count("bread") + Client.Inventory.Count("cooked_beef") + Client.Inventory.Count("apple") == 0 ? " and he carries nothing common to eat" : "")}";
        if (now - _lastProjectAt < ProjectCooldown) return "the last project was less than " + ProjectCooldown.TotalMinutes.ToString("0") + " minutes ago";
        if (Client.Flying) return "he is flying (the landing reflex comes first)";
        if (Client.BodySnapshot().InWater) return "he is in the water";
        if (Skills.NearestThreat(ProjectHostileRadius, Reflexes.AvoidWithin) is { } mob) return $"a {mob.Name.Bare().Replace('_', ' ')} is {mob.DistanceTo(Client.Position.X, Client.Position.Y, Client.Position.Z):0} blocks away and in sight (needs {ProjectHostileRadius:0}, or out of sight beyond {Reflexes.AvoidWithin:0})";
        return null;
    }

    private DateTime _lastBlockerLog = DateTime.MinValue, _nextSleepTry = DateTime.MinValue, _nextKitTry = DateTime.MinValue, _nextFoodTry = DateTime.MinValue, _nextLivableTry = DateTime.MinValue;
    private string _lastStatus = "";
    private bool _whoisAsked;

    /// <summary>
    /// What the server's EssentialsX says about God mode for this account, once he has asked (a <c>/whois</c> of his own name, once per join, 20 seconds in): true, false, or null when it did not say
    /// (no EssentialsX, or not asked yet). God mode cancels damage inside the plugin and does NOT set the abilities flag the client reads, so the flag alone cannot tell (TJ, 2026-11-03: a spider and
    /// three zombies on him for minutes and his health never moved).
    /// </summary>
    public bool? GodMode { get; private set; }

    // ---------------------------------------------------------------- learning from what happens (see ExperienceLog)

    private readonly ExperienceLog _experience = new();
    private string? _deathMessage;
    private DateTime _deathMessageAt = DateTime.MinValue;

    private static readonly System.Text.RegularExpressions.Regex DeathWords = new(
        @"\b(was slain|was shot|was killed|was blown up|was pricked|was fireballed|was squashed|was impaled|was struck|was pummeled|was stung|was burned|was doomed|died|drowned|burned to death|starved|suffocated|fell|hit the ground|tried to swim|went up in flames|withered|blew up|walked into|froze)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private DamageInfo? _lastDamage;
    private DateTime _lastDamageAt = DateTime.MinValue;
    private DateTime _lastDeathTold = DateTime.MinValue;
    private bool _healthKnown;

    /// <summary>What he does about monsters is learned from now on, kept in this world's own file (the owner's Vertex; tests that want the fixed rules leave it off).</summary>
    public void LearnFromDanger(string? file = null)
    {
        Reflexes.Learner = new DangerLearner(file ?? Path.Combine(Companion.Characters.Folder, "..", "worlds", MemoryFileName + ".danger.jsonl"));
        Reflexes.Decided += m => Log?.Invoke("danger: " + m);
        Log?.Invoke($"danger: learning on, {Reflexes.Learner.Outcomes} outcomes known. {Reflexes.Learner.Summary()}");
    }

    /// <summary>The hurts felt in the last minute, oldest first.</summary>
    internal IReadOnlyList<Pain> RecentPains => _experience.RecentPains;
    /// <summary>Deaths since this brain started.</summary>
    public int DeathsThisRun { get; private set; }
    /// <summary>Where and when he last died, with the server's message: his things lie there for five minutes.</summary>
    public (double X, double Y, double Z, DateTime At, string Message)? LastDeath { get; private set; }

    private Moment MomentNow()
    {
        var (x, y, z) = Client.Position;
        var mobs = Client.Entities.Values.Where(e => Skills.Hostile.Contains(e.Name) && e.DistanceTo(x, y, z) < 24).Select(e => (e.Name, e.DistanceTo(x, y, z))).ToList();
        var weapon = Client.Inventory.Find(s => s.Name.EndsWith("_sword") || s.Name.EndsWith("_axe")).Any();
        return new Moment(DateTime.UtcNow, Client.Health, Client.Food, Tasks.Current, Client.World.DayTime, Client.Weather, $"{x:0} {y:0} {z:0}", mobs, weapon, Skills.HasFood, y, Skills.HazardAround());
    }

    private void OnDamaged(DamageInfo damage)
    {
        _lastDamage = damage;
        _lastDamageAt = DateTime.UtcNow;
    }

    /// <summary>Who is behind a hurt, in a word and how far: "skeleton 12 blocks away". Null when nobody is (fire, a fall, hunger).</summary>
    private string? SourceOf(DamageInfo? damage)
    {
        if (damage == null) return null;
        var id = damage.CauseEntityId >= 0 ? damage.CauseEntityId : damage.DirectEntityId;
        return id >= 0 && Client.Entities.TryGetValue(id, out var who) ? (who.IsPlayer ? who.PlayerName : who.Name.Bare().Replace('_', ' ')) : null;
    }

    /// <summary>
    /// Health fell: pair it with the damage event just before it, so every hurt has a reason. A fall in health with no event at all did not go through the game's damage (a server plugin): that goes on the
    /// record too, in those words, because it is exactly the case nobody could explain (TJ, 2026-11-03: "the logs simply say he died with no reason").
    /// </summary>
    private void OnHealthChanged(float before, float after)
    {
        if (after >= before) return;
        var now = DateTime.UtcNow;
        // The first health figure after joining is his saved health arriving, not a hurt (the client starts from a full 20).
        if (!_healthKnown) { _healthKnown = true; return; }
        var damage = now - _lastDamageAt < TimeSpan.FromSeconds(1.5) ? _lastDamage : null;
        var type = damage?.Type ?? "no damage event";
        var source = SourceOf(damage);
        _experience.NotePain(new Pain(now, before - after, type, source));
        // How hot is too hot, from what it did to him: heat damage at a temperature moves the line below it. Only what the plugin does (no one's weapon, no block of fire under him).
        // Not below 40: the reading starts again from 25 at a respawn while the damage runs on, and a reading of 35 at such a moment pushed the line down until only 26 counted as cool (2026-11-04).
        if (type is "generic" or "on_fire" && source == null && Temperature is double hot && hot >= 43 && hot - 3 < Reflexes.HotAt)
        {
            Reflexes.HotAt = hot - 3;
            Log?.Invoke($"learned: heat hurts me at {hot:0.#} degrees, so I cool off from {Reflexes.HotAt:0.#}");
        }
        var (x, y, z) = Client.Position;
        Log?.Invoke($"hurt: -{(before - after) / 2:0.#} hearts by {type.Replace('_', ' ')}{(source != null ? $" from {source}" : "")}, {after / 2:0.#} left, at {x:0} {y:0} {z:0}{(Client.OnFire ? ", on fire" : "")}{(Temperature is double t ? $", {t:0.#} degrees" : "")}; doing: {Tasks.Current ?? "nothing"}");
        _lastDamage = null;
    }

    /// <summary>He died: the true record of it, at the moment it happens, and the job he was on ends (it was steering a body that is now somewhere else).</summary>
    private void OnDied(string message)
    {
        try
        {
            DeathsThisRun++;
            Reflexes.NoteDeath();
            _nextLivableTry = DateTime.MinValue;      // dying says nothing about whether he can find somewhere: from where he comes back, he looks again at once
            var (x, y, z) = Client.Position;
            LastDeath = (x, y, z, DateTime.UtcNow, message);
            var pains = _experience.PainSummary();
            RecordLesson(_experience.Died(MomentNow(), message));
            RefreshCoverThreshold();
            // Said aloud at most every three minutes: dying again the moment he is back is not news every time.
            if (DateTime.UtcNow - _lastDeathTold > TimeSpan.FromMinutes(3))
            {
                _lastDeathTold = DateTime.UtcNow;
                Companion.Post(new DeathEvent($"The server says: {message}." + (pains.Length > 0 ? $" What hurt you: {pains}." : "")));
            }
        }
        catch (Exception e) { Log?.Invoke("could not record a death: " + e); }
        Tasks.Abort("I died: " + message);
    }

    /// <summary>Once a second: what is near, what he is doing, how he is. The log turns it into lessons about deaths and big losses; they are stored, and how soon he takes cover follows from them.</summary>
    private async Task ExperienceLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            RefreshCoverThreshold();
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!Client.IsSpawned) continue;
                var moment = MomentNow();
                var message = DateTime.UtcNow - _deathMessageAt < TimeSpan.FromSeconds(15) ? _deathMessage : null;
                foreach (var lesson in _experience.Observe(moment, message))
                {
                    RecordLesson(lesson);
                    if (lesson.Key == "death") RefreshCoverThreshold();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>How many deaths in the last day happened without his having dug in (this run's, and those in his memory from before).</summary>
    private int DeathsWithoutCoverToday()
    {
        List<Skills.Lesson> mine; lock (_sessionLessons) mine = _sessionLessons.Where(l => l.Key == "death" && l.Has("no-cover")).ToList();
        var n = mine.Count;
        if (LongTerm is { } memory)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24).Ticks;
            foreach (var e in memory.Store.List(new SpawnDev.AI.AiMemoryFilter { Scope = MemoryScope, Kinds = [SpawnDev.AI.AiMemoryKind.Lesson] }))
                if (e.TimeTicks >= cutoff && e.Tags.Contains(ExperimentTag("death")) && e.Tags.Contains("no-cover") && !mine.Any(l => e.Text.StartsWith(l.Text, StringComparison.Ordinal))) n++;
        }
        return n;
    }

    private void RefreshCoverThreshold()
    {
        var deaths = DeathsWithoutCoverToday();
        var threshold = ExperienceLog.CoverBelow(deaths);
        if (threshold != Reflexes.CoverBelow) Log?.Invoke($"learned from {deaths} death{(deaths == 1 ? "" : "s")} today without digging in: I now take cover at {threshold / 2.0:0.#} hearts (it was {Reflexes.CoverBelow / 2.0:0.#})");
        Reflexes.CoverBelow = threshold;
    }

    /// <summary>The last few things that hurt him badly, from his own log, in his words: for the prompt when he decides what to do. Empty when nothing has.</summary>
    private string HurtBefore()
    {
        var lines = new List<string>();
        lock (_sessionLessons) lines.AddRange(_sessionLessons.Where(l => l.Key is "death" or "big-loss").TakeLast(3).Select(l => l.Text).Reverse());
        if (LongTerm is { } memory && lines.Count < 3)
            foreach (var e in memory.Store.List(new SpawnDev.AI.AiMemoryFilter { Scope = MemoryScope, Kinds = [SpawnDev.AI.AiMemoryKind.Lesson] })
                         .Where(e => e.Tags.Contains(ExperimentTag("death")) || e.Tags.Contains(ExperimentTag("big-loss"))).OrderByDescending(e => e.TimeTicks))
            {
                if (lines.Count >= 3) break;
                if (!lines.Any(l => e.Text.StartsWith(l, StringComparison.Ordinal))) lines.Add(e.Text);
            }
        return lines.Count == 0 ? "" : "What has hurt you before, from your own log (plan so it does not happen again): " + string.Join(" | ", lines.Select(l => l.Length > 260 ? l[..260] + "..." : l)) + " ";
    }

    private static readonly System.Text.RegularExpressions.Regex GodModeLine = new(@"God mode:\s*(?<v>true|false|enabled|disabled|on|off)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True or false when a line from the server says "God mode: ..." (EssentialsX's /whois), else null.</summary>
    internal static bool? GodModeIn(string text) =>
        GodModeLine.Match(text) is { Success: true } g ? g.Groups["v"].Value.ToLowerInvariant() is "true" or "enabled" or "on" : null;

    /// <summary>Turn god mode off when the server says it is on (the owner's setting): one /god per join, then ask again to confirm.</summary>
    public bool TurnOffGodMode { get; set; }
    private bool _godOffSent;

    private static readonly System.Text.RegularExpressions.Regex TemperatureText = new(@"(?<v>-?\d+(?:[.,]\d+)?)\s*(?:°\s*)?C\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The temperature in an action-bar line ("Temperature: 56.6 C" on mc.spawndev.com, "23°C"), or null. RealisticSeasons shows each player theirs there, in a format the server sets.</summary>
    internal static double? TemperatureIn(string text) =>
        TemperatureText.Match(text) is { Success: true } t && double.TryParse(t.Groups["v"].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>His temperature as the server last showed it, or null when it shows none.</summary>
    public double? Temperature { get; private set; }
    /// <summary>The last action-bar line, as it came.</summary>
    public string? LastActionBar { get; private set; }
    private double? _temperatureLogged;
    private DateTime _actionBarLogged = DateTime.MinValue;

    private void OnServerText(ChatMessage m)
    {
        if (m.Kind == ChatKind.ActionBar)
        {
            LastActionBar = m.Content;
            if (TemperatureIn(m.Content) is double degrees)
            {
                Temperature = degrees;
                Skills.NoteHeat(degrees);
                if (_temperatureLogged is not double last || Math.Abs(last - degrees) >= 5) { _temperatureLogged = degrees; var (hx, _, hz) = Client.FeetBlock; Log?.Invoke($"temperature: {m.Content.Trim()} at {hx} {hz}"); }
            }
            else if (DateTime.UtcNow - _actionBarLogged > TimeSpan.FromSeconds(30))
            {
                _actionBarLogged = DateTime.UtcNow;
                Log?.Invoke($"action bar: {m.Content.Trim()}");
            }
            return;
        }
        // How he died, as the server says it ("HereticSpawn was slain by Phantom"): kept for the experience log.
        if (m.Kind != ChatKind.Player && m.Content.StartsWith(Client.Profile.Name + " ", StringComparison.Ordinal) && DeathWords.IsMatch(m.Content))
        {
            _deathMessage = m.Content.Trim();
            _deathMessageAt = DateTime.UtcNow;
        }
        if (m.Kind == ChatKind.Player || GodModeIn(m.Content) is not bool god) return;
        GodMode = god;
        Log?.Invoke($"the server says God mode is {(god ? "ON for this account (damage is cancelled)" : "off")}");
        if (god && TurnOffGodMode && !_godOffSent)
        {
            _godOffSent = true;
            Log?.Invoke("turning god mode off, as the owner asked (/god), then asking again");
            // Fixed commands, nothing a model wrote. /god toggles, so only once per join, and the second /whois shows what it is now.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Client.SendChatAsync("/god");
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    await Client.SendChatAsync("/whois " + Client.Profile.Name);
                }
                catch (Exception e) { Log?.Invoke("could not turn god mode off: " + e.Message); }
            });
        }
    }

    /// <summary>Is it night in the game (when a bed can be slept in: from sunset to just before sunrise)?</summary>
    private bool IsNight() => Client.World.DayTime % 24000 is >= 12600 and < 23400;

    /// <summary>How close a monster may be for him to start a project of his own (blocks).</summary>
    internal const double ProjectHostileRadius = 16;

    /// <summary>No project of his own while standing in water or with a monster close (live on AubsCraft, 2026-11-01: he stood in a river among four creepers and chose to build a platform).</summary>
    internal static bool SafeForOwnProject(bool inWater, bool hostileNear) => !inWater && !hostileNear;

    /// <summary>
    /// The projects Vertex may be offered on its own: only ones it can do with what it carries or can gather. It was offered "plant some crops" with no seeds in its pockets (2026-11-01: it
    /// chose "plant some carrots", the job failed, and it told everyone it had finished); planting needs seeds or crops, fishing needs a rod, everything else it can start from nothing.
    /// </summary>
    /// <param name="count">How many of an item (short name, "fishing_rod", "wheat_seeds") it carries.</param>
    internal static List<string> OwnProjectIdeas(Func<string, int> count, bool night = false)
    {
        var ideas = new List<string>
        {
            // Only shapes build_shape has: offered "a shelter or garden" he chose "garden" twice and failed twice (2026-11-02), there being no such shape.
            "build something small with build_shape (a house, a platform or a pen for animals)",
            "gather some wood, stone or dirt",
        };
        // Wandering off over open ground is for the day; at night he builds and gathers close by.
        if (!night) ideas.Insert(1, "explore one direction (no farther than about 40 blocks) and tell what you find");
        if (count("fishing_rod") > 0) ideas.Add("go fishing");
        if (new[] { "wheat_seeds", "beetroot_seeds", "melon_seeds", "pumpkin_seeds", "carrot", "potato" }.Any(seed => count(seed) > 0)) ideas.Add("plant the seeds or crops you carry");
        return ideas;
    }

    /// <summary>
    /// Asks the language model for ONE small project and lets it start it with whichever tool fits (build_shape, explore, collect, fish, farm... or set_goal for something that takes many
    /// steps): the same tools and rules as any request, so nothing it does on its own is anything a friend could not have asked for.
    /// </summary>
    private async Task ChooseOwnProjectAsync(CancellationToken ct)
    {
        try
        {
            string[] recent; lock (_recentProjects) recent = _recentProjects.ToArray();
            Log?.Invoke("choosing a project of my own");
            _choosing = true;
            var night = IsNight();
            var ideas = OwnProjectIdeas(item => Client.Inventory.Count(item), night);
            await Companion.RespondAsync("[Game update, not a friend talking: nobody needs you right now and you have no goal, so pick ONE small project of your own to do while you wait. "
                + $"Something a friend would enjoy watching, and only what you can do right now: {string.Join("; ", ideas)}. "
                + (night ? "It is night: stay close to where you are. " : "Keep it small and safe, in daylight, near where you are. ")
                + "Never break or build on anything that looks like someone's build. "
                + HurtBefore()
                + (recent.Length > 0 ? $"You recently did: {string.Join("; ", recent)}. Pick something different. " : "")
                + "Call the ONE tool that starts it (set_goal only for something that really takes many steps), then say in one short sentence what you chose.]", ct);
        }
        finally
        {
            _choosing = false;
            _projectQueued = false;
        }
    }

    /// <summary>What is going on in the game right now (position, health, nearby, memory of the people here, goals, places).</summary>
    public string DescribeNow()
    {
        var sb = new StringBuilder();
        var (x, y, z) = Client.Position;
        sb.AppendLine($"- In the game you are at x {x:0} y {y:0} z {z:0} in {Client.World.Dimension.Bare()}, " +
                      $"health {Client.Health / 2:0.#} of 10 hearts, food {Client.Food / 2:0.#} of 10, game mode {Client.GameMode.ToString().ToLowerInvariant()}.");
        if (Skills.Awareness() is { } around) sb.AppendLine("- " + around);
        var time = Client.World.DayTime % 24000;
        sb.AppendLine($"- It is {(time is >= 0 and < 12000 ? "daytime" : time < 13000 ? "sunset" : time < 23000 ? "night" : "sunrise")} in the game.");
        sb.AppendLine($"- You are carrying: {Skills.InventorySummary()}. Holding: {(Client.Inventory.MainHand.IsEmpty ? "nothing" : Client.Inventory.MainHand.ToString())}.");
        sb.AppendLine($"- You are {(Tasks.Current is string task ? task : "not doing anything special")}.");
        if (Client.GameMode == GameMode.Survival && Skills.WorkingToward() is { } toward) sb.AppendLine($"- When nobody needs you, you are working your way up: next is {toward.Label}.");
        if (DeathsThisRun > 0) sb.AppendLine($"- You have died {DeathsThisRun} time{(DeathsThisRun == 1 ? "" : "s")} since you started today" + (LastDeath is { } ld ? $", last {Ago(DateTime.UtcNow - ld.At)}: {ld.Message}." : "."));
        if (Reflexes.Learner?.Summary() is { Length: > 0 } learned) sb.AppendLine("- What you have found out about monsters, from counting what happened: " + learned);
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
                // What the server says he is: game mode and abilities (invulnerable is set by creative mode, god mode and the like). Said when it first becomes known and whenever it changes, so "is he
                // really in survival, can he be hurt?" is answered in the log, not by someone typing commands (TJ, 2026-11-03).
                // Once per join, 20 seconds in: ask the server about himself. A fixed command with his own name, nothing a model wrote.
                if (Client.IsSpawned && !_whoisAsked && now - _startedAt > TimeSpan.FromSeconds(20))
                {
                    _whoisAsked = true;
                    // And what the server is running: fixed commands again. A plugin is the only source of the "generic" damage that was killing him (the plain "died"), so which plugins there are belongs in the log.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Client.SendChatAsync("/whois " + Client.Profile.Name);
                            await Task.Delay(2000);
                            await Client.SendChatAsync("/plugins");
                            await Task.Delay(2000);
                            await Client.SendChatAsync("/tps");
                        }
                        catch (Exception e) { Log?.Invoke("could not ask the server about myself: " + e.Message); }
                    });
                }
                if (Client.IsSpawned)
                {
                    var status =$"game mode {Client.GameMode.ToString().ToLowerInvariant()}, {(Client.Invulnerable ? "INVULNERABLE (the server says nothing can hurt him)" : "can be hurt")}, {(Client.MayFly ? "may fly" : "cannot fly")}";
                    if (status != _lastStatus) { _lastStatus = status; Log?.Invoke($"status: {status}; health {Client.Health / 2:0.#} of 10 hearts"); }
                }
                if (Tasks.IsBusy) { _lastActivity = now; continue; }
                var goal = Memory.ActiveGoals.FirstOrDefault();
                var unstudied = goal == null && !Staying && StudyWhenIdle && Skills.UnstudiedBlocks(16).Count > 0;     // the scan only when it could matter
                // Idle long enough to choose a project and not choosing one: say why, at most every 5 minutes, so that "he is idle" has an answer in the log.
                if (SelfDirectedPlay && goal == null && !Staying && Client.IsSpawned && !_projectQueued && now - _lastActivity >= SelfDirectedIdle && now - _lastBlockerLog >= TimeSpan.FromMinutes(5)
                    && ProjectBlocker(now) is { } why)
                {
                    _lastBlockerLog = now;
                    Log?.Invoke("not starting a project of my own: " + why);      // not "choosing a project": the tests (and the panel) look for that phrase to see a project being chosen
                }
                // Night, nothing to do, a bed within 48 blocks (his own house's, or any): sleep until morning, as a player does (TJ, 2026-11-03: "he can sleep"). A failed try waits 3 minutes.
                if (goal == null && !Staying && Client.IsSpawned && !_projectQueued && Client.GameMode == GameMode.Survival && IsNight() && now - _lastActivity >= TimeSpan.FromSeconds(20) && now >= _nextSleepTry
                    && Skills.NearestHostile(10) == null && Client.FindBeds(48, 1).Count > 0)
                {
                    _nextSleepTry = now + TimeSpan.FromMinutes(3);
                    _lastActivity = now;
                    Log?.Invoke("night, nothing to do and a bed close by: going to sleep");
                    Tasks.Start("sleeping until morning", Skills.SleepAsync);
                    continue;
                }
                // Hungry with nothing to eat: food comes first (2026-11-03: he gave up on a kit that needed wood and, TJ thinks, starved). Half a bar or less, every 45 seconds until he has some.
                if (goal == null && !Staying && Client.IsSpawned && !_projectQueued && Client.GameMode == GameMode.Survival && Client.Food <= 10 && !Skills.HasFood
                    && now - _lastActivity >= TimeSpan.FromSeconds(5) && now >= _nextFoodTry && Skills.NearestHostile(8) == null)
                {
                    _nextFoodTry = now + TimeSpan.FromSeconds(45);
                    _lastActivity = now;
                    Log?.Invoke($"hungry (food {Client.Food / 2.0:0.#} of 10) and nothing to eat: going to hunt");
                    Tasks.Start("finding food", ct => Skills.GetFoodThenSummaryAsync([], hunt: true, ct));
                    continue;
                }
                // Before anything else, somewhere he can live: nothing gets built on land that burns him down in a minute. He has this from his own readings (the biome he stands in has read too hot, or he reads too hot now), so it is
                // true on any server with heat of this kind and never comes up on one without. A failed search waits five minutes while he does what he can where he is.
                var tooHot = Temperature is double degreesNow && (degreesNow >= Reflexes.HotAt || Skills.KnownTooHot(Skills.BiomeHere, Reflexes.HotAt));
                if (SelfDirectedPlay && goal == null && !Staying && Client.IsSpawned && !_projectQueued && Client.GameMode == GameMode.Survival && now >= _nextLivableTry && (tooHot || Skills.AtSea))
                {
                    _nextLivableTry = now + TimeSpan.FromSeconds(10);
                    _lastActivity = now;
                    Log?.Invoke($"{(tooHot ? $"too hot to live here ({Temperature:0.#} degrees in the {Skills.BiomeHere?.Replace('_', ' ')})" : $"out at sea, in the {Skills.BiomeHere?.Replace('_', ' ')}")}: looking for somewhere I can live. What I know: {string.Join(", ", Skills.BiomeHeat().Select(b => $"{b.Biome.Replace('_', ' ')} {b.Degrees:0}"))}");
                    Tasks.StartReflex("looking for somewhere I can live", async ct =>
                    {
                        var result = await Skills.FindLivableLandAsync(() => Temperature, Reflexes.HotAt, ct);
                        Log?.Invoke((result.Item1 ? "found: " : "not found: ") + result.Item2);
                        // Only a search that looked and failed waits. Dying on the way is no answer about the place: he reached land reading 25, died, and spent the next 5 minutes back on the island (2026-11-04).
                        if (!result.Item1) _nextLivableTry = Now() + TimeSpan.FromMinutes(5);
                        else Companion.Post(new MilestoneEvent("somewhere you can live, away from the heat that kept killing you: " + result.Item2));
                        return result;
                    });
                    continue;
                }
                // Too hot here but not yet time to try again: wait, do not start work. Falling through to the next rung sent him off on a long job on the burning island, which nothing re-examines until it ends: 33 deaths in 30 minutes (2026-11-04).
                if (tooHot && SelfDirectedPlay && goal == null && !Staying && Client.IsSpawned && !_projectQueued && Client.GameMode == GameMode.Survival) continue;
                // Getting on in the world: tools, food, a bed, iron... whatever comes next from what he has right now. Never a fixed script: after a death he is back at the top, and nothing else needs to know.
                // Quiet (the job's start and end are not news); reaching a step is, and is said once. A friend's request or a goal replaces it at once.
                if (SelfDirectedPlay && goal == null && !Staying && Client.IsSpawned && !_projectQueued && Client.GameMode == GameMode.Survival && now - _lastActivity >= TimeSpan.FromSeconds(3)
                    && now >= _nextKitTry && Skills.NearestHostile(10) == null && Skills.NextMilestone() is { } step)
                {
                    _nextKitTry = now + TimeSpan.FromSeconds(5);
                    _lastActivity = now;
                    Log?.Invoke($"working toward {step.Label}");
                    Tasks.StartReflex($"working toward {step.Label}", ct => Skills.AdvanceAsync(step, ct));
                    continue;
                }
                var action = IdlePolicy.Decide(Staying, Tasks.IsBusy, _goalQueued || _projectQueued, Client.IsSpawned, now - _lastActivity, IdleBeforeGoals, goal != null, StudyWhenIdle, unstudied,
                    SelfDirectedPlay, SelfDirectedIdle, SelfDirectedPlay && FitForOwnProject(now));
                if (action == IdleAction.ChooseProject && NextExperiment() is { } experiment)
                {
                    // Something he has never tried and can try right here: do that before inventing a project (nothing is at stake, and he writes down what he finds out).
                    _lastActivity = now;
                    _lastProjectAt = now;
                    Log?.Invoke("experiment: " + experiment.Name);
                    Tasks.Start(experiment.Name, experiment.Run);
                }
                else
                if (action == IdleAction.ChooseProject)
                {
                    _projectQueued = true;
                    _lastActivity = now;
                    _lastProjectAt = now;
                    Companion.Post(new OwnProjectEvent());
                }
                else
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
            await Companion.RespondAsync($"[Game update, not a friend talking: {OutcomeReport.ForModel(step, goal.Text)} Then tell your friends how it went, in one short first-person sentence.]", ct);
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

    /// <summary>
    /// Whose a new goal is. A goal Vertex sets for itself while choosing its own project is nobody's: the model writes player "me" out of habit, and "me" means whoever spoke last, so
    /// without this a project of its own was recorded as FOR Aubri (seen live 2026-11-01, "plant some carrots") and, once done, written into her history.
    /// </summary>
    internal static string? GoalOwner(bool choosingOwnProject, string? requested) => choosingOwnProject ? null : requested;

    public string SetGoal(string goal, string? forPlayer)
    {
        if (string.IsNullOrWhiteSpace(goal)) return "no goal given";
        forPlayer = GoalOwner(_choosing, forPlayer);
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
        Client.ChatReceived -= OnServerText;
        Client.Damaged -= OnDamaged;
        Client.HealthChanged -= OnHealthChanged;
        Client.Died -= OnDied;
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
