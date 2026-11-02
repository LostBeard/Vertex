using System.Text;
using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.World;

namespace Vertex.Brain;

/// <summary>What the top of the ground is, at one spot, as a player looking out over it would tell it.</summary>
public enum SurfaceKind { Land, Water, Lava, Void, Unknown }

/// <summary>One sample along a line out from him: how far, what the surface there is, how high it is, and where (the column x and z).</summary>
public readonly record struct SurfaceSample(int Distance, SurfaceKind Kind, int Y, int X, int Z);

/// <summary>What lies out along one compass direction.</summary>
/// <param name="Direction">N, NE, E, SE, S, SW, W or NW.</param>
/// <param name="Samples">Every second block out, nearest first.</param>
public sealed record RayView(string Direction, IReadOnlyList<SurfaceSample> Samples)
{
    /// <summary>The nearest sample of a kind, or null.</summary>
    public SurfaceSample? Nearest(SurfaceKind kind) { foreach (var s in Samples) if (s.Kind == kind) return s; return null; }

    /// <summary>The first place the ground drops 4 blocks or more from the one before (or ends in nothing), and by how much; null if it does not.</summary>
    public (int Distance, int Height)? FirstDrop()
    {
        for (var i = 1; i < Samples.Count; i++)
        {
            var (a, b) = (Samples[i - 1], Samples[i]);
            if (a.Kind == SurfaceKind.Land && b.Kind == SurfaceKind.Land && a.Y - b.Y >= 4) return (b.Distance, a.Y - b.Y);
            if (a.Kind == SurfaceKind.Land && b.Kind == SurfaceKind.Void) return (b.Distance, 40);
        }
        return null;
    }

    /// <summary>The farthest distance the loaded world is known out along this line.</summary>
    public int Known => Samples.LastOrDefault(s => s.Kind != SurfaceKind.Unknown).Distance;
}

/// <summary>
/// Looking around: the surface out to some distance in the eight compass directions, from what the client has loaded of the world (what a player standing here could take in of the terrain:
/// where there is land, water, lava, a cliff or a drop, and where the world he knows ends). Live on AubsCraft 2026-11-02 he swam out into a lake, said he could not see land, and never
/// looked: the shore was behind him, in the data he already had. "Being aware of surroundings is a large part of existing in a world" (TJ). Pure reading of the loaded chunks: no network,
/// a few thousand block lookups.
/// </summary>
public sealed class SurroundingsView
{
    public static readonly string[] Names = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
    private static readonly (double X, double Z)[] Vectors = [(0, -1), (0.7071, -0.7071), (1, 0), (0.7071, 0.7071), (0, 1), (-0.7071, 0.7071), (-1, 0), (-0.7071, -0.7071)];

    public bool InWater { get; init; }
    /// <summary>How many blocks of water are under him (0 when dry).</summary>
    public int WaterDepth { get; init; }
    /// <summary>The block under his feet when on land.</summary>
    public string Standing { get; init; } = "";
    public int Reach { get; init; }
    public IReadOnlyList<RayView> Rays { get; init; } = [];

    /// <summary>The nearest land in any direction (not counting the spot he is on), or null.</summary>
    public (string Direction, int Distance)? NearestLand => Nearest(SurfaceKind.Land);
    public (string Direction, int Distance)? NearestWater => Nearest(SurfaceKind.Water);
    public (string Direction, int Distance)? NearestLava => Nearest(SurfaceKind.Lava);

    /// <summary>The nearest sample of land, with where it is (to swim or walk toward), or null.</summary>
    public SurfaceSample? NearestLandSample()
    {
        SurfaceSample? best = null;
        foreach (var r in Rays)
            if (r.Nearest(SurfaceKind.Land) is { } s && (best == null || s.Distance < best.Value.Distance)) best = s;
        return best;
    }

