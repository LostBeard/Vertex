using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Concentus;
using Concentus.Enums;
using SpawnDev.Minecraft.Protocol;

namespace SpawnDev.Minecraft.VoiceChat;

public enum VoiceSoundKind { Player, Group, Location }

/// <summary>
/// 20 ms of decoded speech (48 kHz mono PCM, 960 samples) from one speaker. An empty Pcm means the speaker
/// stopped (their client sent its stop packet).
/// </summary>
public sealed record VoiceFrame(Guid Sender, VoiceSoundKind Kind, short[] Pcm, long Sequence, bool Whispering, float Distance, (double X, double Y, double Z)? Location);

/// <summary>A player's voice state as the server announces it.</summary>
public sealed record VoicePlayerState(Guid Id, string Name, bool Disabled, bool Disconnected, Guid? Group);

/// <summary>
/// A client for Simple Voice Chat (henkelmax; protocol compatibility version 20 = mod/plugin 2.6.x), the proximity
/// voice mod AubsCraft runs. Built from the 1.21.5 branch's source:
///   TCP: register the voicechat:* channels, send voicechat:request_secret(int compatibility), receive
///        voicechat:secret (secret, UDP port, our UUID, codec, MTU, distance, keep-alive, groups, host, recording).
///   UDP: client->server = 0xFF, our UUID, VarInt-length AES-GCM payload; server->client = 0xFF, payload.
///        payload = 12-byte IV + AES-128-GCM(type byte + packet), 128-bit tag. Authenticate (repeat 1/s until
///        acked), then ConnectionCheck (repeat until acked); answer KeepAlive and Ping.
///   Audio: Opus 48 kHz mono 20 ms frames in MicPacket (data, sequence, whispering); an empty MicPacket = stop.
/// Nothing is installed on the server: this only talks to a server that already runs Simple Voice Chat.
/// </summary>
public sealed class SimpleVoiceChat : IAsyncDisposable
{
    public const int CompatibilityVersion = 20;
    public const int SampleRate = 48000;
    public const int FrameSize = 960;
    private const byte Magic = 0xFF;

    private readonly MinecraftClient _client;
    private readonly ConcurrentDictionary<Guid, IOpusDecoder> _decoders = new();
    private readonly ConcurrentDictionary<Guid, VoicePlayerState> _states = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Socket? _udp;
    private IPEndPoint? _endpoint;
    private AesGcm? _aes;
    private Guid _playerId;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IOpusEncoder? _encoder;
    private long _sequence;
    private long _lastKeepAliveMs;
    private string _serverHost = "";

    public SimpleVoiceChat(MinecraftClient client, string serverHost)
    {
        _client = client;
        _serverHost = serverHost;
        _client.PluginMessage += OnPluginMessage;
        _client.Spawned += OnSpawned;
    }

    /// <summary>True once the UDP voice connection is authenticated and checked.</summary>
    public bool IsConnected { get; private set; }
    /// <summary>The secret packet said the server is incompatible / never answered: voice unavailable.</summary>
    public string? Status { get; private set; } = "waiting for the server";
    public double VoiceDistance { get; private set; }
    public int KeepAliveMs { get; private set; }
    public string Codec { get; private set; } = "";
    public bool GroupsEnabled { get; private set; }

    public IReadOnlyDictionary<Guid, VoicePlayerState> PlayerStates => _states;

    /// <summary>Decoded speech, raised on the UDP receive loop (keep handlers short).</summary>
    public event Action<VoiceFrame>? AudioReceived;
    public event Action? Connected;
    public event Action<string>? Log;

    /// <summary>Completes when the voice connection is up (or throws if the server refuses).</summary>
    public Task WaitConnectedAsync(CancellationToken cancellationToken = default) => _connected.Task.WaitAsync(cancellationToken);

    private void Say(string m) => Log?.Invoke(m);

    // ---------------------------------------------------------------- TCP side (plugin channels)

