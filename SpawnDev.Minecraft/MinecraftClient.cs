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
    /// <summary>
    /// Modded block facts beyond the embedded catalog (tools/ModDump), e.g. dumped at runtime for one server's mods.
    /// Entries here win over the embedded ones of the same name.
    /// </summary>
    public IReadOnlyDictionary<string, Data.ModdedBlock>? ModdedBlocks { get; set; }
    /// <summary>Use the modded blocks built into SpawnDev.Minecraft (tools/ModDump). Off: only ModdedBlocks.</summary>
    public bool UseEmbeddedModdedBlocks { get; set; } = true;
}

/// <summary>
/// A Minecraft Java Edition client (protocol 770 / 1.21.5): login, configuration, play, keep-alive, chat, and the
/// player's own position, health and game mode. One receive loop handles packets in order; events are raised on
/// that loop, so handlers must not block.
/// </summary>
public sealed partial class MinecraftClient : IAsyncDisposable
{
    public const int ProtocolVersion = 770;
    public const string GameVersion = "1.21.5";

    private readonly ClientOptions _options;
    private readonly LastSeenMessages _lastSeen = new();
    private readonly SemaphoreSlim _chatLock = new(1, 1);
    private ChatSession? _chatSession;
    private TaskCompletionSource? _chatSessionAccepted;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, PlayerInfo> _players = new();

    /// <summary>The server's command tree for this player (null until the server sends it).</summary>
    public CommandTree? Commands { get; private set; }

    /// <summary>The server's player list (tab list), keyed by player UUID.</summary>
    public IReadOnlyDictionary<Guid, PlayerInfo> Players => _players;
    /// <summary>A player was added to the player list (joined the server, or was already on when we joined).</summary>
    public event Action<PlayerInfo>? PlayerAdded;
    /// <summary>A player left the player list.</summary>
    public event Action<PlayerInfo>? PlayerRemoved;
    private readonly object _positionLock = new();
    private MinecraftConnection? _connection;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _tickLoop;
    private TaskCompletionSource? _spawned;
    private bool _awaitingLoaded;
    private readonly Physics.PlayerState _body = new();
    private Physics.PlayerPhysics? _physics;
    // What we last told the server (LocalPlayer.sendPosition bookkeeping).
    private double _sentX, _sentY, _sentZ;
    private float _sentYaw, _sentPitch;
    private bool _sentOnGround, _sentHorizontalCollision, _sentSprinting, _sentSneaking;
    private byte _sentInput;
    private int _positionReminder;

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

    public (double X, double Y, double Z) Position { get { lock (_positionLock) return (_body.X, _body.Y, _body.Z); } }
    public (float Yaw, float Pitch) Rotation { get { lock (_positionLock) return (_body.Yaw, _body.Pitch); } }
    public (double X, double Y, double Z) Velocity { get { lock (_positionLock) return (_body.VelX, _body.VelY, _body.VelZ); } }
    public bool OnGround { get { lock (_positionLock) return _body.OnGround; } }

    /// <summary>The keys Vertex is "pressing". Read by the physics every tick.</summary>
    public Physics.PlayerControls Controls { get; } = new();

    /// <summary>
    /// When true (the default) the client simulates vanilla movement every tick and reports it like a real
    /// client. When false it stands still and only answers teleports.
    /// </summary>
    public bool PhysicsEnabled { get; set; } = true;

    /// <summary>
    /// Raised on the tick loop at the start of every client tick, before physics: the place to steer
    /// (set Controls, Look). Must not block.
    /// </summary>
    public event Action? Tick;

    /// <summary>A copy of the simulated body (position, velocity, collision and fluid flags).</summary>
    public Physics.PlayerState BodySnapshot()
    {
        lock (_positionLock) return _body.Clone();
    }

    /// <summary>Turns the head/body (degrees, vanilla convention: yaw 0 = +Z/south, 90 = -X/west; pitch +90 = down).</summary>
    public void Look(float yaw, float pitch)
    {
        lock (_positionLock)
        {
            _body.Yaw = yaw;
            _body.Pitch = Math.Clamp(pitch, -90f, 90f);
        }
    }

    /// <summary>Looks at a point from the eyes.</summary>
    public void LookAt(double x, double y, double z)
    {
        var (px, py, pz) = Position;
        double dx = x - px, dy = y - (py + Physics.PlayerState.EyeHeight), dz = z - pz;
        var yaw = (float)(Math.Atan2(-dx, dz) * 180 / Math.PI);
        var pitch = (float)(-Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI);
        Look(yaw, pitch);
    }

