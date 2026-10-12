using System.Threading.Channels;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.VoiceChat;

namespace Vertex.AI;

/// <summary>
/// Vertex's voice: says lines one after another in proximity voice (Kokoro on the GPU, 24 kHz up to 48 kHz,
/// streamed sentence by sentence so speech starts after the first sentence), or in text chat when voice chat is
/// not available on the server.
/// </summary>
public sealed class Mouth : IAsyncDisposable
{
    private readonly MinecraftClient _client;
    private readonly SimpleVoiceChat? _voice;
    private readonly Func<string, string, CancellationToken, Task<(float[] Samples, int SampleRate)>> _tts;
    private readonly Channel<(string Text, Action? OnStarted)> _queue = Channel.CreateUnbounded<(string, Action?)>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    // The line being said right now; Hush cuts it off.
    private CancellationTokenSource? _line;

    /// <summary>The Kokoro voice ("kokoro:af_heart"...).</summary>
    public string VoiceId { get; set; } = "kokoro:af_heart";
    /// <summary>Also type what is said into chat (so text-only players and logs see it).</summary>
    public bool EchoToChat { get; set; } = true;
    public float Gain { get; set; } = 1.0f;
    public bool IsSpeaking { get; private set; }

    public event Action<string>? Log;

    public Mouth(MinecraftClient client, SimpleVoiceChat? voice, Func<string, string, CancellationToken, Task<(float[] Samples, int SampleRate)>> tts)
    {
        _client = client;
        _voice = voice;
        _tts = tts;
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>Queues a line to say.</summary>
    /// <param name="onStarted">Called when the line starts to be heard (or typed, without voice): a turn's clock stops there.</param>
    public void Say(string text, Action? onStarted = null)
    {
        if (!string.IsNullOrWhiteSpace(text)) _queue.Writer.TryWrite((text.Trim(), onStarted));
    }

    /// <summary>Stops talking: drops what is not yet said and cuts off the line being said (being interrupted).</summary>
    public void Hush()
    {
        while (_queue.Reader.TryRead(out _)) { }
        try { Volatile.Read(ref _line)?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task WorkAsync()
    {
        await foreach (var (line, onStarted) in _queue.Reader.ReadAllAsync(_cts.Token))
        {
            var started = onStarted;
            void Started() { started?.Invoke(); started = null; }
            using var lineCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            Volatile.Write(ref _line, lineCts);
            var ct = lineCts.Token;
            try
            {
                IsSpeaking = true;
                if (EchoToChat && _client.State == ConnectionState.Play)
                    foreach (var chunk in ChatChunks(line)) await _client.SendChatAsync(chunk, ct);
                if (_voice is { IsConnected: true })
                    foreach (var sentence in Sentences(line))
                    {
                        var (samples, rate) = await _tts(sentence, VoiceId, ct);
                        if (rate != 24000) throw new InvalidOperationException($"unexpected TTS rate {rate}");
                        Started();
                        await _voice.SpeakAsync(Audio.Upsample24To48(samples, Gain), false, ct);
                    }
                else Started(); // text only: the line is out as soon as it is typed
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { Log?.Invoke("stopped talking (interrupted)"); }
            catch (Exception e)
            {
                Log?.Invoke("speaking failed: " + e.Message);
            }
            finally
            {
                Interlocked.CompareExchange(ref _line, null, lineCts);
                IsSpeaking = false;
            }
        }
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

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { await _worker; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
