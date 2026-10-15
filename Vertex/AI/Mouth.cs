using System.Threading.Channels;
using Vertex.Core;

namespace Vertex.AI;

/// <summary>
/// Vertex's voice: says lines one after another into its voice channels (proximity voice in a game, the robot's
/// speaker), as Kokoro speech on the GPU, and shows the words where a channel shows text (game chat).
/// </summary>
/// <remarks>
/// Two stages: sentences are synthesised AHEAD while the one before is still playing, and played strictly in order.
/// Synthesising one sentence only after the last finished playing left a gap of a whole synthesis between sentences;
/// playing without waiting cuts the robot's last clip off (its play_sound only queues, SpawnDev.Reachy CLAUDE.md).
/// </remarks>
public sealed class Mouth : IAsyncDisposable
{
    private readonly Func<string, string, CancellationToken, Task<(float[] Samples, int SampleRate)>> _tts;
    private readonly Channel<Line> _lines = Channel.CreateUnbounded<Line>();
    // At most two sentences rendered ahead: enough to hide synthesis, little to throw away when interrupted.
    private readonly Channel<Clip> _clips = Channel.CreateBounded<Clip>(2);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _render, _play;
    // Lines from before the last Hush carry an older epoch and are dropped.
    private int _epoch;
    // Hush cuts off what is being rendered and what is being played.
    private CancellationTokenSource _hush = new();
    // Ears closed from the first clip of a reply until the whole reply is out (Rose: mute for the whole reply, not per
    // sentence; between two streamed sentences nothing plays for a moment, and the room is heard again there).
    private readonly object _earsLock = new();
    private readonly HashSet<IVoiceChannel> _muted = [];
    private int _openReplies, _rendering, _playing;

    private sealed record Line(string Text, IReadOnlyList<IVoiceChannel> To, Action? OnStarted, int Epoch);
    private sealed record Clip(float[] Pcm, IReadOnlyList<IVoiceChannel> To, Action? OnStarted, int Epoch, IReadOnlyDictionary<IVoiceChannel, Task<object>>? Prepared = null);

    /// <summary>The Kokoro voice ("kokoro:af_heart"...).</summary>
    public string VoiceId { get; set; } = "kokoro:af_heart";
    /// <summary>True while a line is being heard on some channel.</summary>
    public bool IsSpeaking => Volatile.Read(ref _playing) > 0;

    public event Action<string>? Log;

    public Mouth(Func<string, string, CancellationToken, Task<(float[] Samples, int SampleRate)>> tts)
    {
        _tts = tts;
        _render = Task.Run(RenderAsync);
        _play = Task.Run(PlayAsync);
    }

    /// <summary>Queues a line to say on the given channels.</summary>
    /// <param name="onStarted">Called when the line starts to be heard (or shown, where no channel can speak): a turn's clock stops there.</param>
    public void Say(string text, IReadOnlyList<IVoiceChannel> to, Action? onStarted = null)
    {
        if (!string.IsNullOrWhiteSpace(text) && to.Count > 0) _lines.Writer.TryWrite(new Line(text.Trim(), to, onStarted, Volatile.Read(ref _epoch)));
    }

    /// <summary>A reply is being generated: ears that close for it stay closed until <see cref="EndReply"/> and the last clip.</summary>
    public void BeginReply() => Interlocked.Increment(ref _openReplies);

    /// <summary>The reply has all been generated (what is queued still plays).</summary>
    public void EndReply()
    {
        Interlocked.Decrement(ref _openReplies);
        OpenEarsIfDone();
    }

    /// <summary>Stops talking: drops what is not yet said and cuts off the line being said (being interrupted).</summary>
    public void Hush()
    {
        Interlocked.Increment(ref _epoch);
        var old = Interlocked.Exchange(ref _hush, new CancellationTokenSource());
        try { old.Cancel(); } catch (ObjectDisposedException) { }
        while (_lines.Reader.TryRead(out _)) { }
        while (_clips.Reader.TryRead(out _)) { }
        OpenEarsIfDone();
    }

