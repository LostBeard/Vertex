using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using SpawnDev.AI;
using SpawnDev.AI.Server;
using SpawnDev.ILGPU.ML.Preprocessing;
using SpawnDev.Reachy;
using Vertex.AI;
using Vertex.Core;

namespace Vertex.Brain;

/// <summary>Something the brain should consider: speech, chat, or a finished task.</summary>
public abstract record BrainEvent;
/// <param name="HeardTick">When the speaker stopped (<see cref="Environment.TickCount64"/>; 0 = when it arrived).</param>
/// <param name="SttMs">Speech to text time for a spoken line.</param>
/// <param name="Channel">Where it was heard; the answer goes back there (null: the control panel or a test).</param>
public sealed record SaidEvent(Guid Speaker, string SpeakerName, string Text, bool Voice, long HeardTick = 0, double SttMs = 0,
    IVoiceChannel? Channel = null) : BrainEvent;
/// <summary>Prefill the system prompt and tool list before anyone talks (at start, after a character switch or a body change).</summary>
public sealed record PrimeEvent : BrainEvent;
/// <summary>Someone Vertex knows has walked in after a long absence: say hi, by name, if nothing else is going on.</summary>
/// <param name="Name">Who.</param>
/// <param name="Absence">How long since they were last seen.</param>
/// <param name="Channel">Where to say it (the robot).</param>
public sealed record GreetEvent(string Name, TimeSpan Absence, IVoiceChannel? Channel) : BrainEvent;

/// <summary>
/// Vertex's mind, wherever it is: the conversation. It hears lines from its voice channels (proximity voice in a game,
/// the robot's microphone, the control panel), decides when it is being talked to, and answers as the current character
/// through the language model, speaking sentence by sentence as the reply is generated. Bodies (a Minecraft world, the
/// Reachy Mini) add what is going on where they are, what Vertex can do there (tools), and events of their own.
/// </summary>
/// <remarks>
/// Hearing never waits for thinking: a turn is interrupted when Vertex is called by name or told to stop, and a turn that
/// runs past <see cref="TurnTimeout"/> says so and gives up (2026-10-12: one slow turn had left everything TJ said after it
/// unheard). The system prompt and tool list are the long, fixed start of every prompt and are prefilled at start, so the
/// first question only costs its own words.
/// </remarks>
public sealed class Companion : IAsyncDisposable
{
    private readonly Channel<BrainEvent> _events = Channel.CreateUnbounded<BrainEvent>();
    private readonly List<AiChatMessage> _history = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IBody> _bodies = [];
    private readonly List<IVoiceChannel> _channels = [];
    private readonly Dictionary<IVoiceChannel, Action<HeardLine>> _listeners = [];
    private Task? _loop;
    private DateTime _lastExchange = DateTime.MinValue;
    private Guid? _lastPartner;
    private int _toolRuns;
    private Turn? _turn;

    /// <summary>History messages kept (user + assistant); past this, the older half is dropped at once.</summary>
    public const int MaxHistory = 24;

    public VertexAi Ai { get; }

    /// <summary>
    /// The repetition penalty of the reply sampler. It also punishes tokens that are already in the PROMPT, and the prompt holds a 4,000-token tool list full of quotes, braces and
    /// parameter names: a penalty on those pushes a tool call's arguments toward gibberish (seen 2026-10-15: a correct build_shape call turning into token soup in an optional argument).
    /// </summary>
    public float RepetitionPenalty { get; set; } = 1.1f;
    public CharacterStore Characters { get; }
    public Character Character { get; private set; }
    /// <summary>The chat model (an Ollama-cache name such as "llama3.1:8b").</summary>
    public string Model { get; set; }
    public Mouth Mouth { get; }
    /// <summary>Name of whoever spoke last (what "me" means in a request).</summary>
    public string? CurrentSpeaker { get; private set; }
    /// <summary>Where the current (or last) friend's turn came from.</summary>
    public IVoiceChannel? CurrentChannel { get; private set; }
    /// <summary>The longest a turn may think before it gives up and says so (a stalled generation must not deafen Vertex).</summary>
    public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>True while a turn is being thought about (or its reply generated).</summary>
    public bool IsThinking => Volatile.Read(ref _turn) != null;
    public IReadOnlyList<IBody> Bodies { get { lock (_bodies) return _bodies.ToList(); } }
    public IReadOnlyList<IVoiceChannel> Channels { get { lock (_channels) return _channels.ToList(); } }
    /// <summary>The clock (tests move it).</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public event Action<string>? Log;
    /// <summary>A turn started (true) or ended (false): a robot shows "thinking" with it.</summary>
    public event Action<bool>? Thinking;

