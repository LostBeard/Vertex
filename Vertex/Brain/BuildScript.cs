using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Data;

namespace Vertex.Brain;

/// <summary>One block of a build: where (relative to the build's corner), what, and how it faces.</summary>
/// <param name="Late">Placed after everything else (a door, a torch inside): the doorway has to stay open until the builder is done inside.</param>
public sealed record BuildCell(int X, int Y, int Z, string Item, BlockFace? Facing = null, bool Late = false);

/// <summary>What a build may be: a language model writes these scripts, so every number is bounded.</summary>
public sealed record BuildLimits(int MaxCells = 1500, int MaxSize = 24, int MaxHeight = 6, int MaxMaterials = 6);

/// <summary>Why a script could not be used, in words for the model to correct itself (and for the person to hear).</summary>
public sealed class BuildScriptException(string message) : Exception(message);

/// <summary>
/// A build written as a few lines of script (by the language model, or by one of the shapes), checked and turned into blocks. Coordinates are relative to the build's corner: x east, y up
/// (0 = the ground level it stands on), z south. Later lines win where they cover the same cell.
/// </summary>
/// <remarks>
/// <code>
/// fill x1 y1 z1 x2 y2 z2 MATERIAL     a solid box
/// hollow x1 y1 z1 x2 y2 z2 MATERIAL   a box that is only its shell (floor, walls and ceiling)
/// walls x1 y1 z1 x2 y2 z2 MATERIAL    the four walls only
/// floor x1 z1 x2 z2 y MATERIAL        one flat layer
/// roof flat|gable|pyramid x1 z1 x2 z2 y MATERIAL    a roof on a rectangle; gable's ridge runs along the longer side; stepped full blocks
/// line x1 y1 z1 x2 y2 z2 MATERIAL    blocks in a straight line (along one axis)
/// block x y z MATERIAL [north|south|east|west]
/// door x y z north|south|east|west   a door (lower half's cell), put in last so the doorway stays open while building
/// torch x y z                        a torch, put in last
/// clear x1 y1 z1 x2 y2 z2            takes cells OUT of the plan (cut a doorway or a window in a wall)
/// </code>
/// Materials are block names: cobblestone, stone bricks, planks (any), oak planks, glass, bricks, dirt...
/// </remarks>
public sealed class BuildScript
{
    public const string Grammar =
        "One command per line, coordinates relative to the build's corner (x east, y up from the ground, z south): " +
        "fill x1 y1 z1 x2 y2 z2 MATERIAL | hollow x1 y1 z1 x2 y2 z2 MATERIAL (box shell) | walls x1 y1 z1 x2 y2 z2 MATERIAL | floor x1 z1 x2 z2 y MATERIAL | " +
        "roof flat/gable/pyramid x1 z1 x2 z2 y MATERIAL | line x1 y1 z1 x2 y2 z2 MATERIAL | block x y z MATERIAL | door x y z north/south/east/west | torch x y z | clear x1 y1 z1 x2 y2 z2 (removes cells, e.g. a doorway).";

    private BuildScript(IReadOnlyList<BuildCell> cells, int width, int depth, int height)
    {
        Cells = cells;
        Width = width;
        Depth = depth;
        Height = height;
        Needs = cells.GroupBy(c => c.Item).ToDictionary(g => g.Key, g => g.Count());
    }

    public IReadOnlyList<BuildCell> Cells { get; }
    public int Width { get; }
    public int Depth { get; }
    public int Height { get; }

    /// <summary>How many of each item the build uses ("planks" means any planks).</summary>
    public IReadOnlyDictionary<string, int> Needs { get; }

    // ---------------------------------------------------------------- parsing

