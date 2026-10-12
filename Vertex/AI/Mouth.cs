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
    private sealed record Clip(float[] Pcm, IReadOnlyList<IVoiceChannel> To, Action? OnStarted, int Epoch);

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
                    await _clips.Writer.WriteAsync(new Clip(samples, line.To, first ? line.OnStarted : null, line.Epoch), ct);
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
                await Task.WhenAll(speakers.Select(s => PlayOnAsync(s, clip.Pcm, cts.Token)));
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


    private async Task PlayOnAsync(IVoiceChannel channel, float[] pcm, CancellationToken ct)
    {
        try { await channel.PlayAsync(pcm, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Log?.Invoke($"playing on {channel.Name} failed: {e.Message}"); }
    }

    /// <summary>Splits text into sentences so speech can start before the whole reply is rendered.</summary>
    public static IEnumerable<string> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c is '.' or '!' or '?') && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                var s = text[start..(i + 1)].Trim();
                if (s.Length > 0) yield return s;
                start = i + 1;
            }
        }
        var rest = text[start..].Trim();
        if (rest.Length > 0) yield return rest;
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
