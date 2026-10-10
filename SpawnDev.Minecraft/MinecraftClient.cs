using System.Security.Cryptography;
using SpawnDev.Minecraft.Auth;
using SpawnDev.Minecraft.Nbt;
using SpawnDev.Minecraft.Net;
using SpawnDev.Minecraft.Protocol;
using SpawnDev.Minecraft.Text;
using CB = SpawnDev.Minecraft.Protocol.PacketIds;

namespace SpawnDev.Minecraft;

public enum ConnectionState { Disconnected, Login, Configuration, Play }

public enum GameMode { Survival = 0, Creative = 1, Adventure = 2, Spectator = 3 }

public sealed class ClientOptions
{
    public string Locale { get; set; } = "en_us";
    /// <summary>Chunk view distance requested from the server (2-32).</summary>
    public int ViewDistance { get; set; } = 8;
    /// <summary>The client brand reported on minecraft:brand. Servers may treat brands differently.</summary>
    public string Brand { get; set; } = "vanilla";
    /// <summary>Respawn automatically after dying.</summary>
    public bool AutoRespawn { get; set; } = true;
    /// <summary>Optional diagnostics sink.</summary>
    public Action<string>? Log { get; set; }
}

/// <summary>
/// A Minecraft Java Edition client (protocol 770 / 1.21.5): login, configuration, play, keep-alive, chat, and the
/// player's own position, health and game mode. One receive loop handles packets in order; events are raised on
/// that loop, so handlers must not block.
/// </summary>
public sealed class MinecraftClient : IAsyncDisposable
{
    public const int ProtocolVersion = 770;
    public const string GameVersion = "1.21.5";

    private readonly ClientOptions _options;
    private readonly LastSeenMessages _lastSeen = new();
    private readonly object _positionLock = new();
    private MinecraftConnection? _connection;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _tickLoop;
    private TaskCompletionSource? _spawned;
    private bool _awaitingLoaded;
    private double _x, _y, _z;
    private float _yaw, _pitch;

    public MinecraftClient(GameProfile profile, ClientOptions? options = null)
    {
        Profile = profile;
        _options = options ?? new ClientOptions();
    }

    public GameProfile Profile { get; private set; }
    public ConnectionState State { get; private set; }
    public Registries Registries { get; } = new();

    public int EntityId { get; private set; }
    public string? WorldName { get; private set; }
    public GameMode GameMode { get; private set; }
    public float Health { get; private set; } = 20;
    public int Food { get; private set; } = 20;
    public float Saturation { get; private set; }
    public bool IsSpawned { get; private set; }
    public string? DisconnectReason { get; private set; }

    public (double X, double Y, double Z) Position { get { lock (_positionLock) return (_x, _y, _z); } }
    public (float Yaw, float Pitch) Rotation { get { lock (_positionLock) return (_yaw, _pitch); } }

    public event Action<ChatMessage>? ChatReceived;
    /// <summary>Raised once when the connection ends, with the server's reason (or the error).</summary>
    public event Action<string>? Disconnected;
    /// <summary>A plugin channel message (e.g. voicechat:secret). Raised on the receive loop.</summary>
    public event Action<string, ReadOnlyMemory<byte>>? PluginMessage;
    /// <summary>Raised after the first position of each login/respawn (the player is in the world).</summary>
    public event Action? Spawned;

    private void Log(string message) => _options.Log?.Invoke(message);

    // ---------------------------------------------------------------- connect

    /// <summary>Connects, logs in, and returns once the player has spawned in the world.</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (_connection != null) throw new InvalidOperationException("already connected");
        _cts = new CancellationTokenSource();
        _connection = await MinecraftConnection.ConnectAsync(host, port, cancellationToken);
        _spawned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DisconnectReason = null;

