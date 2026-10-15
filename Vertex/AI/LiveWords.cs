using System.Threading.Channels;
using SpawnDev.AI.Server;
using SpawnDev.ILGPU.ML.Pipelines;

namespace Vertex.AI;

/// <summary>
/// The words of what someone is saying, AS they say it: streaming speech to text (Nemotron, see <see cref="AiStreamingSpeechEngine"/>) run over the audio of the
/// utterance in progress. This is the fast pass; the finished utterance is still transcribed by Parakeet (more accurate), and these words are what let the rest of
/// Vertex react EARLY - show what it hears while it is heard, notice its name, see a sentence stop partway.
/// </summary>
/// <remarks>
/// <para>
/// The microphone pump must never wait on the GPU (the voice detector has to hear the next sentence while the language model prefills), so audio goes through a queue
/// to a worker that takes the GPU lane for one 160 ms chunk at a time (urgent, like the transcription it precedes). Chunks run faster than the speech they cover
/// (67 ms per 160 ms measured), so a queue built up behind a reply being prefilled drains within a second or two of the lane freeing.
/// </para>
/// <para>
/// A stream lasts a whole TURN, pauses included: it starts when the voice detector hears speech (with the half second before it, which the detector needs time to be sure
/// of), keeps receiving audio through a mid-sentence pause, and ends when the turn does (<see cref="EndTurn"/>). No flush at the end: the silence that closed the turn
/// has already gone through, and a flush would hold the lane against the transcription that follows.
/// </para>
/// </remarks>
public sealed class LiveWords : IAsyncDisposable
{
    /// <summary>Whether the log gets the words themselves (see <see cref="WordsInLog"/>). The live caption is a separate, transient thing and is not affected.</summary>
    public bool LogWords { get; set; }

    /// <summary>Audio kept from before the detector was sure it was speech (16 kHz samples): the first word starts before the detector says so.</summary>
    public const int PrerollSamples = 8000;

    /// <summary>
    /// Audio kept at the end of a turn for the next one: the voice detector closes a segment after 0.25 s of silence and the end-of-turn model takes a moment, so a person who
    /// goes straight on has already started the next sentence when the turn is called over. Live (a test with two sentences in a row), the second stream began at "would like it"
    /// and the "I" went to the first.
    /// </summary>
    public const int CarrySamples = 4800;

    /// <summary>Silence (16 kHz samples) with no turn waiting after which a stream is dropped: the detector opened on a sound that never became a sentence.</summary>
    public const int IdleSamples = 3 * 16000;

    private readonly VertexAi _ai;
    private readonly Channel<Message> _queue = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    // Producer side (the microphone pump's thread only).
    private readonly float[] _preroll = new float[PrerollSamples];
    private int _prerollCount;
    private bool _open;
    private long _silentSamples;

    private int _inFlight;

    private readonly record struct Message(float[]? Audio);

    /// <summary>The words so far in the utterance (and whether it is over). Raised from the worker; handlers must not block.</summary>
    public event Action<string, bool>? Words;
    public event Action<string>? Log;

    public LiveWords(VertexAi ai)
    {
        _ai = ai;
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>True while audio is queued or a stream is open and being worked on (tests wait on it).</summary>
    public bool IsBusy => Volatile.Read(ref _inFlight) > 0;

    /// <summary>
    /// Hands over the next piece of microphone audio. <paramref name="active"/>: the voice detector hears speech, or a turn is waiting for its continuation.
    /// Never blocks.
    /// </summary>
    public void Feed(float[] samples, bool active)
    {
        if (!_open)
        {
            if (!active)
            {
                Remember(samples);
                return;
            }
            _open = true;
            _silentSamples = 0;
            var first = new float[_prerollCount + samples.Length];
            Array.Copy(_preroll, 0, first, 0, _prerollCount);
            Array.Copy(samples, 0, first, _prerollCount, samples.Length);
            _prerollCount = 0;
            Enqueue(first);
            return;
        }
        Enqueue(samples);
        Remember(samples);   // so the tail is there for the next turn
        _silentSamples = active ? 0 : _silentSamples + samples.Length;
        if (_silentSamples >= IdleSamples) EndTurn();
    }

    /// <summary>The utterance is over (the turn completed, was thrown away, or the ears closed): finish the stream. Safe to call when none is open.</summary>
    public void EndTurn()
    {
        if (!_open) return;
        _open = false;
        _silentSamples = 0;
        if (_prerollCount > CarrySamples)
        {
            Array.Copy(_preroll, _prerollCount - CarrySamples, _preroll, 0, CarrySamples);
            _prerollCount = CarrySamples;
        }
        Interlocked.Increment(ref _inFlight);
        _queue.Writer.TryWrite(new Message(null));
    }

    private void Enqueue(float[] audio)
    {
        Interlocked.Increment(ref _inFlight);
        _queue.Writer.TryWrite(new Message(audio));
    }

    private void Remember(float[] samples)
    {
        if (samples.Length >= PrerollSamples)
        {
            Array.Copy(samples, samples.Length - PrerollSamples, _preroll, 0, PrerollSamples);
            _prerollCount = PrerollSamples;
            return;
        }
        var keep = Math.Min(_prerollCount, PrerollSamples - samples.Length);
        Array.Copy(_preroll, _prerollCount - keep, _preroll, 0, keep);
        Array.Copy(samples, 0, _preroll, keep, samples.Length);
        _prerollCount = keep + samples.Length;
    }

    private async Task WorkAsync()
    {
        var ct = _cts.Token;
        AiSpeechStream? stream = null;
        string last = "";
        double chunkMs = 0; int chunks = 0;
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    if (message.Audio is { } audio)
                    {
                        stream ??= await _ai.Streaming.BeginAsync(ct);
                        var update = await _ai.LiveAcceptAsync(stream, audio, ct);
                        chunkMs += update.ProcessingMs; chunks += update.ChunksRun;
                        if (update.NewTokens.Count > 0 && update.Text != last)
                        {
                            last = update.Text;
                            Words?.Invoke(last, false);
                        }
                    }
                    else if (stream != null)
                    {
                        var text = stream.Text;
                        stream.Dispose(); stream = null;
                        if (text.Length > 0)
                        {
                            Log?.Invoke($"live words: {WordsInLog.Quote(text, LogWords)} ({chunks} chunks, {(chunks > 0 ? chunkMs / chunks : 0):0} ms each)");
                            Words?.Invoke(text, true);
                        }
                        last = ""; chunkMs = 0; chunks = 0;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    // Live words are an extra: a failure here never costs the person their answer. Drop the stream and carry on with the next utterance.
                    Log?.Invoke("live words failed: " + e.Message);
                    try { stream?.Dispose(); } catch { }
                    stream = null; last = ""; chunkMs = 0; chunks = 0;
                }
                finally { Interlocked.Decrement(ref _inFlight); }
            }
        }
        catch (OperationCanceledException) { }
        finally { try { stream?.Dispose(); } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try { await _worker; } catch { }
        _cts.Dispose();
    }
}
