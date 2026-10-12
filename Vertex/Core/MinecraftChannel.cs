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

    public async Task ShowTextAsync(string text, CancellationToken ct)
    {
        if (!EchoText || _client.State != ConnectionState.Play) return;
        foreach (var chunk in Mouth.ChatChunks(text)) await _client.SendChatAsync(chunk, ct);
    }

    public ValueTask DisposeAsync()
    {
        _client.ChatReceived -= OnChat;
        Ears?.Dispose();
        return ValueTask.CompletedTask;
    }
}
