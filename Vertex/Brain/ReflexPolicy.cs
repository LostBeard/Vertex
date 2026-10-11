using ILGPU.Runtime;
using SpawnDev.ILGPU.ML.Training;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Entities;

namespace Vertex.Brain;

/// <summary>What to do when a mob hurts us.</summary>
public enum ReflexAction { Fight = 0, Flee = 1 }

/// <summary>
/// The moment a reflex decides on: our state and the mob that hurt us, as numbers a small network reads. Each is
/// scaled to about 0..1.
/// </summary>
public static class ReflexFeatures
{
    public const int Count = 10;

    private static readonly string[] Ranged = ["skeleton", "stray", "bogged", "pillager", "witch", "blaze", "breeze", "guardian"];
    private static readonly string[] Spiders = ["spider", "cave_spider"];
    private static readonly string[] Tough = ["vindicator", "ravager", "evoker", "piglin_brute", "hoglin", "zoglin", "wither_skeleton", "warden"];
    private static readonly string[] Weapons = ["wooden_sword", "golden_sword", "stone_sword", "iron_sword", "diamond_sword", "netherite_sword"];

    /// <summary>
    /// health, armor, best weapon (sword tier), distance to the attacker, hostiles within 8, and what kind of mob it
    /// is (ranged, spider, tough, other melee).
    /// </summary>
    public static float[] From(MinecraftClient c, Entity attacker)
    {
        var (x, y, z) = c.Position;
        var kind = attacker.Name.Bare();
        var weapon = 0;
        for (var i = Weapons.Length - 1; i >= 0; i--)
            if (c.Inventory.Count(Weapons[i]) > 0) { weapon = i + 1; break; }
        var ranged = Ranged.Contains(kind);
        var spider = Spiders.Contains(kind);
        var tough = Tough.Contains(kind);
        return
        [
            c.Health / 20f,
            (float)((c.Attribute("armor") ?? 0) / 20),
            weapon / (float)Weapons.Length,
            (float)Math.Min(1, attacker.DistanceTo(x, y, z) / 16),
            Math.Min(1, c.Entities.Values.Count(e => Skills.Hostile.Contains(e.Name) && e.DistanceTo(x, y, z) < 8) / 4f),
            ranged ? 1 : 0, spider ? 1 : 0, tough ? 1 : 0, ranged || spider || tough ? 0 : 1,
            1, // bias-like constant: the network sees a fixed input it can lean on
        ];
    }
}

/// <summary>Decides fight or flight from the features (bravery: the character's play style, 0..1).</summary>
public interface IReflexPolicy
{
    Task<ReflexAction> DecideAsync(float[] features, double bravery);
}

/// <summary>The hand-written rule: brave characters with health to spare fight, everyone else backs off.</summary>
public sealed class RuleReflexPolicy : IReflexPolicy
{
    public static RuleReflexPolicy Instance { get; } = new();
    public Task<ReflexAction> DecideAsync(float[] f, double bravery) =>
        Task.FromResult(bravery >= 0.5 && f[0] * 20 > 8 ? ReflexAction.Fight : ReflexAction.Flee);
}

/// <summary>
/// A small network (SpawnDev.ILGPU.ML TrainableModel, 10-32-32-2) trained on real fights on a local Paper server:
/// for each situation Vertex fought once and fled once, and learned which came out better (ReflexTraining). It
/// gives the chance that fighting is the better choice; bravery moves the bar (a brave character fights at 35%,
/// a careful one wants 65%).
/// </summary>
public sealed class LearnedReflexPolicy : IReflexPolicy, IDisposable
{
    private readonly TrainableModel _model;
    private readonly SemaphoreSlim _one = new(1, 1);

    public const string Resource = "Vertex.Brain.reflex-policy.bin";

    public static TrainableModel CreateModel(Accelerator accelerator, int maxBatch)
    {
        var m = new TrainableModel(accelerator)
            .AddLinear(ReflexFeatures.Count, 32).AddReLU()
            .AddLinear(32, 32).AddReLU()
            .AddLinear(32, 2);
        m.Build(maxBatch);
        return m;
    }

    public LearnedReflexPolicy(Accelerator accelerator, byte[] weights)
    {
        _model = CreateModel(accelerator, 1);
        _model.ImportWeights(weights);
    }

    /// <summary>The policy shipped with Vertex, or null if none was trained (or it did not beat the rule).</summary>
    public static LearnedReflexPolicy? LoadShipped(Accelerator accelerator)
    {
        using var s = typeof(LearnedReflexPolicy).Assembly.GetManifestResourceStream(Resource);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new LearnedReflexPolicy(accelerator, ms.ToArray());
    }

    /// <summary>The chance that fighting comes out better than fleeing.</summary>
    public async Task<float> FightChanceAsync(float[] features)
    {
        await _one.WaitAsync();
        try { return (await _model.PredictProbsAsync(features, 1))[(int)ReflexAction.Fight]; }
        finally { _one.Release(); }
    }

    public async Task<ReflexAction> DecideAsync(float[] features, double bravery) =>
        await FightChanceAsync(features) >= 0.65 - 0.3 * Math.Clamp(bravery, 0, 1) ? ReflexAction.Fight : ReflexAction.Flee;

    public void Dispose()
    {
        _model.Dispose();
        _one.Dispose();
    }
}
