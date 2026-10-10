using System.Collections.Concurrent;
using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Entities;
using SpawnDev.Minecraft.Items;
using SpawnDev.Minecraft.Nbt;
using SpawnDev.Minecraft.Net;
using SpawnDev.Minecraft.Protocol;
using SpawnDev.Minecraft.Text;
using SpawnDev.Minecraft.World;
using CB = SpawnDev.Minecraft.Protocol.PacketIds;

namespace SpawnDev.Minecraft;

// The world model: chunks, block changes, entities, inventory, containers, tags, experience, attributes.
public sealed partial class MinecraftClient
{
    private readonly ConcurrentDictionary<int, Entity> _entities = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, int[]>> _tags = new();
    private readonly ConcurrentDictionary<int, (double Base, List<(string Id, double Amount, int Operation)> Modifiers)> _attributes = new();

    public GameWorld World { get; } = new();
    public IReadOnlyDictionary<int, Entity> Entities => _entities;
    public PlayerInventory Inventory { get; } = new();
    /// <summary>The container screen that is open (chest, furnace...), or null.</summary>
    public ContainerWindow? OpenWindow { get; private set; }
    /// <summary>The state id of the player inventory (container 0), sent back with every click.</summary>
    public int InventoryStateId { get; private set; }
    public int ExperienceLevel { get; private set; }
    public int TotalExperience { get; private set; }
    public float ExperienceProgress { get; private set; }
    /// <summary>Our own velocity as last set by the server (knockback, explosions), blocks per tick.</summary>
    public (double X, double Y, double Z) ServerVelocity { get; private set; }
    /// <summary>The highest block-change sequence the server has acknowledged (dig/place/use predictions).</summary>
    public int AcknowledgedSequence { get; private set; }

    public event Action<Entity>? EntitySpawned;
    public event Action<Entity>? EntityRemoved;
    /// <summary>A slot of the player inventory changed (slot number, new stack).</summary>
    public event Action<int, ItemStack>? InventoryChanged;
    public event Action<ContainerWindow>? WindowOpened;
    public event Action<int>? WindowClosed;
    /// <summary>A chunk column arrived (chunk x, chunk z).</summary>
    public event Action<int, int>? ChunkLoaded;
    /// <summary>An entity picked up an item entity (collector id, item entity id, count).</summary>
    public event Action<int, int, int>? ItemCollected;
    /// <summary>The server acknowledged block actions up to this sequence number.</summary>
    public event Action<int>? BlockChangeAcknowledged;

    public Entity? Entity(int id) => _entities.GetValueOrDefault(id);

    /// <summary>The tag's member ids in a registry ("minecraft:block", "minecraft:item"...), from the server.</summary>
    public IReadOnlyCollection<int> Tag(string registry, string tag) =>
        _tags.TryGetValue(GameData.Normalize(registry), out var r) && r.TryGetValue(GameData.Normalize(tag.TrimStart('#')), out var ids) ? ids : [];

    /// <summary>An attribute's current value for our player (base + modifiers, vanilla's three operations).</summary>
    public double? Attribute(string name)
    {
        var names = GameData.Vanilla.Registries.GetValueOrDefault("minecraft:attribute") ?? [];
        var id = Array.IndexOf(names, GameData.Normalize(name));
        if (id < 0 || !_attributes.TryGetValue(id, out var a)) return null;
        var value = a.Base;
        foreach (var m in a.Modifiers) if (m.Operation == 0) value += m.Amount;
        var result = value;
        foreach (var m in a.Modifiers) if (m.Operation == 1) result += value * m.Amount;
        foreach (var m in a.Modifiers) if (m.Operation == 2) result *= 1 + m.Amount;
        return result;
    }

    private void ApplyDimension(int dimensionTypeId, string worldName)
    {
        var entry = Registries.Get("minecraft:dimension_type", dimensionTypeId)?.Value as NbtCompound;
        World.Clear();
        _entities.Clear();
        World.Dimension = worldName;
        World.MinY = entry?.GetInt("min_y") ?? -64;
        World.Height = entry?.GetInt("height") ?? 384;
    }

    private void ReadTags(ReceivedPacket packet)
    {
        var r = packet.Reader();
        var registries = r.ReadVarInt();
        for (var i = 0; i < registries; i++)
        {
            var registry = r.ReadString();
            var count = r.ReadVarInt();
            var tags = new Dictionary<string, int[]>(count);
            for (var t = 0; t < count; t++)
            {
                var name = r.ReadString();
                var ids = new int[r.ReadVarInt()];
                for (var k = 0; k < ids.Length; k++) ids[k] = r.ReadVarInt();
                tags[name] = ids;
            }
            _tags[registry] = tags;
        }
    }