    private void OnSpawned()
    {
        // Each join (and each proxy server switch) needs a fresh secret: register, then ask.
        _ = Task.Run(async () =>
        {
            try
            {
                var channels = Encoding.UTF8.GetBytes(string.Join('\0', "voicechat:request_secret", "voicechat:secret", "voicechat:update_state",
                    "voicechat:state", "voicechat:states", "voicechat:remove_state", "voicechat:add_group", "voicechat:remove_group",
                    "voicechat:joined_group", "voicechat:add_category", "voicechat:remove_category", "voicechat:set_group", "voicechat:create_group", "voicechat:leave_group"));
                await _client.SendPluginMessageAsync("minecraft:register", channels);
                var request = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(request, CompatibilityVersion);
                await _client.SendPluginMessageAsync("voicechat:request_secret", request);
                Say("requested voice secret");
            }
            catch (Exception e)
            {
                Say("voice request failed: " + e.Message);
            }
        });
    }

    private void OnPluginMessage(string channel, ReadOnlyMemory<byte> data)
    {
        switch (channel)
        {
            case "voicechat:secret":
                OnSecret(data);
                break;
            case "voicechat:state":
            {
                var r = new PacketReader(data.Span);
                var s = ReadState(ref r);
                _states[s.Id] = s;
                break;
            }
            case "voicechat:states":
            {
                var r = new PacketReader(data.Span);
                var count = r.ReadInt();
                for (var i = 0; i < count; i++)
                {
                    var s = ReadState(ref r);
                    _states[s.Id] = s;
                }
                break;
            }
            case "voicechat:remove_state":
            {
                var r = new PacketReader(data.Span);
                _states.TryRemove(r.ReadUuid(), out _);
                break;
            }
        }
    }

    private static VoicePlayerState ReadState(ref PacketReader r)
    {
        var disabled = r.ReadBool();
        var disconnected = r.ReadBool();
        var id = r.ReadUuid();
        var name = r.ReadString();
        Guid? group = r.ReadBool() ? r.ReadUuid() : null;
        return new VoicePlayerState(id, name, disabled, disconnected, group);
    }

    private void OnSecret(ReadOnlyMemory<byte> data)
    {
        byte[] secret;
        int port, mtu, keepAlive;
        string host;
        {
            var r = new PacketReader(data.Span);
            secret = r.ReadBytes(16).ToArray();
            port = r.ReadInt();
            _playerId = r.ReadUuid();
            var codec = r.ReadByte();
            mtu = r.ReadInt();
            VoiceDistance = r.ReadDouble();
            keepAlive = r.ReadInt();
            GroupsEnabled = r.ReadBool();
            host = r.ReadString();
            r.ReadBool(); // allow recording
            Codec = codec switch { 0 => "VOIP", 1 => "AUDIO", 2 => "RESTRICTED_LOWDELAY", _ => "unknown" };
        }
        KeepAliveMs = keepAlive;
        var (ip, udpPort) = ParseHost(host, _serverHost, port);
        Say($"voice secret received: {ip}:{udpPort}, codec {Codec}, distance {VoiceDistance}, keep-alive {keepAlive} ms");
        _ = Task.Run(() => ConnectUdpAsync(secret, ip, udpPort));
    }

    /// <summary>InitializationData.parseAddress: empty = the game host; a number = port; else host[:port].</summary>
    internal static (string Host, int Port) ParseHost(string voiceHost, string serverHost, int serverPort)
    {
        if (voiceHost.Length == 0) return (serverHost, serverPort);
        if (int.TryParse(voiceHost, out var p)) return (serverHost, p is > 0 and <= 65535 ? p : serverPort);
        if (Uri.TryCreate("voicechat://" + voiceHost, UriKind.Absolute, out var uri))
            return (string.IsNullOrEmpty(uri.Host) ? serverHost : uri.Host, uri.Port > 0 ? uri.Port : serverPort);
        return (serverHost, serverPort);
    }

    // ---------------------------------------------------------------- UDP side

    private async Task ConnectUdpAsync(byte[] secret, string host, int port)
    {
        await CloseUdpAsync();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
            _endpoint = new IPEndPoint(address, port);
            _udp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _udp.Connect(_endpoint);
            _aes = new AesGcm(secret, 16);
            _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, Codec switch
            {
                "AUDIO" => OpusApplication.OPUS_APPLICATION_AUDIO,
                "RESTRICTED_LOWDELAY" => OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY,
                _ => OpusApplication.OPUS_APPLICATION_VOIP,
            });
            var authenticated = false;
            var receive = Task.Run(() => ReceiveLoopAsync(() => authenticated = true, ct), ct);