    public event Action<ChatMessage>? ChatReceived;

    /// <summary>Play packets that failed to decode and were skipped (zero on vanilla servers).</summary>
    public int PacketErrors { get; private set; }
    public event Action<int, Exception>? PacketError;
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
        FailTickWaiters();
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
                await RegisterFabricChannelsAsync(ct);
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
                if (await HandleFabricPayloadAsync(packet, ct)) return null;
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
            case CB.Configuration.Clientbound.UpdateTags:
                ReadTags(packet);
                return null;
            default:
                return null; // features, links, report details: not needed
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
                await StartChatSessionAsync(ct);
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
                    double vx = r.ReadDouble(), vy = r.ReadDouble(), vz = r.ReadDouble();
                    yaw = r.ReadFloat(); pitch = r.ReadFloat();
                    var flags = r.ReadInt();
                    lock (_positionLock)
                    {
                        var b = _body;
                        b.X = (flags & 0x01) != 0 ? b.X + x : x;
                        b.Y = (flags & 0x02) != 0 ? b.Y + y : y;
                        b.Z = (flags & 0x04) != 0 ? b.Z + z : z;
                        var oldYaw = b.Yaw;
                        b.Yaw = (flags & 0x08) != 0 ? b.Yaw + yaw : yaw;
                        b.Pitch = (flags & 0x10) != 0 ? b.Pitch + pitch : pitch;
                        // PlayerPositionUpdate: relative velocity keeps ours (optionally rotated by the yaw change).
                        double ox = b.VelX, oy = b.VelY, oz = b.VelZ;
                        if ((flags & 0x100) != 0)
                        {
                            var turn = (oldYaw - b.Yaw) * Physics.Mth.DegToRad;
                            double cos = Math.Cos(turn), sin = Math.Sin(turn);
                            (ox, oz) = (ox * cos - oz * sin, oz * cos + ox * sin);
                        }
                        b.VelX = (flags & 0x20) != 0 ? ox + vx : vx;
                        b.VelY = (flags & 0x40) != 0 ? oy + vy : vy;
                        b.VelZ = (flags & 0x80) != 0 ? oz + vz : vz;
                        b.OnGround = false;
                        b.FallDistance = 0;
                        (x, y, z, yaw, pitch) = (b.X, b.Y, b.Z, b.Yaw, b.Pitch);
                    }
                }
                OnTeleported();
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
                    _body.Yaw = r.ReadFloat();
                    _body.Pitch = r.ReadFloat();
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
                if (IsModded && TryModdedSpawn(packet)) return null;
                RaisePluginMessage(packet);
                return null;
            case CB.Play.Clientbound.PlayerInfoUpdate:
                OnPlayerInfoUpdate(packet);
                return null;
            case CB.Play.Clientbound.Commands:
            {
                var r = packet.Reader();
                try { Commands = CommandTree.Read(ref r); }
                catch (InvalidDataException e)
                {
                    // A modded argument type has properties we cannot size, so the whole tree is unreadable.
                    // Commands with signable arguments then go out unsigned, which a secure-profile server refuses.
                    Commands = null;
                    PacketErrors++;
                    Log("command tree unreadable, commands will be sent unsigned: " + e.Message);
                }
                return null;
            }
            case CB.Play.Clientbound.PlayerInfoRemove:
            {
                var r = packet.Reader();
                var count = r.ReadVarInt();
                for (var i = 0; i < count; i++)
                    if (_players.TryRemove(r.ReadUuid(), out var gone)) PlayerRemoved?.Invoke(gone);
                return null;
            }
            case CB.Play.Clientbound.StartConfiguration:
            {
                // The server (or a proxy switching servers) sends us back to configuration.
                IsSpawned = false;
                using (var ack = new PacketWriter(CB.Play.Serverbound.ConfigurationAcknowledged))
                    await c.SendAsync(ack, ct);
                State = ConnectionState.Configuration;
                Registries.Clear();
                _players.Clear(); // the next server sends its own list
                Commands = null; // and its own commands
                _fabricRegistries.Clear(); // and, if it is a Fabric server, its own registry sync
                RebuildWorldData();
                await SendClientInformationAsync(CB.Configuration.Serverbound.ClientInformation, ct);
                await RegisterFabricChannelsAsync(ct); // the next server may be a Fabric one (proxy server switch)
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
                // Packets are framed one by one, so a packet we cannot decode (a modded server's custom entity data,
                // item components we do not know) is skipped instead of ending the connection. Counted and reported:
                // on a vanilla server this must stay zero (the tests check).
                try { OnWorldPacket(packet); }
                catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or InvalidCastException or KeyNotFoundException or ArgumentException or NullReferenceException or FormatException)
                {
                    PacketErrors++;
                    Log($"skipped packet 0x{packet.Id:X2}: {e.GetType().Name}: {e.Message}");
                    PacketError?.Invoke(packet.Id, e);
                }
                return null;
        }
    }

    private void ReadSpawnInfo(ref PacketReader r)
    {
        var dimensionType = r.ReadVarInt();
        WorldName = r.ReadString();
        ApplyDimension(dimensionType, WorldName);
        r.ReadLong();   // hashed seed
        GameMode = (GameMode)r.ReadSByte();
        r.ReadByte();   // previous game mode
        r.ReadBool();   // debug
        r.ReadBool();   // flat
        if (r.ReadBool()) { r.ReadString(); r.ReadPosition(); } // death location
        r.ReadVarInt(); // portal cooldown
        r.ReadVarInt(); // sea level
    }

    /// <summary>
    /// ClientboundPlayerInfoUpdatePacket. Action bits and per-entry field order are the 1.21.5 Action enum order
    /// (read from the server bytecode; minecraft-data swaps the last two): add player, initialize chat, game mode,
    /// listed, latency, display name, list order, hat.
    /// </summary>
    private void OnPlayerInfoUpdate(ReceivedPacket packet)
    {
        var r = packet.Reader();
        var actions = r.ReadByte();
        var count = r.ReadVarInt();
        List<PlayerInfo>? added = null;
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadUuid();
            PlayerInfo? player;
            if ((actions & 0x01) != 0)
            {
                var name = r.ReadString(16);
                var properties = r.ReadVarInt();
                for (var p = 0; p < properties; p++)
                {
                    r.ReadString(); // name (e.g. "textures")
                    r.ReadString(); // value
                    if (r.ReadBool()) r.ReadString(); // signature
                }
                player = new PlayerInfo(id, name);
                _players[id] = player;
                (added ??= []).Add(player);
            }
            else _players.TryGetValue(id, out player);

            if ((actions & 0x02) != 0)
            {
                Guid? session = null;
                if (r.ReadBool())
                {
                    session = r.ReadUuid();
                    r.ReadLong();       // key expiry
                    r.ReadByteArray();  // public key
                    r.ReadByteArray();  // Mojang's signature of it
                }
                if (player != null) player.ChatSessionId = session;
                // The server applied OUR chat session: signed chat from here on is decoded with it.
                if (id == Profile.Id && session != null && session == _chatSession?.SessionId) _chatSessionAccepted?.TrySetResult();
            }
            if ((actions & 0x04) != 0) { var mode = (GameMode)r.ReadVarInt(); if (player != null) player.GameMode = mode; }
            if ((actions & 0x08) != 0) { var listed = r.ReadBool(); if (player != null) player.Listed = listed; }
            if ((actions & 0x10) != 0) { var latency = r.ReadVarInt(); if (player != null) player.LatencyMs = latency; }
            if ((actions & 0x20) != 0)
            {
                var display = r.ReadBool() ? TextComponent.ToPlainText(r.ReadNbt()) : null;
                if (player != null) player.DisplayName = display;
            }
            if ((actions & 0x40) != 0) r.ReadVarInt(); // list order
            if ((actions & 0x80) != 0) r.ReadBool();   // show hat
        }
        if (added != null && PlayerAdded != null) foreach (var p in added) PlayerAdded(p);
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
                try { Tick?.Invoke(); }
                catch (Exception e) { Log("tick handler failed: " + e); }
                await PhysicsTickAsync(ct);
                using var end = new PacketWriter(CB.Play.Serverbound.ClientTickEnd);
                await _connection!.SendAsync(end, ct);
                AdvanceTickWaiters();
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
            await SendCommandAsync(c, message[1..], cancellationToken);
            return;
        }
        if (message.Length > 256) throw new ArgumentException("chat messages are at most 256 characters", nameof(message));

        await WaitForChatSessionAsync(cancellationToken);

        // One chat at a time: the server checks that the session index counts up and timestamps never go back.
        await _chatLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var salt = Random.Shared.NextInt64();
            var (offset, acknowledged, seenSignatures) = _lastSeen.TakeUpdate();
            var signature = _chatSession?.Sign(Profile.Id, message, now, salt, seenSignatures);
            using var chat = new PacketWriter(CB.Play.Serverbound.Chat);
            chat.WriteString(message)
                .WriteLong(now.ToUnixTimeMilliseconds())
                .WriteLong(salt)
                .WriteBool(signature != null);
            if (signature != null) chat.WriteBytes(signature);
            chat.WriteVarInt(offset)
                .WriteBytes(acknowledged)
                .WriteByte(0); // checksum 0 = "not checked" (LastSeenMessages$Update.verifyChecksum)
            await c.SendAsync(chat, cancellationToken);
        }
        finally
        {
            _chatLock.Release();
        }
    }

    /// <summary>
    /// Vanilla ClientPacketListener.sendCommand: a command with signable arguments (/msg, /me, /say...) goes out
    /// as chat_command_signed, each such argument signed like a chat message over its own text (the server's
    /// collectSignedArguments builds SignedMessageBody(argument text, timestamp, salt, last seen) per entry, in
    /// order, one chain index each). Anything else, or any command without a chat session, is a plain command.
    /// </summary>
    private async Task SendCommandAsync(MinecraftConnection c, string command, CancellationToken cancellationToken)
    {
        var signable = _chatSession != null ? Commands?.SignableArguments(command) : null;
        if (signable is not { Count: > 0 })
        {
            using var plain = new PacketWriter(CB.Play.Serverbound.ChatCommand);
            plain.WriteString(command);
            await c.SendAsync(plain, cancellationToken);
            return;
        }

        await WaitForChatSessionAsync(cancellationToken);
        await _chatLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var salt = Random.Shared.NextInt64();
            var (offset, acknowledged, seenSignatures) = _lastSeen.TakeUpdate();
            using var signed = new PacketWriter(CB.Play.Serverbound.ChatCommandSigned, 1024 + 300 * signable.Count);
            signed.WriteString(command)
                .WriteLong(now.ToUnixTimeMilliseconds())
                .WriteLong(salt)
                .WriteVarInt(signable.Count);
            foreach (var (name, value) in signable)
                signed.WriteString(name).WriteBytes(_chatSession!.Sign(Profile.Id, value, now, salt, seenSignatures));
            signed.WriteVarInt(offset)
                .WriteBytes(acknowledged)
                .WriteByte(0);
            await c.SendAsync(signed, cancellationToken);
        }
        finally
        {
            _chatLock.Release();
        }
    }

    private async Task WaitForChatSessionAsync(CancellationToken cancellationToken)
    {
        // Paper applies a chat session on its main thread (next tick) but decodes chat on the network thread, so a
        // signed message sent right after joining arrives before the session and is dropped ("Chat disabled due to
        // missing profile public key"; reproduced in LiveOnlineTests). Wait for the server's own confirmation: the
        // INITIALIZE_CHAT player-info entry it broadcasts once our session is in place.
        if (_chatSessionAccepted is { Task.IsCompleted: false } accepted)
        {
            var confirmed = await Task.WhenAny(accepted.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            if (confirmed != accepted.Task) Log("server never confirmed our chat session; sending anyway");
        }
    }

    /// <summary>
    /// Like vanilla's handleLogin: each join (including a proxy server switch) starts a new chat session with
    /// the account's profile key, so the server accepts our signed chat. Offline profiles have no key and chat
    /// unsigned.
    /// </summary>
    private async ValueTask StartChatSessionAsync(CancellationToken ct)
    {
        var keys = Profile.Keys;
        if (keys == null || keys.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            if (keys != null) Log("profile key expired: chat will be unsigned");
            _chatSession = null;
            return;
        }
        await _chatLock.WaitAsync(ct);
        try
        {
            _chatSession?.Dispose();
            _chatSession = new ChatSession(keys);
            _chatSessionAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var update = new PacketWriter(CB.Play.Serverbound.ChatSessionUpdate, 1024);
            update.WriteUuid(_chatSession.SessionId)
                .WriteLong(keys.ExpiresAt.ToUnixTimeMilliseconds())
                .WriteByteArray(keys.PublicKeyDer)
                .WriteByteArray(keys.PublicKeySignatureV2);
            await _connection!.SendAsync(update, ct);
            Log($"chat session {_chatSession.SessionId} started (key expires {keys.ExpiresAt:u})");
        }
        finally
        {
            _chatLock.Release();
        }
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
