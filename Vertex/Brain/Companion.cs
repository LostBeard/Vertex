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
    public static Guid SpeakerId(string key) =>
        Guid.TryParse(key, out var g) ? g : new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    public void Start()
    {
        _events.Writer.TryWrite(new PrimeEvent());
        _loop = Task.Run(() => LoopAsync(_cts.Token));
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
        if (Volatile.Read(ref _turn) is { } turn && turn.Interruptible && IsAddressed(e) && (!turn.ForPerson || Interrupts(e.Text)))
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
        return NamesVertex(t)
            || t.Contains(" stop") || t.Contains(" wait") || t.Contains(" never mind") || t.Contains(" nevermind");
    }

    /// <summary>
    /// What Whisper writes when someone says "Vertex". MEASURED, never guessed from spelling (SpawnDev.Reachy's
    /// lesson): TJ said "Hi, Vertex" in game on 2026-10-12 and it came back "Hi, birdtakes".
    /// </summary>
    public static readonly string[] VertexHeardAs = ["vertex", "vortex", "birdtakes", "bird takes"];

    /// <summary>The line calls Vertex (or the current character) by name.</summary>
    private bool NamesVertex(string lowerText) =>
        lowerText.Contains(Character.Name.ToLowerInvariant()) || VertexHeardAs.Any(lowerText.Contains);

    /// <summary>
    /// Talk back when our name is said, when we are mid-conversation with this speaker (within 45 s), when the channel
    /// is one where everything said is said to Vertex (the robot's microphone), or when a body says so (in Minecraft:
    /// the only other player around, speaking out loud).
    /// </summary>
    public bool IsAddressed(SaidEvent e)
    {
        if (e.Speaker == Guid.Empty) return true; // the operator's console and the control panel talk only to us
        if (NamesVertex(e.Text.ToLowerInvariant())) return true;
        if (_lastPartner == e.Speaker && DateTime.UtcNow - _lastExchange < TimeSpan.FromSeconds(45)) return true;
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
            var turn = new Turn(cts, forPerson, interruptible: e is not PrimeEvent);
            var clock = e is SaidEvent heard ? new TurnClock(heard.HeardTick, heard.SttMs) : new TurnClock();
            Volatile.Write(ref _turn, turn);
            // Shown (the robot looks thoughtful) only for something Vertex will actually answer or do.
            var shows = e is not PrimeEvent && (e is not SaidEvent heardLine || IsAddressed(heardLine));
            if (shows) Thinking?.Invoke(true);
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
                await PrimeAsync(ct);
                return;
            case SaidEvent s when IsAddressed(s):
                CurrentSpeaker = s.SpeakerName;
                CurrentChannel = s.Channel;
                _lastPartner = s.Speaker;
                _lastExchange = DateTime.UtcNow;
                foreach (var body in Bodies) body.OnFriendSpoke(s);
                Mouth.Hush();
                // Where it was said only matters once Vertex is in more than one place.
                var how = s.Voice ? "out loud" : "in chat";
                var where = s.Channel != null && Channels.Count > 1 ? $" ({how}, {s.Channel.HeardThrough})" : $" ({how})";
                var said = $"{s.SpeakerName} says{where}: {s.Text}";
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
        }
        foreach (var body in Bodies)
            if (await body.HandleEventAsync(e, clock, ct)) return;
    }

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
        sb.AppendLine("- Speak as yourself in the first person (\"I\"), to your friends (\"you\"). Answer in one or two short spoken sentences, friendly and simple (a child may be listening). No lists, emojis or markdown.");
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
        // An 8B model sometimes SAYS it is doing something ("I'm on my way") without calling a tool, and then nothing
        // happens (seen 2026-10-11 in BrainTests.ComeHere). Once, tell it so and let it call the tool.
        if (allowTools && Volatile.Read(ref _toolRuns) == runsBefore && ClaimsAction(reply))
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

    /// <summary>Whether a reply says Vertex is doing (or about to do) something.</summary>
    public static bool ClaimsAction(string reply)
    {
        var r = " " + reply.ToLowerInvariant().Replace('’', '\'') + " ";
        return ActionClaims.Any(r.Contains);
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
