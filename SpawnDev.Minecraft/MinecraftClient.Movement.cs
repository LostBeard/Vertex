using SpawnDev.Minecraft.Physics;
using SpawnDev.Minecraft.Protocol;
using CB = SpawnDev.Minecraft.Protocol.PacketIds;

namespace SpawnDev.Minecraft;

// The local player's tick: physics, then reporting the movement the way vanilla's LocalPlayer does.
public sealed partial class MinecraftClient
{
    /// <summary>Raised after each physics tick (position, velocity and flags are updated).</summary>
    public event Action? Moved;

    /// <summary>
    /// Server-forced position changes since connecting (spawn, /tp, respawn, and CORRECTIONS when the server
    /// rejects a move). Movement code can watch this: a rise without a reason means our physics disagreed.
    /// </summary>
    public int TeleportCount { get; private set; }

    /// <summary>Raised on the receive loop when the server sets our position (see TeleportCount).</summary>
    public event Action? Teleported;

    private void OnTeleported()
    {
        TeleportCount++;
        Teleported?.Invoke();
    }

    /// <summary>Test hook: runs after the attributes are applied, before each physics tick (tests make the
    /// client misbehave on purpose to prove the server notices).</summary>
    internal Action<PlayerPhysics>? PhysicsTweak;

    private async ValueTask PhysicsTickAsync(CancellationToken ct)
    {
        if (!PhysicsEnabled) return;
        _physics ??= new PlayerPhysics(World);
        RefreshPhysicsAttributes(_physics);
        PhysicsTweak?.Invoke(_physics);

        // Vanilla only lets you start sprinting with more than 6 food (or flying).
        var wantSprint = Controls.Sprint && Food > 6;
        var controls = Controls;
        var sprintSave = controls.Sprint;
        controls.Sprint = wantSprint;
        bool simulated;
        lock (_positionLock) simulated = _physics.Tick(_body, controls);
        controls.Sprint = sprintSave;
        if (!simulated) return; // our chunk is not loaded yet: vanilla does not move either

        await SendMovementAsync(ct);
        Moved?.Invoke();
    }

    private void RefreshPhysicsAttributes(PlayerPhysics p)
    {
        p.MovementSpeed = AttributeWithout("movement_speed", "minecraft:sprinting") ?? 0.1;
        p.JumpStrength = Attribute("jump_strength") ?? 0.41999998688697815;
        p.Gravity = Attribute("gravity") ?? 0.08;
        p.StepHeight = Attribute("step_height") ?? 0.6;
        p.SneakingSpeed = Attribute("sneaking_speed") ?? 0.3;
        p.WaterMovementEfficiency = Attribute("water_movement_efficiency") ?? 0;
    }

    /// <summary>An attribute's value ignoring one modifier (the physics adds the sprint boost itself).</summary>
    private double? AttributeWithout(string name, string modifierId)
    {
        var names = Data.GameData.Vanilla.Registries.GetValueOrDefault("minecraft:attribute") ?? [];
        var id = Array.IndexOf(names, Data.GameData.Normalize(name));
        if (id < 0 || !_attributes.TryGetValue(id, out var a)) return null;
        var mods = a.Modifiers.Where(m => m.Id != modifierId).ToList();
        var value = a.Base;
        foreach (var m in mods) if (m.Operation == 0) value += m.Amount;
        var result = value;
        foreach (var m in mods) if (m.Operation == 1) result += value * m.Amount;
        foreach (var m in mods) if (m.Operation == 2) result *= 1 + m.Amount;
        return result;
    }

    /// <summary>LocalPlayer.tick input reporting + sendPosition (sprint/shift commands, then the move packet).</summary>
    private async ValueTask SendMovementAsync(CancellationToken ct)
    {
        var c = _connection!;
        double x, y, z;
        float yaw, pitch;
        bool onGround, horizontal, sprinting;
        lock (_positionLock)
        {
            (x, y, z, yaw, pitch) = (_body.X, _body.Y, _body.Z, _body.Yaw, _body.Pitch);
            (onGround, horizontal, sprinting) = (_body.OnGround, _body.HorizontalCollision, _body.Sprinting);
        }

        var input = Controls.Flags;
        if (input != _sentInput)
        {
            using var p = new PacketWriter(CB.Play.Serverbound.PlayerInput);
            p.WriteByte(input);
            await c.SendAsync(p, ct);
            _sentInput = input;
        }
        if (Controls.Sneak != _sentSneaking)
        {
            await PlayerCommandAsync(Controls.Sneak ? 0 : 1, ct); // PRESS/RELEASE_SHIFT_KEY
            _sentSneaking = Controls.Sneak;
        }
        if (sprinting != _sentSprinting)
        {
            await PlayerCommandAsync(sprinting ? 3 : 4, ct); // START/STOP_SPRINTING
            _sentSprinting = sprinting;
        }

        double dx = x - _sentX, dy = y - _sentY, dz = z - _sentZ;
        var moved = dx * dx + dy * dy + dz * dz > 2.0E-4 * 2.0E-4 || ++_positionReminder >= 20;
        var rotated = yaw != _sentYaw || pitch != _sentPitch;
        var flags = (byte)((onGround ? 1 : 0) | (horizontal ? 2 : 0));
        PacketWriter? move = null;
        try
        {
            if (moved && rotated)
                move = new PacketWriter(CB.Play.Serverbound.MovePlayerPosRot).WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteFloat(yaw).WriteFloat(pitch).WriteByte(flags);
            else if (moved)
                move = new PacketWriter(CB.Play.Serverbound.MovePlayerPos).WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteByte(flags);
            else if (rotated)
                move = new PacketWriter(CB.Play.Serverbound.MovePlayerRot).WriteFloat(yaw).WriteFloat(pitch).WriteByte(flags);
            else if (onGround != _sentOnGround || horizontal != _sentHorizontalCollision)
                move = new PacketWriter(CB.Play.Serverbound.MovePlayerStatusOnly).WriteByte(flags);
            if (move != null) await c.SendAsync(move, ct);
        }
        finally
        {
            move?.Dispose();
        }
        if (moved)
        {
            (_sentX, _sentY, _sentZ) = (x, y, z);
            _positionReminder = 0;
        }
        if (rotated) (_sentYaw, _sentPitch) = (yaw, pitch);
        _sentOnGround = onGround;
        _sentHorizontalCollision = horizontal;
    }

    private async ValueTask PlayerCommandAsync(int action, CancellationToken ct)
    {
        using var p = new PacketWriter(CB.Play.Serverbound.PlayerCommand);
        p.WriteVarInt(EntityId).WriteVarInt(action).WriteVarInt(0);
        await _connection!.SendAsync(p, ct);
    }
}