    private bool Stale(int epoch) => epoch != Volatile.Read(ref _epoch);

    /// <summary>Opens the ears closed for speaking once no reply is open and nothing is left to render or play.</summary>
    private void OpenEarsIfDone()
    {
        lock (_earsLock)
        {
            if (Volatile.Read(ref _openReplies) > 0 || Volatile.Read(ref _rendering) > 0 || Volatile.Read(ref _playing) > 0
                || _lines.Reader.Count > 0 || _clips.Reader.Count > 0) return;
            foreach (var s in _muted) s.Muted = false;
            _muted.Clear();
        }
    }

    private void CloseEars(IEnumerable<IVoiceChannel> speakers)
    {
        lock (_earsLock)
            foreach (var s in speakers) { s.Muted = true; _muted.Add(s); }
    }

    private async Task RenderAsync()
    {
        await foreach (var line in _lines.Reader.ReadAllAsync(_cts.Token))
        {
            if (Stale(line.Epoch)) continue;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, Volatile.Read(ref _hush).Token);
            var ct = cts.Token;
            Interlocked.Increment(ref _rendering);
            try
            {
                foreach (var channel in line.To)
                    try { await channel.ShowTextAsync(line.Text, ct); }
                    catch (Exception e) when (e is not OperationCanceledException) { Log?.Invoke($"showing text on {channel.Name} failed: {e.Message}"); }
                if (!line.To.Any(c => c.CanSpeak))
                {
                    line.OnStarted?.Invoke(); // text only: the line is out as soon as it is shown
                    continue;
                }
                var first = true;
                foreach (var sentence in Sentences(line.Text))
                {
                    var (samples, rate) = await _tts(sentence, VoiceId, ct);
                    if (rate != 24000) throw new InvalidOperationException($"unexpected TTS rate {rate}");
                    if (Stale(line.Epoch)) break;
                    // A channel that needs time to get a clip ready (the robot uploads it) starts on it NOW, while the clip before is still playing.
                    Dictionary<IVoiceChannel, Task<object>>? prepared = null;
                    foreach (var channel in line.To.Where(c => c.CanSpeak).OfType<IPreparingVoiceChannel>())
                        (prepared ??= [])[channel] = channel.PrepareAsync(samples, ct);
                    await _clips.Writer.WriteAsync(new Clip(samples, line.To, first ? line.OnStarted : null, line.Epoch, prepared), ct);
                    first = false;
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log?.Invoke("speaking failed: " + e.Message); }
            finally
            {
                Interlocked.Decrement(ref _rendering);
                OpenEarsIfDone();
            }
        }
    }

    private async Task PlayAsync()
    {
        await foreach (var clip in _clips.Reader.ReadAllAsync(_cts.Token))
        {
            if (Stale(clip.Epoch)) continue;
            var speakers = clip.To.Where(c => c.CanSpeak).ToList();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, Volatile.Read(ref _hush).Token);
            Interlocked.Increment(ref _playing);
            CloseEars(speakers);
            try
            {
                clip.OnStarted?.Invoke();
                await Task.WhenAll(speakers.Select(s => PlayOnAsync(s, clip.Pcm, clip.Prepared?.GetValueOrDefault(s), cts.Token)));
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { Log?.Invoke("stopped talking (interrupted)"); }
            finally
            {
                Interlocked.Decrement(ref _playing);
                OpenEarsIfDone();
            }
        }
    }


