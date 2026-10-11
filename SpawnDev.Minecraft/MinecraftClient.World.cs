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

    /// <summary>Our own pose, as the server set it (Sleeping while in a bed).</summary>
    public EntityPose Pose { get; private set; }

    public bool Sleeping => Pose == EntityPose.Sleeping;
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

    private void OnEntitySpawned(Entity e) => EntitySpawned?.Invoke(e);

    /// <summary>The tag's member ids in a registry ("minecraft:block", "minecraft:item"...), from the server.</summary>
    public IReadOnlyCollection<int> Tag(string registry, string tag) =>
        _tags.TryGetValue(GameData.Normalize(registry), out var r) && r.TryGetValue(GameData.Normalize(tag.TrimStart('#')), out var ids) ? ids : [];

    /// <summary>
    /// An attribute's current value for our player, as the vanilla client sees it: the server's value (base +
    /// modifiers) for attributes it syncs; for the ones it does not (attack_damage, attack_speed...) the game's
    /// base value plus the attribute modifiers of the items in our hands and armor slots. Null if unknown.
    /// </summary>
    public double? Attribute(string name)
    {
        name = GameData.Normalize(name);
        var names = GameData.Vanilla.Registries.GetValueOrDefault("minecraft:attribute") ?? [];
        var id = Array.IndexOf(names, name);
        if (id >= 0 && _attributes.TryGetValue(id, out var a)) return Apply(a.Base, a.Modifiers.Select(m => (m.Amount, m.Operation)));
        if (!GameData.Vanilla.PlayerAttributes.TryGetValue(name, out var baseValue)) return null;
        return Apply(baseValue, EquipmentModifiers(name));
    }

    private static double Apply(double baseValue, IEnumerable<(double Amount, int Operation)> modifiers)
    {
        var list = modifiers.ToList();
        var value = baseValue;
        foreach (var m in list) if (m.Operation == 0) value += m.Amount;
        var result = value;
        foreach (var m in list) if (m.Operation == 1) result += value * m.Amount;
        foreach (var m in list) if (m.Operation == 2) result *= 1 + m.Amount;
        return result;
    }

    /// <summary>The modifiers our equipment adds to one attribute (main hand, off hand, armor).</summary>
    private IEnumerable<(double Amount, int Operation)> EquipmentModifiers(string attribute)
    {
        var equipped = new (ItemStack Stack, string Slot)[]
        {
            (Inventory.MainHand, "mainhand"), (Inventory.OffHandItem, "offhand"),
            (Inventory.Head, "head"), (Inventory.Chest, "chest"), (Inventory.Legs, "legs"), (Inventory.Feet, "feet"),
        };
        foreach (var (stack, slot) in equipped)
        {
            if (stack.IsEmpty) continue;
            foreach (var m in ItemModifiers(stack))
                if (m.Attribute == attribute && SlotApplies(m.Slot, slot)) yield return (m.Amount, m.Operation);
        }
    }

    private static bool SlotApplies(string group, string slot) => group switch
    {
        "any" => true,
        "hand" => slot is "mainhand" or "offhand",
        "armor" => slot is "head" or "chest" or "legs" or "feet",
        _ => group == slot,
    };

    /// <summary>
    /// An item's attribute modifiers: the stack's own attribute_modifiers component when it has one (decoded by
    /// ProtoDef: typeId/value/operation/slot), else the item's defaults from the game data.
    /// </summary>
    public static IEnumerable<(string Attribute, double Amount, int Operation, string Slot)> ItemModifiers(ItemStack stack)
    {
        static string NormalizeSlot(string s) => s.Replace("_", "");
        static int Op(string o) => o switch { "add" or "add_value" => 0, "multiply_base" or "add_multiplied_base" => 1, _ => 2 };
        if (stack.Components.TryGetValue("attribute_modifiers", out var own) && own is List<object?> list)
        {
            foreach (var e in list)
                if (e is Dictionary<string, object?> d && GameData.Vanilla.RegistryName("minecraft:attribute", Convert.ToInt32(d["typeId"])) is string attr)
                    yield return (attr, Convert.ToDouble(d["value"]), Op(d["operation"] as string ?? "add"), NormalizeSlot(d["slot"] as string ?? "any"));
            yield break;
        }
        if (stack.Type?.Components["attribute_modifiers"] is System.Text.Json.Nodes.JsonArray defaults)
            foreach (var e in defaults)
                yield return (e!["type"]!.GetValue<string>(), e["amount"]!.GetValue<double>(), Op(e["operation"]!.GetValue<string>()), NormalizeSlot(e["slot"]?.GetValue<string>() ?? "any"));
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
        RebuildWorldData();
    }

    /// <summary>
    /// The block tables for this server: vanilla, or (on a Fabric server) vanilla plus the modded blocks it synced
    /// that tools/ModDump has data for, with the server's block tags (which put modded doors, ladders... in the
    /// vanilla tags pathing and physics read). Rebuilt when either arrives; physics picks the new table up.
    /// </summary>
    private void RebuildWorldData()
    {
        var data = GameData.Vanilla;
        if (_fabricRegistries.TryGetValue("minecraft:block", out var blocks))
        {
            data = data.WithModdedBlocks(blocks);
            if (_tags.TryGetValue("minecraft:block", out var blockTags)) data = data.WithBlockTags(blockTags);
        }
        if (ReferenceEquals(data, World.Data)) return;
        World.Data = data;
        _physics = null;
        if (data.ModdedBlockCount > 0)
            Log($"modded blocks: {data.ModdedBlockCount} with data ({data.KnownStateCount - data.VanillaStateCount} states)"
                + (blocks!.Count > data.Blocks.Length - 1 ? $", {blocks.Count - (data.Blocks.Length - 1)} without (solid unknown blocks)" : ""));
    }

    private static double Angle(sbyte b) => b * 360.0 / 256.0;

    /// <summary>An entity type beyond the vanilla table: its name from a Fabric server's registry sync, if any.</summary>
    private EntityTypeInfo ModdedEntityType(int id)
    {
        var name = FabricRegistries.TryGetValue("minecraft:entity_type", out var map) ? map.FirstOrDefault(kv => kv.Value == id).Key : null;
        return new EntityTypeInfo(id, name ?? $"unknown:{id}", 0.6f, 1.8f, 1.6f);
    }

    private void OnWorldPacket(ReceivedPacket packet)
    {
        var r = packet.Reader();
        switch (packet.Id)
        {
            case CB.Play.Clientbound.LevelChunkWithLight:
            {
                var column = ChunkColumn.Read(ref r, World.MinY, World.Height, World.Data);
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
                var typeId = r.ReadVarInt();
                var type = GameData.Vanilla.Entity(typeId) ?? ModdedEntityType(typeId);
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
                OnEntitySpawned(e);
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
                var id = r.ReadVarInt();
                var e = Entity(id);
                var entries = ProtoDef.Vanilla.Read(ref r, "entityMetadata") as List<object?>;
                if (entries == null) return;
                if (id == EntityId)
                {
                    // Our own data: the pose (index 6) says when we are asleep in a bed.
                    foreach (var entry in entries)
                        if (entry is Dictionary<string, object?> m && Convert.ToInt32(m["key"]) == 6 && m.GetValueOrDefault("value") is { } pose)
                            Pose = (EntityPose)Convert.ToInt32(pose);
                    return;
                }
                if (e == null) return;
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
            case CB.Play.Clientbound.UpdateMobEffect:
            {
                var id = r.ReadVarInt();
                var effect = GameData.Vanilla.RegistryName("minecraft:mob_effect", r.ReadVarInt());
                var amplifier = r.ReadVarInt();
                if (id == EntityId && effect != null) _effects[effect] = amplifier;
                return;
            }
            case CB.Play.Clientbound.RemoveMobEffect:
            {
                var id = r.ReadVarInt();
                var effect = GameData.Vanilla.RegistryName("minecraft:mob_effect", r.ReadVarInt());
                if (id == EntityId && effect != null) _effects.TryRemove(effect, out _);
                return;
            }
            case CB.Play.Clientbound.PlayerAbilities:
            {
                var flags = r.ReadByte();
                var fly = r.ReadFloat();
                r.ReadFloat(); // field-of-view modifier (walking speed)
                OnAbilities(flags, fly);
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
        if (slot == PlayerInventory.HotbarStart + Inventory.SelectedHotbar && Inventory[slot].ItemId != stack.ItemId) _lastHeldChangeTick = Ticks;
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
