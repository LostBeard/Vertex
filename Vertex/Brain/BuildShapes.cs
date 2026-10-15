using System.Text;
using SpawnDev.Minecraft;

namespace Vertex.Brain;

/// <summary>
/// Named things to build (a house, a platform, a pyramid, a castle) as <see cref="BuildScript"/> text, so a child can ask for "a castle" and the language model need only name the shape and
/// the size: the scripts are written here, in code, where every number can be checked, and then go through the same checking and builder as a script the model writes itself.
/// </summary>
public static class BuildShapes
{
    public static readonly string[] Names = ["house", "platform", "pyramid", "castle"];

    /// <summary>The shape a person's words mean ("cabin", "hut", "home" are a house; "deck", "floor" a platform), or null.</summary>
    public static string? Normalize(string? shape) => shape?.Trim().ToLowerInvariant().Replace(' ', '_') switch
    {
        "house" or "cabin" or "hut" or "home" or "cottage" or "shelter" or "small_house" => "house",
        "platform" or "floor" or "deck" or "foundation" or "patio" or "pad" => "platform",
        "pyramid" or "mound" => "pyramid",
        "castle" or "fort" or "fortress" or "keep" or "stronghold" => "castle",
        _ => null,
    };

    /// <summary>The size a shape has when none is asked for: width, depth, height (of the walls).</summary>
    public static (int Width, int Depth, int Height) Defaults(string shape) => shape switch
    {
        "house" => (5, 5, 3),
        "platform" => (7, 7, 1),
        "pyramid" => (9, 9, 1),
        _ => (13, 13, 3),
    };

    /// <summary>Clamps a requested size to what the shape can be (and what can be reached from the ground).</summary>
    public static (int Width, int Depth, int Height) Fit(string shape, int width, int depth, int height)
    {
        var (dw, dd, dh) = Defaults(shape);
        int W(int v, int min, int max) => Math.Clamp(v <= 0 ? 0 : v, min, max);
        width = width <= 0 ? dw : width;
        depth = depth <= 0 ? width == dw ? dd : width : depth;
        height = height <= 0 ? dh : height;
        return shape switch
        {
            "house" => (W(width, 3, 11), W(depth, 3, 11), Math.Clamp(height, 2, 4)),
            "platform" => (W(width, 1, 21), W(depth, 1, 21), 1),
            "pyramid" => (W(width, 3, 11), W(width, 3, 11), 1),          // square, and 11 wide is 6 layers high: as high as can be reached
            _ => (W(width, 9, 17), W(width, 9, 17), Math.Clamp(height, 2, 3)),
        };
    }

    /// <summary>
    /// The script for a shape. `wall` and `roof` are material names as <see cref="BuildScript.ResolveMaterial"/> takes them. `doorSide` is the side the doorway (house) or gate (castle) is on.
    /// A house gets glass windows, a door and a torch only when the builder has them (<paramref name="windows"/>, <paramref name="door"/>, <paramref name="torch"/>).
    /// </summary>
    public static string Script(string shape, int width, int depth, int height, string wall, string roof, BlockFace doorSide, bool windows, bool door, bool torch)
    {
        var sb = new StringBuilder();
        int w = width, d = depth, h = height;
        // The middle of each side, and which way the doorway cuts through it.
        (int X, int Z) Middle(BlockFace side) => side switch
        {
            BlockFace.North => ((w - 1) / 2, 0),
            BlockFace.South => ((w - 1) / 2, d - 1),
            BlockFace.West => (0, (d - 1) / 2),
            _ => (w - 1, (d - 1) / 2),
        };
        switch (shape)
        {
            case "platform":
                sb.AppendLine($"floor 0 0 {w - 1} {d - 1} 0 {wall}");
                break;
            case "pyramid":
                sb.AppendLine($"roof pyramid 0 0 {w - 1} {d - 1} 0 {wall}");
                break;
            case "house":
            {
                sb.AppendLine($"walls 0 0 0 {w - 1} {h - 1} {d - 1} {wall}");
                var (dx, dz) = Middle(doorSide);
                sb.AppendLine($"clear {dx} 0 {dz} {dx} {Math.Min(1, h - 1)} {dz}");
                if (windows)
                    foreach (var side in new[] { BlockFace.North, BlockFace.South, BlockFace.West, BlockFace.East }.Where(s => s != doorSide))
                    {
                        var length = side is BlockFace.North or BlockFace.South ? w : d;
                        if (length < 3) continue;
                        var (mx, mz) = Middle(side);
                        sb.AppendLine($"block {mx} {Math.Min(1, h - 1)} {mz} glass");
                    }
                // A pitched roof when it stays within reach from inside (its highest layer is at y = h + rise - 1); otherwise flat.
                var rise = (Math.Min(w, d) + 1) / 2;
                sb.AppendLine(h + rise - 1 <= 4 ? $"roof gable 0 0 {w - 1} {d - 1} {h} {roof}" : $"roof flat 0 0 {w - 1} {d - 1} {h} {roof}");
                if (door) sb.AppendLine($"door {dx} 0 {dz} {doorSide.ToString().ToLowerInvariant()}");
                if (torch && w > 2 && d > 2) sb.AppendLine("torch 1 0 1");
                break;
            }
            default: // castle: a curtain wall with a gate, a tower at each corner, and a battlement (every other block) along the top
            {
                var top = h;                                    // the walls are y 0..h-1; the battlements are at y = h
                sb.AppendLine($"walls 0 0 0 {w - 1} {h - 1} {d - 1} {wall}");
                // Battlements on the wall tops.
                for (var x = 0; x < w; x += 2) { sb.AppendLine($"block {x} {top} 0 {wall}"); sb.AppendLine($"block {x} {top} {d - 1} {wall}"); }
                for (var z = 2; z < d - 2; z += 2) { sb.AppendLine($"block 0 {top} {z} {wall}"); sb.AppendLine($"block {w - 1} {top} {z} {wall}"); }
                // Corner towers: 3 x 3, one higher than the wall, with their own battlements.
                var towerTop = h + 1;
                foreach (var (tx, tz) in new[] { (0, 0), (w - 3, 0), (0, d - 3), (w - 3, d - 3) })
                {
                    sb.AppendLine($"walls {tx} 0 {tz} {tx + 2} {towerTop - 1} {tz + 2} {wall}");
                    foreach (var (cx, cz) in new[] { (0, 0), (2, 0), (0, 2), (2, 2), (1, 0), (1, 2), (0, 1), (2, 1) }.Where(c => (c.Item1 + c.Item2) % 2 == 0))
                        sb.AppendLine($"block {tx + cx} {towerTop} {tz + cz} {wall}");
                }
                // The gate: three wide, two high, in the middle of the gate side (an opening, no door).
                var (gx, gz) = Middle(doorSide);
                if (doorSide is BlockFace.North or BlockFace.South) sb.AppendLine($"clear {gx - 1} 0 {gz} {gx + 1} 1 {gz}");
                else sb.AppendLine($"clear {gx} 0 {gz - 1} {gx} 1 {gz + 1}");
                if (torch) { sb.AppendLine($"torch {w / 2} 0 {d / 2}"); }
                break;
            }
        }
        return sb.ToString();
    }
}