            // AuthThread: Authenticate once a second until acknowledged, then ConnectionCheck until acknowledged.
            for (var i = 0; i < 30 && !ct.IsCancellationRequested && !IsConnected; i++)
            {
                if (!authenticated)
                {
                    var auth = new byte[32];
                    _playerId.TryWriteBytes(auth, bigEndian: true, out _);
                    secret.CopyTo(auth, 16);
                    await SendAsync(0x05, auth, ct);
                }
                else await SendAsync(0x09, [], ct);
                await Task.WhenAny(Task.Delay(1000, ct), _connected.Task);
            }
            if (!IsConnected)
            {
                Status = "voice server did not answer (UDP " + _endpoint + ")";
                Say(Status);
                _connected.TrySetException(new TimeoutException(Status));
                return;
            }
            // We are not disabled (the vanilla client sends this when the player toggles voice).
            await _client.SendPluginMessageAsync("voicechat:update_state", new byte[] { 0 }, ct);
            await receive;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Status = "voice error: " + e.Message;
            Say(Status);
            _connected.TrySetException(e);
        }
    }

    private async Task ReceiveLoopAsync(Action onAuthenticated, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var plain = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await _udp!.ReceiveAsync(buffer, SocketFlags.None, ct); }
            catch (OperationCanceledException) { return; }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; } // ICMP port unreachable while the server starts
            if (n < 2 || buffer[0] != Magic) continue;
            var r = new PacketReader(buffer.AsSpan(1, n - 1));
            var payload = r.ReadByteArray();
            if (payload.Length < 12 + 16 + 1) continue;
            var length = payload.Length - 12 - 16;
            try
            {
                _aes!.Decrypt(payload[..12], payload.Slice(12, length), payload[^16..], plain.AsSpan(0, length));
            }
            catch (CryptographicException) { continue; } // not for our secret
            await HandleAsync(plain.AsSpan(0, length).ToArray(), onAuthenticated, ct);
        }
    }

    private async Task HandleAsync(byte[] packet, Action onAuthenticated, CancellationToken ct)
    {
        var type = packet[0];
        switch (type)
        {
            case 0x06: // AuthenticateAck
                onAuthenticated();
                Say("voice authenticated");
                return;
            case 0x0A: // ConnectionCheckAck
                if (!IsConnected)
                {
                    IsConnected = true;
                    Status = null;
                    _lastKeepAliveMs = Environment.TickCount64;
                    Say("voice connected");
                    Connected?.Invoke();
                    _connected.TrySetResult();
                }
                return;
            case 0x08: // KeepAlive: answer it
                _lastKeepAliveMs = Environment.TickCount64;
                await SendAsync(0x08, [], ct);
                return;
            case 0x07: // Ping: echo it back
                await SendAsync(0x07, packet.AsSpan(1).ToArray(), ct);
                return;
            case 0x02: case 0x03: case 0x04:
                OnSound(type, packet);
                return;
        }
    }

    private void OnSound(byte type, byte[] packet)
    {
        var r = new PacketReader(packet.AsSpan(1));
        r.ReadUuid(); // channel id
        var sender = r.ReadUuid();
        (double, double, double)? location = null;
        if (type == 0x04) location = (r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
        var opus = r.ReadByteArray().ToArray();
        var sequence = r.ReadLong();
        var distance = type == 0x03 ? 0f : r.ReadFloat();
        var flags = r.ReadByte();
        var whisper = (flags & 0x1) != 0;

        short[] pcm;
        if (opus.Length == 0)
        {
            pcm = [];
            if (_decoders.TryRemove(sender, out var old)) old.ResetState();
        }
        else
        {
            var decoder = _decoders.GetOrAdd(sender, _ => OpusCodecFactory.CreateDecoder(SampleRate, 1));
            pcm = new short[FrameSize];
            var samples = decoder.Decode(opus, pcm, FrameSize, false);
            if (samples != FrameSize) Array.Resize(ref pcm, Math.Max(samples, 0));
        }
        AudioReceived?.Invoke(new VoiceFrame(sender, type switch { 0x02 => VoiceSoundKind.Player, 0x03 => VoiceSoundKind.Group, _ => VoiceSoundKind.Location },
            pcm, sequence, whisper, distance, location));
    }

    private async ValueTask SendAsync(byte type, byte[] body, CancellationToken ct)
    {
        if (_udp == null || _aes == null) return;
        var plain = new byte[1 + body.Length];
        plain[0] = type;
        body.CopyTo(plain, 1);
        // payload = IV(12) + ciphertext + tag(16)
        var payload = new byte[12 + plain.Length + 16];
        RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
        _aes.Encrypt(payload.AsSpan(0, 12), plain, payload.AsSpan(12, plain.Length), payload.AsSpan(12 + plain.Length, 16));
        var frame = new byte[1 + 16 + 5 + payload.Length];
        frame[0] = Magic;
        _playerId.TryWriteBytes(frame.AsSpan(1), bigEndian: true, out _);
        var len = WriteVarInt(frame.AsSpan(17), payload.Length);
        payload.CopyTo(frame, 17 + len);
        await _sendLock.WaitAsync(ct);
        try { await _udp.SendAsync(frame.AsMemory(0, 17 + len + payload.Length), SocketFlags.None, ct); }
        finally { _sendLock.Release(); }
    }

    private static int WriteVarInt(Span<byte> span, int value)
    {
        var v = (uint)value;
        var n = 0;
        while (v >= 0x80) { span[n++] = (byte)(v | 0x80); v >>= 7; }
        span[n++] = (byte)v;
        return n;
    }

    // ---------------------------------------------------------------- speaking

    /// <summary>
    /// Speaks 48 kHz mono PCM into proximity voice: 20 ms Opus frames sent in real time (one every 20 ms, as a
    /// microphone would), then the stop packet. Returns when the last frame is sent.
    /// </summary>
    public async Task SpeakAsync(ReadOnlyMemory<short> pcm48kMono, bool whisper = false, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _encoder == null) throw new InvalidOperationException("voice chat is not connected");
        var opus = new byte[1275];
        var frame = new short[FrameSize];
        var start = Environment.TickCount64;
        var frames = 0;
        try
        {
            for (var offset = 0; offset < pcm48kMono.Length; offset += FrameSize)
            {
                var n = Math.Min(FrameSize, pcm48kMono.Length - offset);
                pcm48kMono.Span.Slice(offset, n).CopyTo(frame);
                if (n < FrameSize) Array.Clear(frame, n, FrameSize - n);
                var size = _encoder.Encode(frame, FrameSize, opus, opus.Length);
                await SendMicAsync(opus.AsSpan(0, size).ToArray(), whisper, cancellationToken);
                frames++;
                var due = start + frames * 20L;
                var wait = due - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)wait, cancellationToken);
            }
        }
        finally
        {
            // The stop packet goes out even when speaking is cut off (Vertex interrupted mid-sentence): without it
            // every listener's client holds the stream open until its own timeout.
            if (IsConnected) await SendMicAsync([], false, CancellationToken.None);
        }
    }

    private ValueTask SendMicAsync(byte[] opus, bool whisper, CancellationToken ct)
    {
        // MicPacket: VarInt-length data, long sequence, bool whispering.
        var body = new byte[5 + opus.Length + 8 + 1];
        var n = WriteVarInt(body, opus.Length);
        opus.CopyTo(body, n);
        n += opus.Length;
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(n), Interlocked.Increment(ref _sequence) - 1);
        n += 8;
        body[n++] = whisper ? (byte)1 : (byte)0;
        return SendAsync(0x01, body[..n], ct);
    }

    private async Task CloseUdpAsync()
    {
        IsConnected = false;
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _aes?.Dispose();
        _aes = null;
        if (_connected.Task.IsCompleted) _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.PluginMessage -= OnPluginMessage;
        _client.Spawned -= OnSpawned;
        await CloseUdpAsync();
    }
}