    public static BuildScript Parse(string text, GameData data, BuildLimits? limits = null)
    {
        limits ??= new BuildLimits();
        var cells = new Dictionary<(int X, int Y, int Z), BuildCell>();
        var lineNo = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n', ';'))
        {
            lineNo++;
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var tokens = line.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            var cmd = tokens[0].ToLowerInvariant();
            try { Apply(cmd, tokens[1..], cells, data, limits); }
            catch (BuildScriptException e) { throw new BuildScriptException($"line {lineNo} (\"{string.Join(' ', tokens)}\"): {e.Message}"); }
            if (cells.Count > limits.MaxCells) throw new BuildScriptException($"line {lineNo}: that is more than {limits.MaxCells} blocks; build something smaller.");
        }
        if (cells.Count == 0) throw new BuildScriptException("The script builds nothing.");
        var list = cells.Values.OrderBy(c => c.Y).ThenBy(c => c.Z).ThenBy(c => c.X).ToList();
        var materials = list.Select(c => c.Item).Distinct().Count();
        if (materials > limits.MaxMaterials) throw new BuildScriptException($"That uses {materials} different materials; use at most {limits.MaxMaterials}.");
        return new BuildScript(list, list.Max(c => c.X) + 1, list.Max(c => c.Z) + 1, list.Max(c => c.Y) + 1);
    }