    private static double Angle(sbyte b) => b * 360.0 / 256.0;

    private void OnWorldPacket(ReceivedPacket packet)
    {
        var r = packet.Reader();
        switch (packet.Id)
        {
            case CB.Play.Clientbound.LevelChunkWithLight:
            {
                var column = ChunkColumn.Read(ref r, World.MinY, World.Height);
                World.SetColumn(column);
                ChunkLoaded?.Invoke(column.X, column.Z);
                return;
            }
            case CB.Play.Clientbound.ForgetLevelChunk:
            {
                var z = r.ReadInt();
                var x = r.ReadInt();
                World.Unload(x, z);
                return;
            }
            case CB.Play.Clientbound.BlockUpdate:
            {
                var (x, y, z) = r.ReadPosition();
                World.SetState(x, y, z, r.ReadVarInt());
                return;
            }
            case CB.Play.Clientbound.SectionBlocksUpdate:
            {
                var pos = r.ReadLong();
                var sx = (int)(pos >> 42);
                var sy = (int)(pos << 44 >> 44);
                var sz = (int)(pos << 22 >> 42);
                var count = r.ReadVarInt();
                for (var i = 0; i < count; i++)
                {
                    var record = r.ReadVarLong();
                    var state = (int)(record >> 12);
                    var local = (int)(record & 0xFFF);
                    World.SetState((sx << 4) + (local >> 8 & 15), (sy << 4) + (local & 15), (sz << 4) + (local >> 4 & 15), state);
                }
                return;
            }
            case CB.Play.Clientbound.BlockEntityData:
            {
                var (x, y, z) = r.ReadPosition();
                var type = r.ReadVarInt();
                var nbt = r.ReadNbt();
                if (World.Column(x >> 4, z >> 4) is ChunkColumn c) c.BlockEntities[(x, y, z)] = new BlockEntity(x, y, z, type, nbt);
                return;
            }
            case CB.Play.Clientbound.BlockChangedAck:
                AcknowledgedSequence = r.ReadVarInt();
                BlockChangeAcknowledged?.Invoke(AcknowledgedSequence);
                return;
            case CB.Play.Clientbound.SetTime:
                World.GameTime = r.ReadLong();
                World.DayTime = r.ReadLong();
                return;
            case CB.Play.Clientbound.SetDefaultSpawnPosition:
                World.Spawn = r.ReadPosition();
                return;
            case CB.Play.Clientbound.UpdateTags:
                ReadTags(packet);
                return;

            // ------------------------------------------------ entities
            case CB.Play.Clientbound.AddEntity:
            {
                var id = r.ReadVarInt();
                var uuid = r.ReadUuid();
                var type = GameData.Vanilla.Entity(r.ReadVarInt()) ?? new EntityTypeInfo(-1, "unknown", 0.6f, 1.8f, 1.6f);
                var e = new Entity(id, uuid, type)
                {
                    X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(),
                };
                e.Pitch = (float)Angle(r.ReadSByte());
                e.Yaw = (float)Angle(r.ReadSByte());
                e.HeadYaw = (float)Angle(r.ReadSByte());
                e.Data = r.ReadVarInt();
                e.VelocityX = r.ReadShort() / 8000.0;
                e.VelocityY = r.ReadShort() / 8000.0;
                e.VelocityZ = r.ReadShort() / 8000.0;
                if (e.IsPlayer && Players.TryGetValue(uuid, out var info)) e.PlayerName = info.Name;
                _entities[id] = e;
                EntitySpawned?.Invoke(e);
                return;
            }
            case CB.Play.Clientbound.MoveEntityPos:
            case CB.Play.Clientbound.MoveEntityPosRot:
            {
                var e = Entity(r.ReadVarInt());
                var dx = r.ReadShort() / 4096.0;
                var dy = r.ReadShort() / 4096.0;
                var dz = r.ReadShort() / 4096.0;
                if (e == null) return;
                e.X += dx; e.Y += dy; e.Z += dz;
                if (packet.Id == CB.Play.Clientbound.MoveEntityPosRot)
                {
                    e.Yaw = (float)Angle(r.ReadSByte());
                    e.Pitch = (float)Angle(r.ReadSByte());
                }
                e.OnGround = r.ReadBool();
                return;
            }
            case CB.Play.Clientbound.MoveEntityRot:
            {
                var e = Entity(r.ReadVarInt());
                if (e == null) return;
                e.Yaw = (float)Angle(r.ReadSByte());
                e.Pitch = (float)Angle(r.ReadSByte());
                e.OnGround = r.ReadBool();
                return;
            }
            case CB.Play.Clientbound.EntityPositionSync:
            {
                var e = Entity(r.ReadVarInt());
                if (e == null) return;
                e.X = r.ReadDouble(); e.Y = r.ReadDouble(); e.Z = r.ReadDouble();
                e.VelocityX = r.ReadDouble(); e.VelocityY = r.ReadDouble(); e.VelocityZ = r.ReadDouble();
                e.Yaw = r.ReadFloat(); e.Pitch = r.ReadFloat();
                e.OnGround = r.ReadBool();
                return;
            }
            case CB.Play.Clientbound.TeleportEntity:
            {
                var e = Entity(r.ReadVarInt());
                if (e == null) return;
                double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
                double vx = r.ReadDouble(), vy = r.ReadDouble(), vz = r.ReadDouble();
                float yaw = r.ReadFloat(), pitch = r.ReadFloat();
                var flags = r.ReadInt();
                e.X = (flags & 0x01) != 0 ? e.X + x : x;
                e.Y = (flags & 0x02) != 0 ? e.Y + y : y;
                e.Z = (flags & 0x04) != 0 ? e.Z + z : z;
                e.Yaw = (flags & 0x08) != 0 ? e.Yaw + yaw : yaw;
                e.Pitch = (flags & 0x10) != 0 ? e.Pitch + pitch : pitch;
                e.VelocityX = (flags & 0x20) != 0 ? e.VelocityX + vx : vx;
                e.VelocityY = (flags & 0x40) != 0 ? e.VelocityY + vy : vy;
                e.VelocityZ = (flags & 0x80) != 0 ? e.VelocityZ + vz : vz;
                e.OnGround = r.ReadBool();
                return;
            }
            case CB.Play.Clientbound.RotateHead:
            {
                var e = Entity(r.ReadVarInt());
                var head = r.ReadSByte();
                if (e != null) e.HeadYaw = (float)Angle(head);
                return;
            }
            case CB.Play.Clientbound.SetEntityMotion:
            {
                var id = r.ReadVarInt();
                double vx = r.ReadShort() / 8000.0, vy = r.ReadShort() / 8000.0, vz = r.ReadShort() / 8000.0;
                if (id == EntityId) ServerVelocity = (vx, vy, vz);
                if (Entity(id) is Entity e) { e.VelocityX = vx; e.VelocityY = vy; e.VelocityZ = vz; }
                return;
            }
            case CB.Play.Clientbound.RemoveEntities:
            {
                var count = r.ReadVarInt();
                for (var i = 0; i < count; i++)
                    if (_entities.TryRemove(r.ReadVarInt(), out var gone)) EntityRemoved?.Invoke(gone);
                return;
            }
            case CB.Play.Clientbound.SetEntityData:
            {
                var e = Entity(r.ReadVarInt());
                var entries = ProtoDef.Vanilla.Read(ref r, "entityMetadata") as List<object?>;
                if (e == null || entries == null) return;
                foreach (var entry in entries)
                {
                    if (entry is not Dictionary<string, object?> m) continue;
                    var key = Convert.ToInt32(m["key"]);
                    var value = m.GetValueOrDefault("value");
                    e.Metadata[key] = value;
                    var type = m.GetValueOrDefault("type") as string;
                    if (e.IsItem && key == 8 && type == "item_stack") e.Item = ItemStack.FromProtoDef(value);
                    else if (key == 9 && type == "float" && value is float health) e.Health = health;
                }
                return;
            }
            case CB.Play.Clientbound.SetEquipment:
            {
                // Entries of (slot byte, item); the slot byte's top bit says another entry follows.
                var e = Entity(r.ReadVarInt());
                while (true)
                {
                    var slot = r.ReadByte();
                    var item = ItemStack.Read(ref r);
                    if (e != null) e.Equipment[slot & 0x7F] = item;
                    if ((slot & 0x80) == 0) return;
                }
            }
            case CB.Play.Clientbound.TakeItemEntity:
            {
                var item = r.ReadVarInt();
                var collector = r.ReadVarInt();
                var count = r.ReadVarInt();
                ItemCollected?.Invoke(collector, item, count);
                return;
            }
            case CB.Play.Clientbound.UpdateAttributes:
            {
                var id = r.ReadVarInt();
                var count = r.ReadVarInt();
                for (var i = 0; i < count; i++)
                {
                    var attribute = r.ReadVarInt();
                    var value = r.ReadDouble();
                    var modifiers = new List<(string, double, int)>();
                    var mc = r.ReadVarInt();
                    for (var k = 0; k < mc; k++) modifiers.Add((r.ReadString(), r.ReadDouble(), r.ReadVarInt()));
                    if (id == EntityId) _attributes[attribute] = (value, modifiers);
                }
                return;
            }

            // ------------------------------------------------ inventory
            case CB.Play.Clientbound.ContainerSetContent:
            {
                var window = r.ReadVarInt();
                var stateId = r.ReadVarInt();
                var count = r.ReadVarInt();
                var slots = new ItemStack[count];
                for (var i = 0; i < count; i++) slots[i] = ItemStack.Read(ref r);
                var carried = ItemStack.Read(ref r);
                Inventory.Cursor = carried;
                if (window == 0)
                {
                    InventoryStateId = stateId;
                    for (var i = 0; i < count; i++) SetInventorySlot(i, slots[i]);
                }
                else if (OpenWindow?.Id == window)
                {
                    OpenWindow.StateId = stateId;
                    OpenWindow.Slots = slots;
                    MirrorPlayerSlots(OpenWindow);
                }
                return;
            }
            case CB.Play.Clientbound.ContainerSetSlot:
            {
                var window = r.ReadVarInt();
                var stateId = r.ReadVarInt();
                var slot = r.ReadShort();
                var stack = ItemStack.Read(ref r);
                if (window == -1 && slot == -1) Inventory.Cursor = stack;
                else if (window == 0)
                {
                    InventoryStateId = stateId;
                    SetInventorySlot(slot, stack);
                }
                else if (window == -2) SetInventorySlot(slot, stack); // vanilla: -2 = player inventory, no state change
                else if (OpenWindow?.Id == window && (uint)slot < (uint)OpenWindow.Slots.Length)
                {
                    OpenWindow.StateId = stateId;
                    OpenWindow.Slots[slot] = stack;
                    MirrorPlayerSlots(OpenWindow);
                }
                return;
            }
            case CB.Play.Clientbound.SetPlayerInventory:
            {
                var index = r.ReadVarInt();
                var stack = ItemStack.Read(ref r);
                SetInventorySlot(PlayerInventory.FromInventoryIndex(index), stack);
                return;
            }
            case CB.Play.Clientbound.SetCursorItem:
                Inventory.Cursor = ItemStack.Read(ref r);
                return;
            case CB.Play.Clientbound.SetHeldSlot:
                Inventory.SelectedHotbar = r.ReadVarInt();
                return;
            case CB.Play.Clientbound.OpenScreen:
            {
                var id = r.ReadVarInt();
                var type = r.ReadVarInt();
                var title = TextComponent.ToPlainText(r.ReadNbt());
                OpenWindow = new ContainerWindow(id, type, title);
                WindowOpened?.Invoke(OpenWindow);
                return;
            }
            case CB.Play.Clientbound.ContainerClose:
            {
                var id = r.ReadVarInt();
                if (OpenWindow?.Id == id) OpenWindow = null;
                WindowClosed?.Invoke(id);
                return;
            }
            case CB.Play.Clientbound.SetExperience:
                ExperienceProgress = r.ReadFloat();
                ExperienceLevel = r.ReadVarInt();
                TotalExperience = r.ReadVarInt();
                return;
        }
    }

    private void SetInventorySlot(int slot, ItemStack stack)
    {
        if ((uint)slot >= PlayerInventory.Size) return;
        Inventory.Set(slot, stack);
        InventoryChanged?.Invoke(slot, stack);
    }

    // An open container's last 36 slots ARE the player's main inventory (27) and hotbar (9).
    private void MirrorPlayerSlots(ContainerWindow window)
    {
        var start = window.Slots.Length - 36;
        if (start < 0) return;
        for (var i = 0; i < 36; i++) SetInventorySlot(PlayerInventory.MainStart + i, window.Slots[start + i]);
    }
}