    public Companion(VertexAi ai, CharacterStore characters, Character character, string model)
    {
        Ai = ai;
        Characters = characters;
        Character = character;
        Model = model;
        Mouth = new Mouth(ai.SpeakAsync) { VoiceId = character.Voice };
        Mouth.Log += m => Log?.Invoke("[mouth] " + m);
    }

    // ---------------------------------------------------------------- bodies and channels

    /// <summary>Adds a body: its tools join the language model's (the prompt prefix changes, so it is prefilled again).</summary>
    public void Attach(IBody body)
    {
        lock (_bodies) _bodies.Add(body);
        foreach (var tool in body.Tools) Ai.Tools.Register(tool);
        if (_loop != null) _events.Writer.TryWrite(new PrimeEvent());
    }

    /// <summary>Removes a body and its tools.</summary>
    public void Detach(IBody body)
    {
        lock (_bodies) _bodies.Remove(body);
        foreach (var tool in body.Tools) Ai.Tools.Unregister(tool.Name);
        if (_loop != null) _events.Writer.TryWrite(new PrimeEvent());
    }

    /// <summary>Starts listening on a channel; its lines become friends talking.</summary>
    public void AddChannel(IVoiceChannel channel)
    {
        void OnHeard(HeardLine h) => Hear(new SaidEvent(SpeakerId(h.SpeakerKey), h.SpeakerName, h.Text, h.Voice, h.EndedTick, h.SttMs, h.Channel));
        lock (_channels)
        {
            _channels.Add(channel);
            _listeners[channel] = OnHeard;
        }
        channel.Heard += OnHeard;
    }

    public void RemoveChannel(IVoiceChannel channel)
    {
        lock (_channels)
        {
            _channels.Remove(channel);
            if (_listeners.Remove(channel, out var l)) channel.Heard -= l;
        }
    }

    /// <summary>A Minecraft player's UUID as is; any other speaker key (the robot's room) as a stable id from its text.</summary>
    /// <summary>
    /// The key of a voice that could not be identified (a clip too short to tell: a bare "Vertex."). It continues a conversation with anyone, in either
    /// direction: live, "Vertex." (unidentified) was followed by Todd's question (identified), the two keys differed, and the question was dropped as
    /// "not said to me".
    /// </summary>
    public static readonly Guid UnidentifiedVoice = SpeakerId("room");

