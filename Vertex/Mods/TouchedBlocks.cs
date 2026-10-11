using System.Text.Json;
using System.Text.Json.Serialization;
using SpawnDev.Minecraft.Data;

namespace Vertex.Mods;

/// <summary>
/// What Vertex learned by experience about one server's blocks (raw state id -> shapes felt, doors opened, names,
/// dig times), saved per world. Raw state ids mean the same blocks only while the server's block list is the same,
/// so they are kept with its fingerprint (MinecraftClient.BlockRegistryFingerprint) and ignored when the mods change.
/// </summary>
public sealed record TouchedBlocks(string Fingerprint, Dictionary<int, LearnedState> States)
{
    private sealed record SavedState(double[][]? Shape, bool Sticky, string? Name, int? UseTurnsInto, bool UseDoesNothing,
        bool Doorway, bool Open, bool AlongX, int? DigTicks, string? DigTool);

    // Shapes + Sticky: the first format (shapes only), still read.
    private sealed record Saved(string Fingerprint, Dictionary<string, SavedState>? States, Dictionary<string, double[][]>? Shapes = null, List<int>? Sticky = null);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    private static Box[] Boxes(double[][] b) => b.Select(a => new Box(a[0], a[1], a[2], a[3], a[4], a[5])).ToArray();

    public static TouchedBlocks? Load(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), Options);
            if (s == null) return null;
            var states = new Dictionary<int, LearnedState>();
            foreach (var (k, v) in s.States ?? [])
                states[int.Parse(k)] = new LearnedState
                {
                    Shape = v.Shape == null ? null : Boxes(v.Shape), Sticky = v.Sticky, Name = v.Name, UseTurnsInto = v.UseTurnsInto,
                    UseDoesNothing = v.UseDoesNothing, Doorway = v.Doorway, Open = v.Open, AlongX = v.AlongX, DigTicks = v.DigTicks, DigTool = v.DigTool,
                };
            foreach (var (k, v) in s.Shapes ?? [])
                states[int.Parse(k)] = new LearnedState { Shape = Boxes(v), Sticky = s.Sticky?.Contains(int.Parse(k)) == true };
            return new TouchedBlocks(s.Fingerprint, states);
        }
        catch (Exception e) when (e is JsonException or FormatException or IndexOutOfRangeException or NullReferenceException) { return null; }
    }

    public static void Save(string file, TouchedBlocks t)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var saved = new Saved(t.Fingerprint, t.States.ToDictionary(kv => kv.Key.ToString(), kv => new SavedState(
            kv.Value.Shape?.Select(b => new[] { b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ }).ToArray(), kv.Value.Sticky, kv.Value.Name,
            kv.Value.UseTurnsInto, kv.Value.UseDoesNothing, kv.Value.Doorway, kv.Value.Open, kv.Value.AlongX, kv.Value.DigTicks, kv.Value.DigTool)));
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(saved, Options));
        File.Move(temp, file, overwrite: true);
    }
}