    private static void Apply(string cmd, string[] a, Dictionary<(int, int, int), BuildCell> cells, GameData data, BuildLimits lim)
    {
        void Put(int x, int y, int z, string item, BlockFace? facing = null, bool late = false)
        {
            if (x < 0 || z < 0 || x >= lim.MaxSize || z >= lim.MaxSize) throw new BuildScriptException($"x and z must be between 0 and {lim.MaxSize - 1} (got {x} and {z}).");
            if (y < 0 || y >= lim.MaxHeight) throw new BuildScriptException($"y must be between 0 and {lim.MaxHeight - 1} (got {y}): I can only reach that high from the ground.");
            cells[(x, y, z)] = new BuildCell(x, y, z, item, facing, late);
        }
        int N(int i) => i < a.Length && int.TryParse(a[i], out var v) ? v : throw new BuildScriptException($"expected a number at word {i + 1}.");
        string Material(int from) => ResolveMaterial(string.Join(' ', a.Skip(from)), data);

        switch (cmd)
        {
            case "fill" or "hollow" or "walls":
            {
                int x1 = N(0), y1 = N(1), z1 = N(2), x2 = N(3), y2 = N(4), z2 = N(5);
                var item = Material(6);
                (x1, x2) = (Math.Min(x1, x2), Math.Max(x1, x2));
                (y1, y2) = (Math.Min(y1, y2), Math.Max(y1, y2));
                (z1, z2) = (Math.Min(z1, z2), Math.Max(z1, z2));
                for (var y = y1; y <= y2; y++)
                for (var z = z1; z <= z2; z++)
                for (var x = x1; x <= x2; x++)
                {
                    var onWall = x == x1 || x == x2 || z == z1 || z == z2;
                    var onShell = onWall || y == y1 || y == y2;
                    if (cmd == "fill" || (cmd == "walls" && onWall) || (cmd == "hollow" && onShell)) Put(x, y, z, item);
                }
                break;
            }
            case "floor":
            {
                int x1 = N(0), z1 = N(1), x2 = N(2), z2 = N(3), y = N(4);
                var item = Material(5);
                for (var z = Math.Min(z1, z2); z <= Math.Max(z1, z2); z++)
                for (var x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++) Put(x, y, z, item);
                break;
            }
            case "roof":
            {
                if (a.Length < 1) throw new BuildScriptException("roof what? flat, gable or pyramid.");
                var kind = a[0].ToLowerInvariant();
                var b = a[1..];
                int Nb(int i) => i < b.Length && int.TryParse(b[i], out var v) ? v : throw new BuildScriptException($"expected a number at word {i + 2}.");
                int x1 = Nb(0), z1 = Nb(1), x2 = Nb(2), z2 = Nb(3), y = Nb(4);
                var item = ResolveMaterial(string.Join(' ', b.Skip(5)), data);
                (x1, x2) = (Math.Min(x1, x2), Math.Max(x1, x2));
                (z1, z2) = (Math.Min(z1, z2), Math.Max(z1, z2));
                switch (kind)
                {
                    case "flat":
                        for (var z = z1; z <= z2; z++) for (var x = x1; x <= x2; x++) Put(x, y, z, item);
                        break;
                    case "pyramid":
                        for (int k = 0; x1 + k <= x2 - k && z1 + k <= z2 - k; k++)
                            for (var z = z1 + k; z <= z2 - k; z++) for (var x = x1 + k; x <= x2 - k; x++) Put(x, y + k, z, item);
                        break;
                    case "gable":
                    {
                        // The ridge runs along the longer side; each layer up is one block narrower on both long edges.
                        var alongX = x2 - x1 >= z2 - z1;
                        for (var k = 0; ; k++)
                        {
                            var (a1, a2) = alongX ? (z1 + k, z2 - k) : (x1 + k, x2 - k);
                            if (a1 > a2) break;
                            for (var m = alongX ? x1 : z1; m <= (alongX ? x2 : z2); m++)
                                for (var c = a1; c <= a2; c++) { if (alongX) Put(m, y + k, c, item); else Put(c, y + k, m, item); }
                        }
                        break;
                    }
                    default: throw new BuildScriptException($"a roof is flat, gable or pyramid, not \"{kind}\".");
                }
                break;
            }
            case "line":
            {
                int x1 = N(0), y1 = N(1), z1 = N(2), x2 = N(3), y2 = N(4), z2 = N(5);
                var item = Material(6);
                var steps = Math.Max(Math.Abs(x2 - x1), Math.Max(Math.Abs(y2 - y1), Math.Abs(z2 - z1)));
                for (var i = 0; i <= steps; i++)
                    Put(steps == 0 ? x1 : x1 + (int)Math.Round((x2 - x1) * (double)i / steps), steps == 0 ? y1 : y1 + (int)Math.Round((y2 - y1) * (double)i / steps),
                        steps == 0 ? z1 : z1 + (int)Math.Round((z2 - z1) * (double)i / steps), item);
                break;
            }
            case "block":
            {
                int x = N(0), y = N(1), z = N(2);
                BlockFace? facing = null;
                var rest = a.Skip(3).ToList();
                if (rest.Count > 1 && ParseFacing(rest[^1]) is { } f) { facing = f; rest.RemoveAt(rest.Count - 1); }
                Put(x, y, z, ResolveMaterial(string.Join(' ', rest), data), facing);
                break;
            }
            case "door":
            {
                int x = N(0), y = N(1), z = N(2);
                var facing = a.Length > 3 ? ParseFacing(a[3]) : null;
                if (facing == null) throw new BuildScriptException("a door needs a side: north, south, east or west.");
                Put(x, y, z, "door", facing, late: true);
                break;
            }
            case "torch":
                Put(N(0), N(1), N(2), "minecraft:torch", late: true);
                break;
            case "clear":
            {
                int x1 = N(0), y1 = N(1), z1 = N(2), x2 = N(3), y2 = N(4), z2 = N(5);
                for (var y = Math.Min(y1, y2); y <= Math.Max(y1, y2); y++)
                for (var z = Math.Min(z1, z2); z <= Math.Max(z1, z2); z++)
                for (var x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++) cells.Remove((x, y, z));
                break;
            }
            default:
                throw new BuildScriptException($"I don't know the command \"{cmd}\". " + Grammar);
        }
    }

    private static BlockFace? ParseFacing(string word) => word.ToLowerInvariant() switch
    {
        "north" or "n" => BlockFace.North, "south" or "s" => BlockFace.South, "east" or "e" => BlockFace.East, "west" or "w" => BlockFace.West, _ => null,
    };