    private async Task PlayOnAsync(IVoiceChannel channel, float[] pcm, Task<object>? prepared, CancellationToken ct)
    {
        try
        {
            if (prepared != null && channel is IPreparingVoiceChannel preparing) await preparing.PlayPreparedAsync(await prepared, ct);
            else await channel.PlayAsync(pcm, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log?.Invoke($"playing on {channel.Name} failed: {e.Message}"); }
    }

    /// <summary>
    /// The most characters one spoken clip holds (about 7 s of speech). A longer sentence is spoken in pieces cut at a comma or a joining word: a clip is rendered, sent to the robot
    /// and played as a unit, so a 14 s clip (live 2026-10-15: a sentence listing three biomes) could not be ready, 6 s of transfer included, when the clip before it ended, and the
    /// speaker sat silent for 11 s, while the same words as two 7 s clips are rendered and sent while the one before plays.
    /// </summary>
    public const int MaxClipChars = 120;

    /// <summary>Splits text into sentences so speech can start before the whole reply is rendered.</summary>
    public static IEnumerable<string> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c is '.' or '!' or '?') && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                foreach (var piece in Pieces(text[start..(i + 1)].Trim())) yield return piece;
                start = i + 1;
            }
        }
        foreach (var piece in Pieces(text[start..].Trim())) yield return piece;
    }

    private static readonly string[] ClauseBreaks = [", ", "; ", ": ", " - ", " — "];
    private static readonly string[] JoiningWords = [" and ", " but ", " or ", " so ", " because ", " which ", " where ", " while ", " when ", " then "];

    /// <summary>A sentence, or (when it is longer than <see cref="MaxClipChars"/>) pieces of it of about equal length, cut near the middle at the best break.</summary>
    internal static IEnumerable<string> Pieces(string sentence)
    {
        if (sentence.Length == 0) yield break;
        if (sentence.Length <= MaxClipChars) { yield return sentence; yield break; }
        var cut = AvoidDanglingWord(sentence, BestCut(sentence));
        foreach (var piece in Pieces(sentence[..cut].Trim())) yield return piece;
        foreach (var piece in Pieces(sentence[cut..].Trim())) yield return piece;
    }

    // Where to cut: a comma-like break nearest the middle (within the middle 40%), else a joining word (the cut goes BEFORE it), else the space nearest the middle.
    private static int BestCut(string s)
    {
        int mid = s.Length / 2, lo = s.Length * 3 / 10, hi = s.Length * 7 / 10;
        int Nearest(IEnumerable<int> positions) => positions.Where(p => p >= lo && p <= hi).OrderBy(p => Math.Abs(p - mid)).DefaultIfEmpty(-1).First();
        var clause = Nearest(ClauseBreaks.SelectMany(b => AllIndexes(s, b).Select(i => i + b.TrimEnd().Length)));
        if (clause > 0) return clause;
        var joining = Nearest(JoiningWords.SelectMany(w => AllIndexes(s, w)));
        if (joining > 0) return joining;
        var space = Nearest(AllIndexes(s, " "));
        return space > 0 ? space : mid;
    }

    // A clip must not end on a word that wants the next one ("... can find and | then dig ..."): the cut moves back to before it.
    private static readonly HashSet<string> Dangling = new(StringComparer.OrdinalIgnoreCase)
        { "and", "but", "or", "so", "then", "because", "which", "where", "while", "when", "the", "a", "an", "to", "of", "with", "in", "on", "at", "for", "that", "if" };

    private static int AvoidDanglingWord(string s, int cut)
    {
        var floor = s.Length / 4;                                          // never make a stub of the first piece
        while (cut > floor)
        {
            var left = s[..cut].TrimEnd();
            var space = left.LastIndexOf(' ');
            if (space < 0 || !Dangling.Contains(left[(space + 1)..].Trim(',', ';', ':'))) break;
            if (left[^1] is ',' or ';' or ':') break;                      // a comma already gives it a place to stop
            cut = space;
        }
        return cut;
    }

    private static IEnumerable<int> AllIndexes(string s, string what)
    {
        for (var i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, StringComparison.Ordinal)) yield return i;
    }

    /// <summary>Chat lines are at most 256 characters: split on word boundaries.</summary>
    public static IEnumerable<string> ChatChunks(string text, int max = 250)
    {
        text = text.Replace('\n', ' ');
        while (text.Length > max)
        {
            var cut = text.LastIndexOf(' ', max);
            if (cut <= 0) cut = max;
            yield return text[..cut];
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // a host tearing down twice must not throw
        _lines.Writer.TryComplete();
        _cts.Cancel();
        try { await _render; } catch (OperationCanceledException) { }
        _clips.Writer.TryComplete();
        try { await _play; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