    private (string, int)? Nearest(SurfaceKind kind)
    {
        (string, int)? best = null;
        foreach (var r in Rays)
            if (r.Nearest(kind) is { } s && (best == null || s.Distance < best.Value.Item2)) best = (r.Direction, s.Distance);
        return best;
    }

    /// <summary>The nearest drop of 4 blocks or more in any direction: (direction, distance, height).</summary>
    public (string Direction, int Distance, int Height)? NearestDrop()
    {
        (string, int, int)? best = null;
        foreach (var r in Rays)
            if (r.FirstDrop() is { } d && (best == null || d.Distance < best.Value.Item2)) best = (r.Direction, d.Distance, d.Height);
        return best;
    }

    /// <summary>The farthest the loaded world reaches in any direction (what he knows of it), in blocks.</summary>
    public int KnownReach => Rays.Count == 0 ? 0 : Rays.Max(r => r.Known);

    // ------------------------------------------------------------------ taking the look

    private static string Compass(int i) => Names[i];

    /// <summary>Looks out from <paramref name="at"/> to <paramref name="reach"/> blocks.</summary>
    public static SurroundingsView Look(GameWorld w, (double X, double Y, double Z) at, int reach)
    {
        int fx = (int)Math.Floor(at.X), fy = (int)Math.Floor(at.Y + 1e-6), fz = (int)Math.Floor(at.Z);
        var data = w.Data;
        var feet = w.GetState(fx, fy, fz);
        var under = w.GetState(fx, fy - 1, fz);
        var inWater = IsWater(data, feet) || (IsWater(data, under) && !Solid(data, feet));
        var depth = 0;
        if (inWater)
            for (var y = IsWater(data, feet) ? fy : fy - 1; IsWater(data, w.GetState(fx, y, fz)); y--) depth++;
        var standing = !inWater && under > 0 ? data.BlockOf(under).Name.Replace("minecraft:", "") : "";

        var rays = new List<RayView>(8);
        for (var i = 0; i < 8; i++)
        {
            var samples = new List<SurfaceSample>(reach / 2);
            for (var d = 2; d <= reach; d += 2)
            {
                int x = (int)Math.Floor(at.X + Vectors[i].X * d), z = (int)Math.Floor(at.Z + Vectors[i].Z * d);
                var (kind, y) = SurfaceAt(w, x, z, fy);
                samples.Add(new SurfaceSample(d, kind, y, x, z));
                if (kind == SurfaceKind.Unknown) break; // nothing is known beyond the edge of what is loaded
            }
            rays.Add(new RayView(Compass(i), samples));
        }
        return new SurroundingsView { InWater = inWater, WaterDepth = depth, Standing = standing, Reach = reach, Rays = rays };
    }

    private static bool IsWater(GameData d, int s) => s > 0 && (d.StateFlags[s] & GameData.FlagWater) != 0;
    private static bool Solid(GameData d, int s) => s > 0 && d.HasCollision(s) && !d.IsAir(s);

    /// <summary>The top of the ground in this column near his height: land (not counting the canopy and trunks of trees: the ground under them), water, lava, nothing, or not loaded.</summary>
    internal static (SurfaceKind Kind, int Y) SurfaceAt(GameWorld w, int x, int z, int aroundY)
    {
        var data = w.Data;
        if (!w.IsLoaded(x, z)) return (SurfaceKind.Unknown, 0);
        for (var y = aroundY + 12; y >= aroundY - 40; y--)
        {
            var s = w.GetState(x, y, z);
            if (s < 0) return (SurfaceKind.Unknown, 0);
            if (s == 0 || data.IsAir(s)) continue;
            var flags = data.StateFlags[s];
            if ((flags & GameData.FlagWater) != 0) return (SurfaceKind.Water, y);
            if ((flags & GameData.FlagLava) != 0) return (SurfaceKind.Lava, y);
            if (!data.HasCollision(s)) continue;           // grass, flowers, snow layers, torches
            var name = data.BlockOf(s).Name;
            if (name.EndsWith("_leaves") || name.EndsWith("_log") || name.EndsWith("_wood")) continue; // a tree is not the ground
            return (SurfaceKind.Land, y);
        }
        return (SurfaceKind.Void, 0);
    }

