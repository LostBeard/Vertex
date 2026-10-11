using System.Text;
using SpawnDev.Minecraft.Protocol;
using CB = SpawnDev.Minecraft.Protocol.PacketIds;

namespace SpawnDev.Minecraft;

// Fabric servers: answer Fabric API's registry sync (fabric-registry-sync-v0, 1.21.5 branch), which modded servers
// require before letting a client in ("This server requires Fabric Loader and Fabric API installed on your client!").
public sealed partial class MinecraftClient
{
    private const string FabricSyncChannel = "fabric:registry/sync/direct";
    private const string FabricSyncCompleteChannel = "fabric:registry/sync/complete";
    private MemoryStream? _fabricSync;

    /// <summary>
    /// The registries a Fabric server synced (registry -> entry name -> raw id), including modded entries. Empty on
    /// vanilla/Paper servers.
    /// </summary>
    public IReadOnlyDictionary<string, Dictionary<string, int>> FabricRegistries => _fabricRegistries;
    private readonly Dictionary<string, Dictionary<string, int>> _fabricRegistries = [];

    /// <summary>True when the server is a Fabric server that synced modded registries to us.</summary>
    public bool IsModded => _fabricRegistries.Count > 0;

    private async ValueTask RegisterFabricChannelsAsync(CancellationToken ct)
    {
        // A Fabric server only sends the sync to clients that registered the channel.
        using var p = new PacketWriter(CB.Configuration.Serverbound.CustomPayload);
        p.WriteString("minecraft:register").WriteBytes(Encoding.UTF8.GetBytes(FabricSyncChannel + "\0" + FabricSyncCompleteChannel));
        await _connection!.SendAsync(p, ct);
    }

    /// <summary>Handles fabric:registry/sync/direct; true when the payload was Fabric's.</summary>
    private async ValueTask<bool> HandleFabricPayloadAsync(Net.ReceivedPacket packet, CancellationToken ct)
    {
        byte[] data;
        {
            var r = packet.Reader();
            if (r.ReadString() != FabricSyncChannel) return false;
            data = r.ReadRest().ToArray();
        }
        _fabricSync ??= new MemoryStream();
        if (data.Length > 0)
        {
            _fabricSync.Write(data); // the map is split over payloads; an empty one ends it
            return true;
        }
        var map = ParseFabricRegistryMap(_fabricSync.ToArray());
        _fabricSync = null;
        _fabricRegistries.Clear();
        foreach (var (k, v) in map) _fabricRegistries[k] = v;
        RebuildWorldData();
        Log($"Fabric registry sync: {map.Count} registries ({string.Join(", ", map.Select(m => $"{m.Key.Replace("minecraft:", "")} {m.Value.Count}"))})");
        using var done = new PacketWriter(CB.Configuration.Serverbound.CustomPayload);
        done.WriteString(FabricSyncCompleteChannel);
        await _connection!.SendAsync(done, ct);
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex PuzzlesLibChannel = new(@"^[a-z0-9_.\-]+:main/\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Mods built on Puzzles Lib (Mutant Monsters and the other Fuzs mods) spawn their entities with a custom payload
    /// on "&lt;mod&gt;:main/&lt;n&gt;" instead of add_entity: the vanilla add_entity fields followed by the mod's NBT (format
    /// read from a live capture: entity id, UUID, type 157 = mutantmonsters:mutant_zombie, x y z, angles, data,
    /// velocity, then a compound tag). Only accepted when it decodes as exactly that: a modded entity type from the
    /// server's registry sync, a position near us, and an NBT tag right after the velocity.
    /// </summary>
    private bool TryModdedSpawn(Net.ReceivedPacket packet)
    {
        var r = packet.Reader();
        var channel = r.ReadString();
        if (!PuzzlesLibChannel.IsMatch(channel) || !FabricRegistries.TryGetValue("minecraft:entity_type", out var types)) return false;
        try
        {
            var id = r.ReadVarInt();
            var uuid = r.ReadUuid();
            var typeId = r.ReadVarInt();
            if (typeId < Data.GameData.Vanilla.Entities.Length || !types.ContainsValue(typeId)) return false;
            double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
            var (px, py, pz) = Position;
            if (!double.IsFinite(x) || Math.Abs(x - px) > 512 || Math.Abs(z - pz) > 512 || y < World.MinY - 64 || y > World.MaxY + 64) return false;
            sbyte pitch = r.ReadSByte(), yaw = r.ReadSByte(), head = r.ReadSByte();
            var data = r.ReadVarInt();
            short vx = r.ReadShort(), vy = r.ReadShort(), vz = r.ReadShort();
            if (r.Remaining > 0 && r.Rest[0] is not (0x0A or 0x00)) return false;
            var e = new Entities.Entity(id, uuid, ModdedEntityType(typeId))
            {
                X = x, Y = y, Z = z,
                Pitch = (float)(pitch * 360.0 / 256), Yaw = (float)(yaw * 360.0 / 256), HeadYaw = (float)(head * 360.0 / 256),
                Data = data, VelocityX = vx / 8000.0, VelocityY = vy / 8000.0, VelocityZ = vz / 8000.0,
            };
            _entities[id] = e;
            OnEntitySpawned(e);
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>
    /// DirectRegistryPacketHandler's format: registries grouped by namespace ("" = minecraft), each with an
    /// attribute byte and its ids grouped by namespace, in runs of consecutive raw ids (start given as a delta from
    /// the previous run's last id).
    /// </summary>
    internal static Dictionary<string, Dictionary<string, int>> ParseFabricRegistryMap(byte[] buffer)
    {
        static string Ns(string s) => s.Length == 0 ? "minecraft" : s;
        var result = new Dictionary<string, Dictionary<string, int>>();
        var r = new PacketReader(buffer);
        var registryNamespaces = r.ReadVarInt();
        for (var i = 0; i < registryNamespaces; i++)
        {
            var regNs = Ns(r.ReadString());
            var count = r.ReadVarInt();
            for (var j = 0; j < count; j++)
            {
                var regPath = r.ReadString();
                r.ReadByte(); // attributes
                var ids = new Dictionary<string, int>();
                var idNamespaces = r.ReadVarInt();
                var lastRunLast = 0;
                for (var k = 0; k < idNamespaces; k++)
                {
                    var idNs = Ns(r.ReadString());
                    var runs = r.ReadVarInt();
                    for (var l = 0; l < runs; l++)
                    {
                        var startDiff = r.ReadVarInt();
                        var size = r.ReadVarInt();
                        var raw = lastRunLast + startDiff - 1;
                        for (var m = 0; m < size; m++)
                        {
                            raw++;
                            ids[idNs + ":" + r.ReadString()] = raw;
                        }
                        lastRunLast = raw;
                    }
                }
                result[regNs + ":" + regPath] = ids;
            }
        }
        return result;
    }
}