    public static Guid SpeakerId(string key) =>
        Guid.TryParse(key, out var g) ? g : new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    private readonly TaskCompletionSource _primed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when the system prompt and tools have been prefilled once (or the attempt ended). Measured live 2026-10-15 that first prefill takes 43 s (the language model's kernels for a
    /// prompt that long compile on first use), and everything that needs the GPU waits behind it: the robot said "Hi!" and then nothing for a minute. A host awaits this BEFORE it speaks.
    /// </summary>
    public Task WhenPrimed => _primed.Task;

    public void Start()
    {
        _events.Writer.TryWrite(new PrimeEvent());
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _ = Task.Run(() => LaneReportLoopAsync(_cts.Token));
    }

    /// <summary>A tool did something (counted to catch a reply that claims an action no tool took).</summary>
    public void NoteToolRun() => Interlocked.Increment(ref _toolRuns);

    /// <summary>Lets bodies, the control panel and tests hand the companion something to consider.</summary>
    public void Post(BrainEvent e)
    {
        if (e is SaidEvent s) Hear(s);
        else _events.Writer.TryWrite(e);
    }

    /// <summary>Says something outside a turn (a greeting on start, a notice).</summary>
    public void Say(string text, IReadOnlyList<IVoiceChannel>? to = null) => Mouth.Say(text, to ?? Channels);

    // ---------------------------------------------------------------- hearing

    /// <summary>
    /// Something was said. Hearing never waits for thinking: a turn already running is interrupted when this is
    /// addressed to Vertex and either the running turn is not a friend's (a game update, a goal step) or Vertex is
    /// called by name or told to stop. Anything else waits its turn (and a speaker's waiting lines are answered
    /// together). Before 2026-10-12 one slow turn left every later line queued behind it ("not hearing anything").
    /// </summary>
    private void Hear(SaidEvent e)
    {
        // The person kept talking before the robot made a sound ("We're gonna play Minecraft today." ... "What's the best thing to build first?"): people do not
        // take turns sentence by sentence. The answer being formed is dropped and both lines are answered together. (Once it is speaking, the ears are shut and
        // this cannot happen; a "stop" or "wait" still cuts a turn short as before.)
        if (Volatile.Read(ref _turn) is { ForPerson: true, Spoke: false, Said: { } earlier } thinking && thinking.Interruptible && e.Voice && earlier.Voice
            && Environment.TickCount64 - thinking.StartedTick < 12_000 && IsAddressed(e) && !Stops(e.Text)
            && (earlier.Speaker == e.Speaker || earlier.Speaker == UnidentifiedVoice || e.Speaker == UnidentifiedVoice))
        {
            Log?.Invoke($"  ({e.SpeakerName} kept talking before I answered: answering both lines together)");
            thinking.Interrupted = true;
            Mouth.Hush();
            try { thinking.Cts.Cancel(); } catch (ObjectDisposedException) { }
            // The identified voice wins (a bare "Vertex." was not identified, the sentence after it was).
            var who = e.Speaker == UnidentifiedVoice ? earlier : e;
            _events.Writer.TryWrite(e with { Speaker = who.Speaker, SpeakerName = who.SpeakerName, Text = earlier.Text + " " + e.Text });
            return;
        }
        if (Volatile.Read(ref _turn) is { } turn && turn.Interruptible && IsAddressed(e) && (!turn.ForPerson || Interrupts(e.Text)))
        {
            Log?.Invoke($"  (interrupted by {e.SpeakerName})");
            turn.Interrupted = true;
            Mouth.Hush();
            try { turn.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        _events.Writer.TryWrite(e);
    }

    /// <summary>How long a line that is not a question waits before it is answered, in case the person is still going.</summary>
    public TimeSpan StatementGrace { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Called by name, or told to stop or wait: worth cutting a friend's turn short for.</summary>
    private bool Interrupts(string text) => NamesVertex(" " + text.ToLowerInvariant() + " ") || Stops(text);

    /// <summary>Told to stop, wait or forget it.</summary>
    private static bool Stops(string text)
    {
        var t = " " + text.ToLowerInvariant() + " ";
        return t.Contains(" stop") || t.Contains(" wait") || t.Contains(" never mind") || t.Contains(" nevermind");
    }

    /// <summary>
    /// The line calls Vertex (or the current character) by name, however the recogniser wrote it: the spellings measured so far
    /// (Whisper wrote "birdtakes", Parakeet "Rutax" and "Per text") and near-misses of its consonants. See <see cref="WakeWord"/>.
    /// </summary>
    private bool NamesVertex(string text) => WakeWord.Names(text, Character.Name);

    /// <summary>
    /// Talk back when our name is said, when we are mid-conversation with this speaker (within 45 s), when the channel
    /// is one where everything said is said to Vertex (the robot's microphone), or when a body says so (in Minecraft:
    /// the only other player around, speaking out loud).
    /// </summary>
    public bool IsAddressed(SaidEvent e)
    {
        if (e.Speaker == Guid.Empty) return true; // the operator's console and the control panel talk only to us
        if (NamesVertex(e.Text.ToLowerInvariant())) return true;
        if (_lastPartner is { } partner && DateTime.UtcNow - _lastExchange < TimeSpan.FromSeconds(45)
            && (partner == e.Speaker || partner == UnidentifiedVoice || e.Speaker == UnidentifiedVoice)) return true;
        if (e.Channel?.AnswersEverything == true) return true;
        foreach (var body in Bodies)
            if (body.IsAddressed(e) == true) return true;
        return false;
    }

    /// <summary>The turn being thought or spoken right now (null when Vertex is idle).</summary>
    private sealed class Turn(CancellationTokenSource cts, bool forPerson, bool interruptible)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public bool ForPerson { get; } = forPerson;
        /// <summary>False for the prefill at start: two seconds, and cutting it only makes the next answer slower
        /// (on the robot, the room's first words cut it off - 2026-10-12).</summary>
        public bool Interruptible { get; } = interruptible;
        public volatile bool Interrupted;
        /// <summary>The line this turn answers (null for a game event or the prefill).</summary>
        public SaidEvent? Said { get; set; }
        /// <summary>When the turn began (<see cref="Environment.TickCount64"/>).</summary>
        public long StartedTick { get; } = Environment.TickCount64;
        /// <summary>Some sound of the reply has started: from here the person is not interrupting a thought but a speaker.</summary>
        public volatile bool Spoke;
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
            if (!forPerson) Log?.Invoke($"  (turn: {e.GetType().Name})");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(e is PrimeEvent ? TimeSpan.FromMinutes(5) : TurnTimeout);   // the first prefill of a new process is slow: not a lost train of thought
            var turn = new Turn(cts, forPerson, interruptible: e is not PrimeEvent) { Said = e as SaidEvent };
            var clock = e is SaidEvent heard ? new TurnClock(heard.HeardTick, heard.SttMs) : new TurnClock();
            Volatile.Write(ref _turn, turn);
            // Shown (the robot looks thoughtful) only for something Vertex will actually answer or do.
            var shows = e is not PrimeEvent && (e is not SaidEvent heardLine || IsAddressed(heardLine));
            if (shows) Thinking?.Invoke(true);
            try
            {
                // A statement is rarely all someone has to say ("We're gonna play Minecraft today." ... then what they actually want): wait a moment before starting on it,
                // and if they carry on the answer is never begun (see Hear). A question is answered at once.
                if (e is SaidEvent statement && statement.Voice && StatementGrace > TimeSpan.Zero && IsAddressed(statement)
                    && !WakeWord.IsOnlyTheName(statement.Text, Character.Name) && TurnText.ExpectsMore(statement.Text, Character.Name))
                {
                    // The conversation is with this person from now on, so a continuation inside the wait is recognised as theirs.
                    _lastPartner = statement.Speaker;
                    _lastExchange = DateTime.UtcNow;
                    Log?.Invoke($"  (a statement, not a question: waiting {StatementGrace.TotalMilliseconds:0} ms in case there is more)");
                    await Task.Delay(StatementGrace, cts.Token);
                }
                await HandleAsync(e, clock, cts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException)
            {
                // Interrupted (someone spoke) or out of time: either way the next thing in line gets a turn now.
                if (!turn.Interrupted)
                {
                    Log?.Invoke($"  (gave up thinking after {TurnTimeout.TotalSeconds:0} s)");
                    if (forPerson) Mouth.Say("Sorry, I lost my train of thought. Can you say that again?", ReplyTo((e as SaidEvent)?.Channel));
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
                if (shows) Thinking?.Invoke(false);
            }
        }
    }

    private async Task HandleAsync(BrainEvent e, TurnClock clock, CancellationToken ct)
    {
        switch (e)
        {
            case PrimeEvent:
                try { await PrimeAsync(ct); }
                finally { _primed.TrySetResult(); }
                return;
            case SaidEvent s when IsAddressed(s):
                CurrentSpeaker = s.SpeakerName;
                CurrentChannel = s.Channel;
                _lastPartner = s.Speaker;
                _lastExchange = DateTime.UtcNow;
                // Just the name ("Vertex."): the way a smart speaker does it - show at once that it is listening and wait for the rest, with no spoken answer.
                // Answering took 3.6 s, landed on top of what the person said next, and the ears are shut while it talks.
                if (s.Voice && s.Channel?.AnswersEverything == false && WakeWord.IsOnlyTheName(s.Text, Character.Name))
                {
                    Log?.Invoke($"  (called by name only: listening for the rest)");
                    foreach (var body in Bodies) body.Act("antennas perk up");
                    return;
                }
                foreach (var body in Bodies) body.OnFriendSpoke(s);
                Mouth.Hush();
                // Where it was said only matters once Vertex is in more than one place.
                var how = s.Voice ? "out loud" : "in chat";
                var where = s.Channel != null && Channels.Count > 1 ? $" ({how}, {s.Channel.HeardThrough})" : $" ({how})";
                // A misheard name is put right before the model sees it ("Rutax, what's the" -> "Vertex, what's the"): left alone it answers "I'm not sure who Rutax is".
                var said = $"{s.SpeakerName} says{where}: {WakeWord.Normalize(s.Text, Character.Name)}";
                // System One: an urgent command we are sure of runs NOW; the language model only words the reply.
                string? done = null;
                foreach (var body in Bodies)
                    if ((done = await body.FastActAsync(s)) != null) break;
                if (done != null)
                    await RespondAsync($"{said}\n[You already did it: {done}] Answer in one short sentence; do not call tools.", ct, allowTools: false, clock, s.Channel);
                else
                    await RespondAsync(said, ct, clock: clock, replyChannel: s.Channel);
                return;
            case SaidEvent:
                return; // not for us
            case GreetEvent g:
                // Nothing was said to Vertex: it simply noticed someone. A short, warm, spoken hello by name - not a reply to anyone, and no tools.
                foreach (var body in Bodies) body.Act("antennas perk up");
                await RespondAsync($"[{g.Name} has just come into the room; you have not seen them for {DescribeAbsence(g.Absence)}. Say hi to {g.Name} by name, in one short friendly sentence. Do not call tools.]",
                    ct, allowTools: false, clock, g.Channel);
                return;
        }
        foreach (var body in Bodies)
            if (await body.HandleEventAsync(e, clock, ct)) return;
    }

    /// <summary>
    /// Says hi to someone who has just come back, if Vertex is not busy: no turn running, nobody talked to it in the last minute, and nothing else waiting.
    /// Returns whether the greeting was queued.
    /// </summary>
    public bool TryGreet(string name, TimeSpan absence, IVoiceChannel? channel)
    {
        if (Volatile.Read(ref _turn) != null) return false;
        if (DateTime.UtcNow - _lastExchange < TimeSpan.FromSeconds(60)) return false;
        if (_events.Reader.TryPeek(out _)) return false;
        Log?.Invoke($"  (greeting {name}, back after {DescribeAbsence(absence)})");
        return _events.Writer.TryWrite(new GreetEvent(name, absence, channel));
    }

    private static string DescribeAbsence(TimeSpan t) =>
        t.TotalHours >= 36 ? $"{(int)t.TotalDays} days" : t.TotalHours >= 1.5 ? $"about {(int)Math.Round(t.TotalHours)} hours" : "a while";

    /// <summary>Where a reply goes: back to the channel it came from, or every channel (the control panel, a game event).</summary>
    public IReadOnlyList<IVoiceChannel> ReplyTo(IVoiceChannel? channel) => channel != null ? [channel] : Channels;

    // ---------------------------------------------------------------- talking

    public string SystemPrompt()
    {
        var bodies = Bodies;
        var sb = new StringBuilder();
        sb.AppendLine(Character.Persona);
        sb.AppendLine();
        sb.Append($"Your name is {Character.Name}.");
        foreach (var body in bodies) sb.Append(' ').Append(body.Setting);
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Speak as yourself in the first person (\"I\"), to your friends (\"you\"), the way people talk out loud: friendly and simple (a child may be listening). No lists, emojis or markdown.");
        sb.AppendLine("- Match the length to what was asked. A greeting, a thank-you, a quick question or a job gets one or two short sentences. When you are asked to explain something, tell about something, or give ideas or advice, answer properly in three to six short sentences, with real details, and no more than that.");
        sb.AppendLine("- Tool results describe what YOU are doing. If a job has only started, say you are on your way or working on it, not that it is done.");
        sb.AppendLine("- To DO anything, call a tool. Never claim you did something unless a tool did it.");
        sb.AppendLine("- If you are asked to do something you have no tool for, say so kindly and suggest something you can do.");
        sb.AppendLine("- Never be mean and never use bad words.");
        sb.AppendLine("- A friend saying hello, thanks or something friendly gets a friendly answer, not a job: only call a tool when you are asked to do something.");
        sb.AppendLine("- Before each message you get a note about what is going on around you. It is for you to know; never read it out or repeat its words.");
        foreach (var body in bodies)
            foreach (var rule in body.Rules) sb.Append("- ").AppendLine(rule);
        var others = Characters.All().Where(c => c.Name != Character.Name).Select(c => c.Name);
        sb.AppendLine($"- Other characters you can switch to: {string.Join(", ", others)}.");
        return sb.ToString();
    }

    /// <summary>
    /// What is going on right now, from every body. A note of its own just before the newest message, NOT in the system
    /// prompt: the system prompt plus the tool list is the long, unchanging start of every prompt, and the language model
    /// reuses it from the last turn only while it stays byte-identical.
    /// </summary>
    public string NowPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("What is going on around you (background for you, not something to say):");
        var any = false;
        foreach (var body in Bodies)
        {
            var now = body.DescribeNow();
            if (string.IsNullOrWhiteSpace(now)) continue;
            sb.Append(now.TrimEnd()).AppendLine();
            any = true;
        }
        if (!any) sb.AppendLine("- Nothing in particular.");
        return sb.ToString();
    }

    /// <summary>
    /// One turn: system prompt + history + a note on what is going on + this message, tools on. The reply is spoken
    /// sentence by sentence AS IT IS GENERATED, so Vertex starts talking while it is still thinking.
    /// </summary>
    /// <param name="replyChannel">Where to answer (null: every channel).</param>
    public async Task<string> RespondAsync(string userMessage, CancellationToken ct = default, bool allowTools = true, TurnClock? clock = null,
        IVoiceChannel? replyChannel = null)
    {
        clock ??= new TurnClock();
        Mouth.BeginReply();
        try { return await ReplyAsync(userMessage, ct, allowTools, clock, ReplyTo(replyChannel)); }
        finally { Mouth.EndReply(); }
    }

    private async Task<string> ReplyAsync(string userMessage, CancellationToken ct, bool allowTools, TurnClock clock, IReadOnlyList<IVoiceChannel> to)
    {
        var messages = new List<AiChatMessage> { new("system", SystemPrompt()) };
        lock (_history) messages.AddRange(_history);
        // The state is its own note just before the message. Glued to the front of the message, the model began its
        // replies with the note's label ("Right now: I'm on my way to Aubri", BrainTests 2026-10-12).
        messages.Add(new AiChatMessage("system", NowPrompt()));
        messages.Add(new AiChatMessage("user", userMessage));
        Log?.Invoke("> " + userMessage);
        var runsBefore = Volatile.Read(ref _toolRuns);
        var spoken = new List<string>();
        var reply = await ChatOnceAsync(messages, allowTools, ct, clock, spoken, to);
        // A question is answered, not acted on: "what's the best thing to build?" got "I'm on my way to helping you decide" and a retry that ordered a tool call.
        var askedAQuestion = IsRealQuestion(userMessage);
        // An 8B model sometimes SAYS it is doing something ("I'm on my way") without calling a tool, and then nothing
        // happens (seen 2026-10-11 in BrainTests.ComeHere). Once, tell it so and let it call the tool.
        if (allowTools && !askedAQuestion && Volatile.Read(ref _toolRuns) == runsBefore && (ClaimsAction(reply) || ClaimsInability(reply)))
        {
            Log?.Invoke("  (said it would act but called no tool: asking again)");
            messages.Add(new AiChatMessage("assistant", reply));
            messages.Add(new AiChatMessage("user", "[You said that, but you did not call any tool, so NOTHING happened. Call the right tool now, then answer in one short sentence.]"));
            // What it already said was heard; the second answer is only spoken when nothing was.
            reply = await ChatOnceAsync(messages, true, ct, clock, spoken.Count > 0 ? null : spoken, to);
        }
        // Nothing said and nothing done is no answer at all (seen 2026-10-11 for "remember that I love pink
        // axolotls"): once, tell it so.
        else if (allowTools && Volatile.Read(ref _toolRuns) == runsBefore && reply.Count(char.IsLetter) < 4)
        {
            Log?.Invoke("  (no answer and no tool: asking again)");
            messages.Add(new AiChatMessage("assistant", reply));
            messages.Add(new AiChatMessage("user", "[You gave no answer and called no tool. If you were asked to do something, call the right tool now; then answer in one short sentence.]"));
            reply = await ChatOnceAsync(messages, true, ct, clock, spoken.Count > 0 ? null : spoken, to);
        }
        // Still claiming to act after being asked again, with no tool called: nothing happened, and keeping the claim in the history teaches the model to claim without calling (seen
        // 2026-10-15: after one "I called trade. I'm walking to the villager." with no call, every later reply was a claim and no tool was ever called again).
        var falseClaim = allowTools && !askedAQuestion && Volatile.Read(ref _toolRuns) == runsBefore && ClaimsAction(reply);
        if (falseClaim) { Log?.Invoke("  (still no tool: not keeping what it said)"); reply = ""; }
        lock (_history)
        {
            _history.Add(new AiChatMessage("user", userMessage));
            _history.Add(new AiChatMessage("assistant", reply.Length > 0 ? reply : falseClaim ? "(I could not do that)" : "(did it)"));
            // Old exchanges leave in CHUNKS, not one per turn: the language model reuses the start of the prompt from
            // the last turn only while it is unchanged, and a window that slid every turn made it prefill the whole
            // history again each time (MEASURED 2026-10-12, AiSessionTests: reused 525 of 543 tokens, first word
            // 0.68 s, while the history grew; 26 of 569 and 2.4 s once it slid).
            if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory / 2);
        }
        // The end of the reply that was not a whole sentence while streaming (or all of it, when nothing streamed).
        var rest = Unspoken(reply, spoken);
        if (falseClaim) SayInTurn("Sorry, I'm not sure how to do that one.", clock, to);
        else if (rest.Count(char.IsLetter) >= 4 && !ChatTemplates.IsOnlyPythonicCalls(rest))
            SayInTurn(rest, clock, to);
        else if (spoken.Count == 0 && clock.TakeReport())
            Log?.Invoke("timing: " + clock.Describe(reply.Length > 0 ? "answered without speaking" : "acted without speaking"));
        return reply;
    }

    /// <summary>
    /// Speaks a line for the current turn; the turn's timing is logged when it is first heard. Stage directions
    /// ("*antennas perk up*") are never read out: a body that can act them out does.
    /// </summary>
    private void SayInTurn(string line, TurnClock clock, IReadOnlyList<IVoiceChannel> to)
    {
        var (spoken, actions) = SpokenText.Split(line);
        foreach (var action in actions)
            foreach (var body in Bodies) body.Act(action);
        if (spoken.Count(char.IsLetter) < 2) return;
        clock.FirstSentence();
        Mouth.Say(spoken, to, () =>
        {
            if (Volatile.Read(ref _turn) is { } speaking) speaking.Spoke = true;
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
    private async Task<string> ChatOnceAsync(List<AiChatMessage> messages, bool allowTools, CancellationToken ct, TurnClock clock,
        List<string>? spoken, IReadOnlyList<IVoiceChannel> to)
    {
        // A turn that must not act (telling friends how a job went) still carries the tool list: the language model reuses the start of the prompt from the last turn only while it is
        // unchanged, and a prompt WITHOUT the 4,000-token tool list made the next turn read all of it again (MEASURED 2026-10-15: first word 39 s and 29 s after such a turn). A call it
        // makes anyway comes back unexecuted (no rounds).
        var rounds = Ai.Chat.MaxToolRounds;
        if (!allowTools) Ai.Chat.MaxToolRounds = 0;
        var pending = new StringBuilder();
        var generated = new StringBuilder();
        var madeUpFriend = false;
        Task OnDelta(string delta)
        {
            clock.FirstToken();
            if (spoken == null) return Task.CompletedTask;
            // The model sometimes carries on the transcript and writes the friend's next line ("Todd says (out loud): ... a castle?") in the format the
            // prompt uses for them. Nothing from that point on is spoken, and it is cut from the reply (see SelfTalk).
            if (madeUpFriend) return Task.CompletedTask;
            generated.Append(delta);
            if (SelfTalk.IndexOfFriendsLine(generated.ToString()) is { } at)
            {
                madeUpFriend = true;
                var keep = generated.ToString(0, at);
                var spokenSoFar = string.Concat(spoken);
                pending.Clear();
                var tail = keep.Length > spokenSoFar.Length ? Unspoken(keep, spoken) : "";
                if (tail.Count(char.IsLetter) >= 2) pending.Append(tail + " ");
                Log?.Invoke("  (the model wrote the friend's next line; cut)");
            }
            else pending.Append(delta);
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
                    SayInTurn(sentence, clock, to);
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
                Options = new AiGenerationOptions { MaxOutputTokens = 220, Strategy = "top_p", Temperature = 0.7f, TopP = 0.9f, RepetitionPenalty = RepetitionPenalty },
            }, OnDelta, ct);
        }
        finally
        {
            Ai.Chat.MaxToolRounds = rounds;
            Ai.Chat.PerfLog = perf;
        }
        var reply = SelfTalk.CutFriendsLine(AiChatEngine.StripToolCalls(AiChatEngine.StripThinking(result.TextWithoutToolCalls)).Trim());
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
        "i called ", "i've called ", "i have called ", "i started ", "i'm walking ", "i'm trading", "i'm planting", "i'm building", "i'm feeding", "i'm picking", "i'm shearing", "i'm milking", "i'm lighting", "i'm shooting",
        "i'm collecting", "i'm gathering", "i'm mining", "i'm chopping", "i'm cutting", "i'm crafting", "i'm smelting", "i'm fetching", "i'm bringing", "i'm giving", "i'm eating", "i'm sleeping",
        "i'm exploring", "i'm guarding", "i'm attacking", "i'm fighting", "i'm storing", "i'm taking", "i'm placing", "i'm harvesting", "i'm breeding", "i'm staying", "i'm stopping",
        "i remember,", "i remember that", "i've remembered", "i have remembered", "i'll remember", "i will remember", "i'll keep that in mind", " noted",
    ];

    /// <summary>
    /// Whether a message asks something that is to be ANSWERED. "Can you milk the cow?" ends in a question mark but is a request (seen 2026-10-15: exempt as a question, the model said
    /// "I'm on my way to milk the cow" and called no tool, and nobody asked it again).
    /// </summary>
    public static bool IsRealQuestion(string message)
    {
        var m = message.Trim();
        if (!m.EndsWith('?')) return false;
        var t = " " + m.ToLowerInvariant().Replace('’', '\'') + " ";
        string[] requests = [" can you ", " could you ", " would you ", " will you ", " can u ", " could u ", " would u ", " please "];
        return !requests.Any(t.Contains);
    }

    /// <summary>"I'm putting torches around us", "I'm waiting for a sheep": any "I'm (something)ing" that is not a feeling (the phrase list above cannot name every verb; MEASURED 2026-10-15, ToolChoiceTests).</summary>
    private static readonly System.Text.RegularExpressions.Regex DoingSomething = new(
        @"\bi(?:'m| am| am now| am still) (?!feeling|doing (great|good|fine|well|okay)|having|glad|happy|so |just |not |thinking|looking forward|sorry|sure|afraid)[a-z]+ing\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string[] InabilityClaims =
    [
        "i don't have any", "i do not have any", "i don't see any", "i do not see any", "i can't find", "i couldn't find", "i need a ", "i need an ", "i'm not able", "i am not able",
        "i can't do that", "i cannot do that", "i don't know how",
    ];

    /// <summary>
    /// Whether a reply says Vertex cannot do what was asked, WITHOUT having tried. An 8B model answers "I don't have any sheep nearby to shear" from the memory of an earlier turn's
    /// result instead of calling the tool again (MEASURED 2026-10-15: shear and milk, two of the three tool-choice misses). The retry asks it to call the tool, which tells the truth;
    /// unlike <see cref="ClaimsAction"/> this is never treated as a false claim afterwards, because "I don't have a bow" can be exactly right.
    /// </summary>
    public static bool ClaimsInability(string reply)
    {
        var r = " " + reply.ToLowerInvariant().Replace('’', '\'') + " ";
        return InabilityClaims.Any(r.Contains);
    }

    /// <summary>Whether a reply says Vertex is doing (or about to do) something.</summary>
    public static bool ClaimsAction(string reply)
    {
        var r = " " + reply.ToLowerInvariant().Replace('’', '\'') + " ";
        return ActionClaims.Any(r.Contains) || DoingSomething.IsMatch(r);
    }

    // ---------------------------------------------------------------- characters

    public string SwitchCharacter(string name)
    {
        var c = Characters.Find(name);
        if (c == null) return $"no character called {name}; known: {string.Join(", ", Characters.All().Select(x => x.Name))}";
        Character = c;
        Mouth.VoiceId = c.Voice;
        lock (_history) _history.Clear(); // a new character starts a new conversation
        _events.Writer.TryWrite(new PrimeEvent()); // a new persona is a new system prompt: prefill it again
        Log?.Invoke($"now playing as {c.Name}");
        return $"you are now {c.Name}";
    }

    private int _disposed;

    /// <summary>
    /// While Vertex is thinking or talking, says every 10 s who held the GPU and for how long (the models share one lane, so a slow reply or a gap between sentences is time some
    /// other call held it). Quiet when nothing is going on.
    /// </summary>
    private async Task LaneReportLoopAsync(CancellationToken ct)
    {
        var mark = Environment.TickCount64;
        var wasActive = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var active = IsThinking || Mouth.IsSpeaking;
                var text = Ai.Lane.DescribeUsage(ref mark);
                if (active || wasActive) Log?.Invoke(text);
                wasActive = active;
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // a host tearing down twice must not throw
        _events.Writer.TryComplete();
        _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch (OperationCanceledException) { } }
        foreach (var ch in Channels) RemoveChannel(ch);
        await Mouth.DisposeAsync();
    }
}
