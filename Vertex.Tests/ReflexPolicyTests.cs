using ILGPU;
using ILGPU.Runtime.CPU;
using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>
/// The reflex policy's machinery, on ILGPU's CPU accelerator (no GPU): the network trains, its weights round-trip
/// into LearnedReflexPolicy, bravery moves the bar, and the hand-written rule stays what it was.
/// </summary>
public class ReflexPolicyTests
{
    /// <summary>A situation where fighting is plainly right (lots of health, a sword) or plainly wrong.</summary>
    private static float[] Situation(float health, float weapon) => [health, 0, weapon, 0.2f, 0.25f, 0, 0, 0, 1, 1];

    [Test]
    public async Task LearnedPolicy_TrainsOnTheCpu_AndLoadsItsWeights()
    {
        using var context = Context.Create(b => b.CPU());
        using var cpu = context.CreateCPUAccelerator(0);
        const int batch = 16;
        var rng = new Random(3);
        byte[] weights;
        using (var model = LearnedReflexPolicy.CreateModel(cpu, batch))
        {
            // Fight when healthy and armed, flee when weak and empty-handed.
            for (var step = 0; step < 600; step++)
            {
                var x = new float[batch * Brain.ReflexFeatures.Count];
                var y = new int[batch];
                for (var i = 0; i < batch; i++)
                {
                    var strong = rng.Next(2) == 0;
                    var f = strong ? Situation(0.7f + 0.3f * rng.NextSingle(), 0.6f + 0.4f * rng.NextSingle()) : Situation(0.1f + 0.2f * rng.NextSingle(), 0.2f * rng.NextSingle());
                    f.CopyTo(x, i * f.Length);
                    y[i] = (int)(strong ? ReflexAction.Fight : ReflexAction.Flee);
                }
                model.TrainStep(x, y, batch, 0.05f);
            }
            await model.FlushAsync();
            weights = await model.ExportWeightsAsync();
        }

        using var policy = new LearnedReflexPolicy(cpu, weights);
        var strongChance = await policy.FightChanceAsync(Situation(0.9f, 0.8f));
        var weakChance = await policy.FightChanceAsync(Situation(0.15f, 0));
        TestContext.Out.WriteLine($"fight chance: strong {strongChance:0.000}, weak {weakChance:0.000}");
        Assert.That(strongChance, Is.GreaterThan(0.9f));
        Assert.That(weakChance, Is.LessThan(0.1f));
        Assert.That(await policy.DecideAsync(Situation(0.9f, 0.8f), 0.2), Is.EqualTo(ReflexAction.Fight), "even a careful character fights a sure win");
        Assert.That(await policy.DecideAsync(Situation(0.15f, 0), 0.9), Is.EqualTo(ReflexAction.Flee), "even a brave one runs from a sure loss");
    }

    /// <summary>The weights that ship (ReflexTraining wrote them only after they beat the rule) load and decide.</summary>
    [Test]
    public async Task ShippedPolicy_Loads_AndDecides()
    {
        using var context = Context.Create(b => b.CPU());
        using var cpu = context.CreateCPUAccelerator(0);
        using var policy = LearnedReflexPolicy.LoadShipped(cpu);
        if (policy == null) Assert.Ignore("no trained policy ships (it did not beat the rule): Vertex uses the rule");
        var chance = await policy!.FightChanceAsync(Situation(0.9f, 0.8f));
        TestContext.Out.WriteLine($"healthy and armed against one zombie: fight chance {chance:0.000}");
        Assert.That(chance, Is.InRange(0f, 1f));
        Assert.That(await policy.DecideAsync(Situation(0.9f, 0.8f), 0.5), Is.EqualTo(ReflexAction.Fight), "healthy and armed against one zombie: fight");
    }

    [Test]
    public async Task RulePolicy_IsTheHandWrittenRule()
    {
        var rule = RuleReflexPolicy.Instance;
        Assert.That(await rule.DecideAsync(Situation(0.5f, 0), 0.6), Is.EqualTo(ReflexAction.Fight), "brave, health 10");
        Assert.That(await rule.DecideAsync(Situation(0.4f, 1), 0.6), Is.EqualTo(ReflexAction.Flee), "brave, but health 8");
        Assert.That(await rule.DecideAsync(Situation(1f, 1), 0.4), Is.EqualTo(ReflexAction.Flee), "careful");
    }
}