    // ------------------------------------------------------------------ saying it

    /// <summary>
    /// The compact line for the status block: said only when it matters (in the water; lava or a drop close by), else null so the ordinary prompt costs nothing. He was told nothing about
    /// where land was while he swam, which is why he never thought to look.
    /// </summary>
    public string? Notable()
    {
        var parts = new List<string>();
        if (InWater)
        {
            parts.Add($"You are in the water ({WaterDepth} deep).");
            parts.Add(NearestLand is { } l ? $"The nearest land you can see is {l.Direction}, about {l.Distance} blocks away." : $"You can't see any land within {Reach} blocks (the world you know goes out about {KnownReach}). Use look_around.");
        }
        if (NearestLava is { } lava && lava.Distance <= 10) parts.Add($"Lava {lava.Direction}, {lava.Distance} blocks away.");
        if (NearestDrop() is { } drop && drop.Distance <= 6) parts.Add($"A drop of {(drop.Height >= 40 ? "more than 20" : drop.Height.ToString())} blocks, {drop.Direction}, {drop.Distance} blocks away.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>The whole look, for the look_around tool: where he is, then each direction, then the nearest of each thing.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(InWater ? $"I am in the water ({WaterDepth} deep)." : $"I am on land{(Standing.Length > 0 ? $", standing on {Standing.Replace('_', ' ')}" : "")}.");
        sb.Append($" I looked out {Reach} blocks (the world I know goes out about {KnownReach}). Distances are blocks.\n");
        foreach (var r in Rays) sb.Append(r.Direction).Append(": ").Append(Runs(r)).Append('\n');
        sb.Append("Nearest: land ").Append(NearestLand is { } l ? $"{l.Direction} {l.Distance}" : "none seen");
        sb.Append("; water ").Append(NearestWater is { } w ? $"{w.Direction} {w.Distance}" : "none seen");
        sb.Append("; lava ").Append(NearestLava is { } v ? $"{v.Direction} {v.Distance}" : "none seen");
        sb.Append("; drop ").Append(NearestDrop() is { } d ? $"{d.Direction} {d.Distance} ({(d.Height >= 40 ? "very deep" : d.Height + " blocks")})" : "none seen").Append('.');
        return sb.ToString();
    }

    /// <summary>One direction as a few runs: "land 2-14, water 16-44 (a 6 block drop), land 46-96".</summary>
    private static string Runs(RayView r)
    {
        if (r.Samples.Count == 0) return "unknown";
        var segments = new List<(SurfaceKind Kind, int From, int To, string Note)>();
        for (var i = 0; i < r.Samples.Count; i++)
        {
            var s = r.Samples[i];
            var step = i == 0 ? 0 : r.Samples[i - 1].Y - s.Y;           // positive: the ground fell
            var landStep = i > 0 && s.Kind == SurfaceKind.Land && r.Samples[i - 1].Kind == SurfaceKind.Land && Math.Abs(step) >= 4;
            if (segments.Count == 0 || segments[^1].Kind != s.Kind || landStep)
                segments.Add((s.Kind, s.Distance, s.Distance, landStep ? (step > 0 ? $" (after a {step} block drop)" : $" (after a {-step} block rise)") : ""));
            else segments[^1] = segments[^1] with { To = s.Distance };
        }
        static string Name(SurfaceKind k) => k switch { SurfaceKind.Land => "land", SurfaceKind.Water => "water", SurfaceKind.Lava => "LAVA", SurfaceKind.Void => "nothing below", _ => "not loaded" };
        return string.Join(", ", segments.Select(g => g.From == g.To ? $"{Name(g.Kind)} at {g.From}{g.Note}" : $"{Name(g.Kind)} {g.From}-{g.To}{g.Note}"));
    }}