    /// <summary>
    /// A block name as people say it ("stone bricks", "oak planks", "glass") to the item name we place. "planks" (also wood, wooden, plank) means any planks. Null-safe: a name that is not a
    /// block we can place is an error the model can read.
    /// </summary>
    public static string ResolveMaterial(string words, GameData data)
    {
        var w = words.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (w.Length == 0) throw new BuildScriptException("a block name is missing.");
        if (w.StartsWith("minecraft:")) w = w[10..];
        switch (w)
        {
            case "wood" or "wooden" or "plank" or "planks" or "wood_planks" or "wooden_planks": return "planks";
            case "cobble" or "cobbles": return "minecraft:cobblestone";
            case "stone_brick" or "stonebrick" or "stonebricks": return "minecraft:stone_bricks";
            case "brick": return "minecraft:bricks";
            case "door" or "torch": throw new BuildScriptException($"use the {w} command for a {w}.");
        }
        foreach (var candidate in new[] { w, w.EndsWith("s") ? w[..^1] : w + "s" })
            if (data.Item("minecraft:" + candidate) is { Block: not null } item) return item.Name;
        throw new BuildScriptException($"I don't know a block called \"{words.Trim()}\" that I can build with.");
    }

    /// <summary>Whether an item stack is what a cell wants ("planks" any planks, "door" any wooden door).</summary>
    public static Func<SpawnDev.Minecraft.Items.ItemStack, bool> Matcher(string item) => item switch
    {
        "door" => Blueprints.IsDoor,
        _ => Blueprints.MaterialMatch(item),
    };

    /// <summary>What to call an item when talking: "planks", "stone bricks".</summary>
    public static string Pretty(string item) => item == "planks" ? "planks" : item.Replace("minecraft:", "").Replace('_', ' ');

    // ---------------------------------------------------------------- order

    /// <summary>
    /// The order to place cells in: layer by layer from the ground up, and within a layer always a cell that has something to click against (the ground, a block already placed, or
    /// one of the world's), nearest to the last one placed so the builder does not walk back and forth. Late cells (doors, torches) go last. Cells with nothing to lean on ever (floating)
    /// go after the rest, where they will most likely fail and be reported rather than hold everything else up.
    /// </summary>
    /// <param name="solidAt">Whether the world already has something solid at a (world) position.</param>
    public static List<BuildCell> Order(IEnumerable<BuildCell> cells, Func<int, int, int, bool> solidAt)
    {
        var remaining = cells.Where(c => !c.Late).ToDictionary(c => (c.X, c.Y, c.Z));
        var late = cells.Where(c => c.Late).OrderBy(c => c.Y).ThenBy(c => c.Z).ThenBy(c => c.X).ToList();
        var placed = new HashSet<(int, int, int)>();
        var order = new List<BuildCell>(remaining.Count + late.Count);
        bool Solid(int x, int y, int z) => placed.Contains((x, y, z)) || solidAt(x, y, z);
        bool Supported(BuildCell c) => Solid(c.X, c.Y - 1, c.Z) || Solid(c.X + 1, c.Y, c.Z) || Solid(c.X - 1, c.Y, c.Z) || Solid(c.X, c.Y, c.Z + 1) || Solid(c.X, c.Y, c.Z - 1) || Solid(c.X, c.Y + 1, c.Z);

        BuildCell? last = null;
        while (remaining.Count > 0)
        {
            var ready = remaining.Values.Where(Supported).ToList();
            if (ready.Count == 0) break;
            var layer = ready.Min(c => c.Y);
            var pick = ready.Where(c => c.Y == layer)
                .OrderBy(c => last == null ? 0 : Math.Abs(c.X - last.X) + Math.Abs(c.Z - last.Z)).ThenBy(c => c.Z).ThenBy(c => c.X).First();
            order.Add(pick);
            placed.Add((pick.X, pick.Y, pick.Z));
            remaining.Remove((pick.X, pick.Y, pick.Z));
            last = pick;
        }
        order.AddRange(remaining.Values.OrderBy(c => c.Y).ThenBy(c => c.Z).ThenBy(c => c.X));
        order.AddRange(late);
        return order;
    }
}
