using SpawnDev.Minecraft;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.AI;

namespace Vertex.Core;

/// <summary>
/// A Minecraft world as a voice channel: players' proximity voice (Simple Voice Chat, when the server has it) heard
/// through Whisper, their chat lines, and Vertex's own speech played back into proximity voice and typed into chat.
/// </summary>
public sealed class MinecraftChannel : IVoiceChannel
{
    private readonly MinecraftClient _client;
    private readonly SimpleVoiceChat? _voice;

    public string Name => "Minecraft";
    public string HeardThrough => "in the game";
    /// <summary>The voice-chat ears (null without voice chat).</summary>
    public Ears? Ears { get; }
    /// <summary>Also type what is said into chat (so text-only players and logs see it).</summary>
    public bool EchoText { get; set; } = true;
    /// <summary>Loudness of Vertex's voice in game.</summary>
    public float Gain { get; set; } = 1.0f;
    public bool CanSpeak => _voice is { IsConnected: true };
    /// <summary>Simple Voice Chat never sends Vertex its own voice: nothing to mute.</summary>
    public bool Muted { get; set; }

    public event Action<HeardLine>? Heard;
    public event Action<string>? Log;

    public MinecraftChannel(MinecraftClient client, SimpleVoiceChat? voice, Func<float[], CancellationToken, Task<string>> transcribe)
    {
        _client = client;
        _voice = voice;
        if (voice != null)
        {
            Ears = new Ears(voice, transcribe);
            Ears.Log += m => Log?.Invoke("[ears] " + m);
            Ears.Heard += h => Heard?.Invoke(new HeardLine(this, h.Speaker.ToString(), NameOf(h.Speaker), h.Text, true, h.EndedTick, h.SttMs));
        }
        client.ChatReceived += OnChat;
    }

    private string NameOf(Guid id) => _client.Players.TryGetValue(id, out var p) ? p.Name : "someone";

    private void OnChat(ChatMessage m)
    {
        if (m.Kind != ChatKind.Player || m.SenderId == _client.Profile.Id || m.SenderId == null) return;
        Heard?.Invoke(new HeardLine(this, m.SenderId.Value.ToString(), m.SenderName ?? NameOf(m.SenderId.Value), m.Content, false, Environment.TickCount64, 0));
    }

    public async Task PlayAsync(float[] pcm24k, CancellationToken ct)
    {
        if (_voice is { IsConnected: true } voice) await voice.SpeakAsync(Audio.Upsample24To48(pcm24k, Gain), false, ct);
    }

    /// <summary>
    /// The least time between two chat lines Vertex sends. The server counts every chat line as 20 against a limit of 200 that sinks by one each SERVER tick, so about one line a second is the
    /// most a server at full speed allows for long, and a slower server (the test server beside a busy GPU) allows less: it kicked Vertex with "Kicked for spamming" in 5 of 7 runs once the
    /// model answered about 1.6 s sooner (2026-11-03), after 20 to 30 seconds of a line a second. 1.5 s is the margin.
    /// </summary>
    public TimeSpan MinChatGap { get; set; } = TimeSpan.FromSeconds(1.5);
    /// <summary>A chat line still waiting after this long is dropped: a late echo of something said a minute ago is worse than none.</summary>
    public TimeSpan ChatLineExpires { get; set; } = TimeSpan.FromSeconds(30);

    private readonly System.Threading.Channels.Channel<(string Text, long At)> _chat = System.Threading.Channels.Channel.CreateUnbounded<(string, long)>();
    private Task? _chatPump;
    private readonly object _pumpLock = new();
    private readonly CancellationTokenSource _stop = new();

    /// <summary>
    /// Types the words into chat, paced: lines are queued here and sent by a background loop at least <see cref="MinChatGap"/> apart, so this returns at once and never holds up the voice.
    /// </summary>
    public Task ShowTextAsync(string text, CancellationToken ct)
    {
        if (!EchoText || _client.State != ConnectionState.Play) return Task.CompletedTask;
        lock (_pumpLock) _chatPump ??= Task.Run(PumpChatAsync);
        foreach (var chunk in Mouth.ChatChunks(text)) _chat.Writer.TryWrite((chunk, Environment.TickCount64));
        return Task.CompletedTask;
    }

    private async Task PumpChatAsync()
    {
        var last = long.MinValue / 2;
        try
        {
            await foreach (var (chunk, at) in _chat.Reader.ReadAllAsync(_stop.Token))
            {
                if (Environment.TickCount64 - at > ChatLineExpires.TotalMilliseconds || _client.State != ConnectionState.Play) continue;
                var wait = last + (long)MinChatGap.TotalMilliseconds - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)wait, _stop.Token);
                try
                {
                    // SayAsync, never SendChatAsync: what Vertex says comes from a language model (and from players asking it to repeat things), and a chunk that began with "/" would run as a
                    // server command with this account's permissions. A slash is only a character here.
                    await _client.SayAsync(chunk, _stop.Token);
                }
                catch (Exception e) when (e is not OperationCanceledException) { Log?.Invoke("chat line not sent: " + e.Message); }
                last = Environment.TickCount64;
            }
        }
        catch (OperationCanceledException) { }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _chat.Writer.TryComplete();
        _client.ChatReceived -= OnChat;
        Ears?.Dispose();
        return ValueTask.CompletedTask;
    }
}
