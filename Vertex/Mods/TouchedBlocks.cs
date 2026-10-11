using System.Text.Json;
using SpawnDev.Minecraft.Data;

namespace Vertex.Mods;

/// <summary>
/// Block shapes Vertex learned by touch on one server (raw state id -> boxes, and which ones hold you), saved per
/// world. Raw state ids mean the same blocks only while the server's block list is the same, so they are kept
/// with its fingerprint (MinecraftClient.BlockRegistryFingerprint) and ignored when the mods change.
/// </summary>
public sealed record TouchedBlocks(string Fingerprint, Dictionary<int, Box[]> Shapes, List<int> Sticky)
{
    private sealed record Saved(string Fingerprint, Dictionary<string, double[][]> Shapes, List<int> Sticky);

    public static TouchedBlocks? Load(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file));
            if (s == null) return null;
            return new TouchedBlocks(s.Fingerprint,
                s.Shapes.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value.Select(b => new Box(b[0], b[1], b[2], b[3], b[4], b[5])).ToArray()),
                s.Sticky ?? []);
        }
        catch (Exception e) when (e is JsonException or FormatException or IndexOutOfRangeException or NullReferenceException) { return null; }
    }

    public static void Save(string file, TouchedBlocks t)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var saved = new Saved(t.Fingerprint,
            t.Shapes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Select(b => new[] { b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ }).ToArray()),
            t.Sticky);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
    }
}