        using (var hello = new PacketWriter(CB.Handshake.Serverbound.Intention))
        {
            hello.WriteVarInt(ProtocolVersion).WriteString(host).WriteUShort((ushort)port).WriteVarInt(2); // 2 = login
            await _connection.SendAsync(hello, cancellationToken);
        }
        using (var start = new PacketWriter(CB.Login.Serverbound.Hello))
        {
            start.WriteString(Profile.Name).WriteUuid(Profile.Id);
            await _connection.SendAsync(start, cancellationToken);
        }
        State = ConnectionState.Login;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        using var reg = cancellationToken.Register(() => _spawned.TrySetCanceled(cancellationToken));
        await _spawned.Task;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        string reason = "connection closed";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var received = await _connection!.ReceiveAsync(ct);
                if (received == null) break;
                using var packet = received.Value;
                var stop = await HandleAsync(packet, ct);
                if (stop != null) { reason = stop; break; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = "disconnected by client";
        }
        catch (Exception e)
        {
            reason = "connection error: " + e.Message;
            Log(e.ToString());
        }
        Finish(reason);
    }

    private void Finish(string reason)
    {
        if (State == ConnectionState.Disconnected) return;
        State = ConnectionState.Disconnected;
        IsSpawned = false;
        DisconnectReason ??= reason;
        _spawned?.TrySetException(new InvalidOperationException("disconnected before spawning: " + DisconnectReason));
        _cts?.Cancel();
        Log("disconnected: " + DisconnectReason);
        Disconnected?.Invoke(DisconnectReason);
    }

    /// <summary>Handles one packet. Returns a disconnect reason to end the connection, else null.</summary>
    private ValueTask<string?> HandleAsync(ReceivedPacket packet, CancellationToken ct) => State switch
    {
        ConnectionState.Login => HandleLoginAsync(packet, ct),
        ConnectionState.Configuration => HandleConfigurationAsync(packet, ct),
        ConnectionState.Play => HandlePlayAsync(packet, ct),
        _ => ValueTask.FromResult<string?>(null),
    };

    // ---------------------------------------------------------------- login

    private async ValueTask<string?> HandleLoginAsync(ReceivedPacket packet, CancellationToken ct)
    {
        var c = _connection!;
        switch (packet.Id)
        {
            case CB.Login.Clientbound.LoginDisconnect:
            {
                // Login disconnect is still JSON text (NBT starts in configuration).
                var r = packet.Reader();
                DisconnectReason = JsonChatToPlain(r.ReadString(262144));
                return DisconnectReason;
            }
            case CB.Login.Clientbound.Hello:
            {
                string serverId;
                byte[] publicKey, verifyToken;
                bool authenticate;
                {
                    var r = packet.Reader();
                    serverId = r.ReadString(20);
                    publicKey = r.ReadByteArray().ToArray();
                    verifyToken = r.ReadByteArray().ToArray();
                    authenticate = r.ReadBool();
                }
                var secret = RandomNumberGenerator.GetBytes(16);
                if (authenticate)
                {
                    if (Profile.AccessToken == null)
                        return DisconnectReason = "this server is in online mode: sign in with a Microsoft account to join it";
                    await SessionServer.JoinAsync(Profile.AccessToken, Profile.Id, SessionServer.ServerHash(serverId, secret, publicKey), ct);
                }
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                using (var key = new PacketWriter(CB.Login.Serverbound.Key))
                {
                    key.WriteByteArray(rsa.Encrypt(secret, RSAEncryptionPadding.Pkcs1));
                    key.WriteByteArray(rsa.Encrypt(verifyToken, RSAEncryptionPadding.Pkcs1));
                    await c.SendAsync(key, ct);
                }
                c.EnableEncryption(secret);
                Log("encryption enabled");
                return null;
            }
            case CB.Login.Clientbound.LoginCompression:
            {
                var r = packet.Reader();
                var threshold = r.ReadVarInt();
                c.EnableCompression(threshold);
                Log($"compression threshold {threshold}");
                return null;
            }
            case CB.Login.Clientbound.LoginFinished:
            {
                Guid id;
                string name;
                {
                    var r = packet.Reader();
                    id = r.ReadUuid();
                    name = r.ReadString(16);
                }
                Profile = Profile with { Id = id, Name = name };
                Log($"logged in as {name} ({id})");
                using (var ack = new PacketWriter(CB.Login.Serverbound.LoginAcknowledged))
                    await c.SendAsync(ack, ct);
                State = ConnectionState.Configuration;
                Registries.Clear();
                await SendClientInformationAsync(CB.Configuration.Serverbound.ClientInformation, ct);
                await SendBrandAsync(CB.Configuration.Serverbound.CustomPayload, ct);
                return null;
            }
            case CB.Login.Clientbound.CustomQuery:
            {
                int messageId;
                {
                    var r = packet.Reader();
                    messageId = r.ReadVarInt();
                }
                // We understand no login plugin channels: answer "not understood" like vanilla.
                using var answer = new PacketWriter(CB.Login.Serverbound.CustomQueryAnswer);
                answer.WriteVarInt(messageId).WriteBool(false);
                await c.SendAsync(answer, ct);
                return null;
            }
            case CB.Login.Clientbound.CookieRequest:
                await AnswerCookieAsync(packet, CB.Login.Serverbound.CookieResponse, ct);
                return null;
            default:
                Log($"login: unexpected packet 0x{packet.Id:X2}");
                return null;
        }
    }

    // ---------------------------------------------------------------- configuration

    private async ValueTask<string?> HandleConfigurationAsync(ReceivedPacket packet, CancellationToken ct)
    {
        var c = _connection!;
        switch (packet.Id)
        {
            case CB.Configuration.Clientbound.KeepAlive:
                await EchoLongAsync(packet, CB.Configuration.Serverbound.KeepAlive, ct);
                return null;
            case CB.Configuration.Clientbound.Ping:
                await EchoIntAsync(packet, CB.Configuration.Serverbound.Pong, ct);
                return null;
            case CB.Configuration.Clientbound.Disconnect:
                return DisconnectReason = ReadNbtText(packet);
            case CB.Configuration.Clientbound.SelectKnownPacks:
            {
                // Claim no known packs: the server then sends every registry entry in full (dimension heights,
                // chat formats...), so nothing has to be bundled for a particular game version.
                using var reply = new PacketWriter(CB.Configuration.Serverbound.SelectKnownPacks);
                reply.WriteVarInt(0);
                await c.SendAsync(reply, ct);
                return null;
            }
            case CB.Configuration.Clientbound.RegistryData:
            {
                var r = packet.Reader();
                var registry = r.ReadString();
                var count = r.ReadVarInt();
                var entries = new List<RegistryEntry>(count);
                for (var i = 0; i < count; i++)
                {
                    var key = r.ReadString();
                    var value = r.ReadBool() ? r.ReadNbt() : null;
                    entries.Add(new RegistryEntry(key, value));
                }
                Registries.Set(registry, entries);
                return null;
            }
            case CB.Configuration.Clientbound.CustomPayload:
                RaisePluginMessage(packet);
                return null;
            case CB.Configuration.Clientbound.ResourcePackPush:
                await AcceptResourcePackAsync(packet, CB.Configuration.Serverbound.ResourcePack, ct);
                return null;
            case CB.Configuration.Clientbound.CookieRequest:
                await AnswerCookieAsync(packet, CB.Configuration.Serverbound.CookieResponse, ct);
                return null;
            case CB.Configuration.Clientbound.FinishConfiguration:
            {
                using (var done = new PacketWriter(CB.Configuration.Serverbound.FinishConfiguration))
                    await c.SendAsync(done, ct);
                State = ConnectionState.Play;
                Log($"configuration finished ({Registries.Names.Count} registries)");
                return null;
            }
            case CB.Configuration.Clientbound.Transfer:
                return DisconnectReason = "the server asked to transfer to another server (not supported yet)";
            default:
                return null; // tags, features, links, report details: not needed yet
        }
    }

    // ---------------------------------------------------------------- play

    private async ValueTask<string?> HandlePlayAsync(ReceivedPacket packet, CancellationToken ct)
    {
        var c = _connection!;
        switch (packet.Id)
        {
            case CB.Play.Clientbound.KeepAlive:
                await EchoLongAsync(packet, CB.Play.Serverbound.KeepAlive, ct);
                return null;
            case CB.Play.Clientbound.Ping:
                await EchoIntAsync(packet, CB.Play.Serverbound.Pong, ct);
                return null;
            case CB.Play.Clientbound.Disconnect:
                return DisconnectReason = ReadNbtText(packet);
            case CB.Play.Clientbound.Login:
            {
                var r = packet.Reader();
                EntityId = r.ReadInt();
                r.ReadBool(); // hardcore
                var worlds = r.ReadVarInt();
                for (var i = 0; i < worlds; i++) r.ReadString();
                r.ReadVarInt(); // max players
                r.ReadVarInt(); // view distance
                r.ReadVarInt(); // simulation distance
                r.ReadBool();   // reduced debug info
                r.ReadBool();   // respawn screen
                r.ReadBool();   // limited crafting
                ReadSpawnInfo(ref r);
                IsSpawned = false;
                _awaitingLoaded = true;
                Log($"joined {WorldName} as entity {EntityId} in {GameMode}");
                _tickLoop ??= Task.Run(() => TickLoopAsync(_cts!.Token));
                return null;
            }
            case CB.Play.Clientbound.Respawn:
            {
                var r = packet.Reader();
                ReadSpawnInfo(ref r);
                IsSpawned = false;
                _awaitingLoaded = true;
                return null;
            }
            case CB.Play.Clientbound.PlayerPosition:
            {
                int teleportId;
                double x, y, z;
                float yaw, pitch;
                {
                    var r = packet.Reader();
                    teleportId = r.ReadVarInt();
                    x = r.ReadDouble(); y = r.ReadDouble(); z = r.ReadDouble();
                    r.ReadDouble(); r.ReadDouble(); r.ReadDouble(); // velocity: used once physics lands
                    yaw = r.ReadFloat(); pitch = r.ReadFloat();
                    var flags = r.ReadInt();
                    lock (_positionLock)
                    {
                        _x = (flags & 0x01) != 0 ? _x + x : x;
                        _y = (flags & 0x02) != 0 ? _y + y : y;
                        _z = (flags & 0x04) != 0 ? _z + z : z;
                        _yaw = (flags & 0x08) != 0 ? _yaw + yaw : yaw;
                        _pitch = (flags & 0x10) != 0 ? _pitch + pitch : pitch;
                        (x, y, z, yaw, pitch) = (_x, _y, _z, _yaw, _pitch);
                    }
                }
                using (var accept = new PacketWriter(CB.Play.Serverbound.AcceptTeleportation))
                {
                    accept.WriteVarInt(teleportId);
                    await c.SendAsync(accept, ct);
                }
                await SendPositionAsync(x, y, z, yaw, pitch, onGround: false, ct);
                if (_awaitingLoaded)
                {
                    _awaitingLoaded = false;
                    using (var loaded = new PacketWriter(CB.Play.Serverbound.PlayerLoaded))
                        await c.SendAsync(loaded, ct);
                    IsSpawned = true;
                    Log($"spawned at {x:0.##} {y:0.##} {z:0.##}");
                    Spawned?.Invoke();
                    _spawned?.TrySetResult();
                }
                return null;
            }
            case CB.Play.Clientbound.PlayerRotation:
            {
                var r = packet.Reader();
                lock (_positionLock)
                {
                    _yaw = r.ReadFloat();
                    _pitch = r.ReadFloat();
                }
                return null;
            }
            case CB.Play.Clientbound.ChunkBatchFinished:
            {
                // Tell the server how fast we take chunks; 64/tick is the most it accepts.
                using var ack = new PacketWriter(CB.Play.Serverbound.ChunkBatchReceived);
                ack.WriteFloat(64f);
                await c.SendAsync(ack, ct);
                return null;
            }
            case CB.Play.Clientbound.SetHealth:
            {
                {
                    var r = packet.Reader();
                    Health = r.ReadFloat();
                    Food = r.ReadVarInt();
                    Saturation = r.ReadFloat();
                }
                if (Health <= 0 && _options.AutoRespawn)
                {
                    using var respawn = new PacketWriter(CB.Play.Serverbound.ClientCommand);
                    respawn.WriteVarInt(0); // perform respawn
                    await c.SendAsync(respawn, ct);
                }
                return null;
            }
            case CB.Play.Clientbound.GameEvent:
            {
                var r = packet.Reader();
                var e = r.ReadByte();
                var value = r.ReadFloat();
                if (e == 3) GameMode = (GameMode)(int)value; // change game mode
                return null;
            }
            case CB.Play.Clientbound.SystemChat:
            {
                var r = packet.Reader();
                var text = TextComponent.ToPlainText(r.ReadNbt());
                var overlay = r.ReadBool();
                ChatReceived?.Invoke(new ChatMessage(overlay ? ChatKind.ActionBar : ChatKind.System, text, text));
                return null;
            }
            case CB.Play.Clientbound.PlayerChat:
                OnPlayerChat(packet);
                if (_lastSeen.PendingCount > 64)
                {
                    using var ack = new PacketWriter(CB.Play.Serverbound.ChatAck);
                    ack.WriteVarInt(_lastSeen.TakeOffset());
                    await c.SendAsync(ack, ct);
                }
                return null;
            case CB.Play.Clientbound.DisguisedChat:
            {
                var r = packet.Reader();
                var message = r.ReadNbt();
                var (key, parameters) = ReadChatTypeHolder(ref r);
                var name = r.ReadNbt();
                var target = r.ReadBool() ? r.ReadNbt() : null;
                var content = TextComponent.ToPlainText(message);
                var senderName = TextComponent.ToPlainText(name);
                ChatReceived?.Invoke(new ChatMessage(ChatKind.Disguised,
                    Decorate(key, parameters, message, name, target), content, null, senderName));
                return null;
            }
            case CB.Play.Clientbound.CustomPayload:
                RaisePluginMessage(packet);
                return null;
            case CB.Play.Clientbound.StartConfiguration:
            {
                // The server (or a proxy switching servers) sends us back to configuration.
                IsSpawned = false;
                using (var ack = new PacketWriter(CB.Play.Serverbound.ConfigurationAcknowledged))
                    await c.SendAsync(ack, ct);
                State = ConnectionState.Configuration;
                Registries.Clear();
                await SendClientInformationAsync(CB.Configuration.Serverbound.ClientInformation, ct);
                return null;
            }
            case CB.Play.Clientbound.ResourcePackPush:
                await AcceptResourcePackAsync(packet, CB.Play.Serverbound.ResourcePack, ct);
                return null;
            case CB.Play.Clientbound.CookieRequest:
                await AnswerCookieAsync(packet, CB.Play.Serverbound.CookieResponse, ct);
                return null;
            case CB.Play.Clientbound.Transfer:
                return DisconnectReason = "the server asked to transfer to another server (not supported yet)";
            default:
                return null;
        }
    }

    private void ReadSpawnInfo(ref PacketReader r)
    {
        r.ReadVarInt(); // dimension type id
        WorldName = r.ReadString();
        r.ReadLong();   // hashed seed
        GameMode = (GameMode)r.ReadSByte();
        r.ReadByte();   // previous game mode
        r.ReadBool();   // debug
        r.ReadBool();   // flat
        if (r.ReadBool()) { r.ReadString(); r.ReadPosition(); } // death location
        r.ReadVarInt(); // portal cooldown
        r.ReadVarInt(); // sea level
    }

    private void OnPlayerChat(ReceivedPacket packet)
    {
        var r = packet.Reader();
        r.ReadVarInt(); // global index
        var sender = r.ReadUuid();
        r.ReadVarInt(); // index
        if (r.ReadBool()) _lastSeen.Add(r.ReadBytes(256));
        var plain = r.ReadString(256);
        r.ReadLong(); // timestamp
        r.ReadLong(); // salt
        var previous = r.ReadVarInt();
        for (var i = 0; i < previous; i++)
            if (r.ReadVarInt() == 0) r.ReadBytes(256);
        var unsigned = r.ReadBool() ? r.ReadNbt() : null;
        var filter = r.ReadVarInt();
        if (filter == 2) // partially filtered: a BitSet of longs
        {
            var longs = r.ReadVarInt();
            r.ReadBytes(longs * 8);
        }
        var (key, parameters) = ReadChatTypeHolder(ref r);
        var name = r.ReadNbt();
        var target = r.ReadBool() ? r.ReadNbt() : null;

        var content = unsigned ?? new NbtString(plain);
        ChatReceived?.Invoke(new ChatMessage(ChatKind.Player,
            Decorate(key, parameters, content, name, target),
            TextComponent.ToPlainText(content), sender, TextComponent.ToPlainText(name)));
    }

    /// <summary>A chat type: either a registry reference (id + 1) or inline (0, then the type itself).</summary>
    private (string Key, string[] Parameters) ReadChatTypeHolder(ref PacketReader r)
    {
        var holder = r.ReadVarInt();
        if (holder != 0)
        {
            var entry = Registries.Get("minecraft:chat_type", holder - 1);
            var chat = (entry?.Value as NbtCompound)?.GetCompound("chat");
            var key = chat?.GetString("translation_key") ?? "chat.type.text";
            var parameters = chat?.GetList("parameters")?.Items.Select(i => (i as NbtString)?.Value ?? "").ToArray()
                ?? ["sender", "content"];
            return (key, parameters);
        }
        // Inline ChatType: chat decoration then narration decoration; each is key, parameter ids, style.
        var chatKey = r.ReadString();
        var count = r.ReadVarInt();
        var chatParameters = new string[count];
        for (var i = 0; i < count; i++) chatParameters[i] = r.ReadVarInt() switch { 0 => "content", 1 => "sender", _ => "target" };
        r.SkipNbt();
        r.ReadString();
        var n = r.ReadVarInt();
        for (var i = 0; i < n; i++) r.ReadVarInt();
        r.SkipNbt();
        return (chatKey, chatParameters);
    }

    private static string Decorate(string key, string[] parameters, NbtTag? content, NbtTag? sender, NbtTag? target)
    {
        var with = parameters.Select(p => p switch
        {
            "sender" => sender ?? new NbtString(""),
            "target" => target ?? new NbtString(""),
            _ => content ?? new NbtString(""),
        }).ToList();
        var component = new NbtCompound(new()
        {
            ["translate"] = new NbtString(key),
            ["with"] = new NbtList(NbtType.Compound, with),
        });
        return TextComponent.ToPlainText(component);
    }

    // ---------------------------------------------------------------- tick

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (State != ConnectionState.Play || !IsSpawned) continue;
                tick++;
                if (tick % 20 == 0)
                {
                    // Vanilla resends its position at least once a second even when standing still.
                    var (x, y, z) = Position;
                    var (yaw, pitch) = Rotation;
                    await SendPositionAsync(x, y, z, yaw, pitch, onGround: true, ct);
                }
                using var end = new PacketWriter(CB.Play.Serverbound.ClientTickEnd);
                await _connection!.SendAsync(end, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (State == ConnectionState.Disconnected || e is System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Log("tick loop ended: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- outgoing

    /// <summary>Sends a chat message, or a command when it starts with '/'.</summary>
    public async Task SendChatAsync(string message, CancellationToken cancellationToken = default)
    {
        var c = _connection ?? throw new InvalidOperationException("not connected");
        if (State != ConnectionState.Play) throw new InvalidOperationException("not in game");
        if (message.StartsWith('/'))
        {
            using var command = new PacketWriter(CB.Play.Serverbound.ChatCommand);
            command.WriteString(message[1..]);
            await c.SendAsync(command, cancellationToken);
            return;
        }
        if (message.Length > 256) throw new ArgumentException("chat messages are at most 256 characters", nameof(message));
        var (offset, acknowledged) = _lastSeen.TakeUpdate();
        using var chat = new PacketWriter(CB.Play.Serverbound.Chat);
        chat.WriteString(message)
            .WriteLong(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .WriteLong(Random.Shared.NextInt64())
            .WriteBool(false) // unsigned (signing arrives with Microsoft accounts)
            .WriteVarInt(offset)
            .WriteBytes(acknowledged)
            .WriteByte(0); // checksum 0 = "not checked" (LastSeenMessages$Update.verifyChecksum)
        await c.SendAsync(chat, cancellationToken);
    }

    private async ValueTask SendPositionAsync(double x, double y, double z, float yaw, float pitch, bool onGround, CancellationToken ct)
    {
        using var move = new PacketWriter(CB.Play.Serverbound.MovePlayerPosRot);
        move.WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteFloat(yaw).WriteFloat(pitch).WriteByte(onGround ? (byte)1 : (byte)0);
        await _connection!.SendAsync(move, ct);
    }

    private async ValueTask SendClientInformationAsync(int packetId, CancellationToken ct)
    {
        using var info = new PacketWriter(packetId);
        info.WriteString(_options.Locale)
            .WriteSByte((sbyte)Math.Clamp(_options.ViewDistance, 2, 32))
            .WriteVarInt(0)       // chat: enabled
            .WriteBool(true)      // chat colors
            .WriteByte(0x7F)      // all skin parts
            .WriteVarInt(1)       // main hand: right
            .WriteBool(false)     // text filtering
            .WriteBool(true)      // allow server listing
            .WriteVarInt(2);      // particles: minimal
        await _connection!.SendAsync(info, ct);
    }

    private async ValueTask SendBrandAsync(int packetId, CancellationToken ct)
    {
        using var brand = new PacketWriter(packetId);
        brand.WriteString("minecraft:brand").WriteString(_options.Brand);
        await _connection!.SendAsync(brand, ct);
    }

    /// <summary>Sends a plugin channel message (custom payload) in the current state.</summary>
    public async Task SendPluginMessageAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var c = _connection ?? throw new InvalidOperationException("not connected");
        var id = State switch
        {
            ConnectionState.Configuration => CB.Configuration.Serverbound.CustomPayload,
            ConnectionState.Play => CB.Play.Serverbound.CustomPayload,
            _ => throw new InvalidOperationException("plugin messages need configuration or play"),
        };
        using var payload = new PacketWriter(id, data.Length + channel.Length + 8);
        payload.WriteString(channel).WriteBytes(data.Span);
        await c.SendAsync(payload, cancellationToken);
    }

    private void RaisePluginMessage(ReceivedPacket packet)
    {
        if (PluginMessage == null) return;
        var r = packet.Reader();
        var channel = r.ReadString();
        PluginMessage.Invoke(channel, r.ReadRest().ToArray()); // the packet buffer is reused; handlers get their own copy
    }

    private async ValueTask EchoLongAsync(ReceivedPacket packet, int replyId, CancellationToken ct)
    {
        long id;
        {
            var r = packet.Reader();
            id = r.ReadLong();
        }
        using var reply = new PacketWriter(replyId);
        reply.WriteLong(id);
        await _connection!.SendAsync(reply, ct);
    }

    private async ValueTask EchoIntAsync(ReceivedPacket packet, int replyId, CancellationToken ct)
    {
        int id;
        {
            var r = packet.Reader();
            id = r.ReadInt();
        }
        using var reply = new PacketWriter(replyId);
        reply.WriteInt(id);
        await _connection!.SendAsync(reply, ct);
    }

    private async ValueTask AnswerCookieAsync(ReceivedPacket packet, int replyId, CancellationToken ct)
    {
        string key;
        {
            var r = packet.Reader();
            key = r.ReadString();
        }
        using var reply = new PacketWriter(replyId);
        reply.WriteString(key).WriteBool(false); // we store no cookies
        await _connection!.SendAsync(reply, ct);
    }

    private async ValueTask AcceptResourcePackAsync(ReceivedPacket packet, int replyId, CancellationToken ct)
    {
        Guid id;
        {
            var r = packet.Reader();
            id = r.ReadUuid();
        }
        // A bot has no use for textures, but servers that require a pack kick on decline: report the vanilla
        // client's success sequence (accepted, downloaded, loaded).
        foreach (var status in new[] { 3, 4, 0 })
        {
            using var reply = new PacketWriter(replyId);
            reply.WriteUuid(id).WriteVarInt(status);
            await _connection!.SendAsync(reply, ct);
        }
    }

    private static string ReadNbtText(ReceivedPacket packet)
    {
        var r = packet.Reader();
        return TextComponent.ToPlainText(r.ReadNbt());
    }

    private static string JsonChatToPlain(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return JsonText(doc.RootElement);
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }

        static string JsonText(System.Text.Json.JsonElement e) => e.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => e.GetString() ?? "",
            System.Text.Json.JsonValueKind.Array => string.Concat(e.EnumerateArray().Select(JsonText)),
            System.Text.Json.JsonValueKind.Object =>
                (e.TryGetProperty("text", out var t) ? t.GetString() :
                 e.TryGetProperty("translate", out var k) ? TextComponent.Translate(k.GetString() ?? "") ?? k.GetString() : "") +
                (e.TryGetProperty("extra", out var x) ? JsonText(x) : ""),
            _ => "",
        };
    }

    // ---------------------------------------------------------------- shutdown

    /// <summary>Closes the connection.</summary>
    public async Task DisconnectAsync()
    {
        DisconnectReason ??= "disconnected by client";
        _cts?.Cancel();
        if (_connection != null) await _connection.DisposeAsync();
        if (_receiveLoop != null) await _receiveLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_tickLoop != null) await _tickLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Finish(DisconnectReason);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
