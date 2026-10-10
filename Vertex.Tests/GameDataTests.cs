using SpawnDev.Minecraft.Data;

namespace Vertex.Tests;

/// <summary>The generated tables: spot values checked against the game's known behaviour.</summary>
public class GameDataTests
{
    private static GameData D => GameData.Vanilla;

    [Test]
    public void Blocks_HaveVanillaHardnessShapesAndFriction()
    {
        var stone = D.Block("stone")!;
        Assert.That(stone.MinState, Is.EqualTo(1));
        Assert.That(D.Hardness[stone.DefaultState], Is.EqualTo(1.5f));
        Assert.That(D.StateFlags[stone.DefaultState] & GameData.FlagNeedsTool, Is.Not.Zero, "stone drops nothing without a pickaxe");
        Assert.That(D.CollisionBoxes(stone.DefaultState), Is.EqualTo(new[] { new Box(0, 0, 0, 1, 1, 1) }));

        Assert.That(D.IsAir(D.Block("air")!.DefaultState));
        Assert.That(D.HasCollision(D.Block("short_grass")!.DefaultState), Is.False);
        Assert.That(D.Friction[D.Block("ice")!.DefaultState], Is.EqualTo(0.98f));
        Assert.That(D.SpeedFactor[D.Block("soul_sand")!.DefaultState], Is.EqualTo(0.4f));
        Assert.That(D.CollisionBoxes(D.Block("soul_sand")!.DefaultState)[0].MaxY, Is.EqualTo(0.875));
        Assert.That(D.Hardness[D.Block("bedrock")!.DefaultState], Is.EqualTo(-1f), "unbreakable");
        var water = D.Block("water")!.DefaultState;
        Assert.That(D.StateFlags[water] & (GameData.FlagWater | GameData.FlagFluidSource), Is.EqualTo(GameData.FlagWater | GameData.FlagFluidSource));
        Assert.That(D.FluidAmount[water], Is.EqualTo(8));
        // A fence is 1.5 tall: the classic "can't jump over" rule.
        Assert.That(D.CollisionBoxes(D.Block("oak_fence")!.DefaultState).Max(b => b.MaxY), Is.EqualTo(1.5));
    }

    [Test]
    public void StateProperties_RoundTrip()
    {
        var stairs = D.Block("oak_stairs")!;
        var s = D.WithProperty(stairs.DefaultState, "facing", "east");
        s = D.WithProperty(s, "half", "top");
        Assert.That(D.GetProperty(s, "facing"), Is.EqualTo("east"));
        Assert.That(D.GetProperty(s, "half"), Is.EqualTo("top"));
        Assert.That(D.GetProperty(s, "waterlogged"), Is.EqualTo(D.GetProperty(stairs.DefaultState, "waterlogged")));
        Assert.That(D.BlockOf(s), Is.SameAs(stairs));
        // Every state of every block maps back to its block.
        foreach (var b in D.Blocks)
            for (var st = b.MinState; st <= b.MaxState; st++)
                Assert.That(D.StateBlock[st], Is.EqualTo(b.Id));
        Assert.That(D.StateCount, Is.EqualTo(D.Blocks[^1].MaxState + 1));
    }

    [Test]
    public void Items_HaveToolRulesFoodAndPlacedBlocks()
    {
        var pick = D.Item("diamond_pickaxe")!;
        Assert.That(pick.MaxStackSize, Is.EqualTo(1));
        Assert.That(pick.MaxDamage, Is.EqualTo(1561));
        Assert.That(pick.ToolRules.Any(r => r.BlockTag == "minecraft:mineable/pickaxe" && r.Speed == 8f && r.CorrectForDrops == true));
        var bread = D.Item("bread")!;
        Assert.That((bread.FoodNutrition, bread.FoodSaturation), Is.EqualTo((5, 6.0f)));
        Assert.That(D.Item("oak_planks")!.Block, Is.EqualTo("minecraft:oak_planks"));
        Assert.That(D.Item("redstone")!.Block, Is.EqualTo("minecraft:redstone_wire"));
        Assert.That(D.Item("diamond")!.Block, Is.Null);
    }

    [Test]
    public void TagsAndRecipes_ComeFromTheJar()
    {
        Assert.That(D.BlockTag("mineable/pickaxe"), Does.Contain("minecraft:stone").And.Contain("minecraft:iron_ore"));
        Assert.That(D.ItemTag("#minecraft:logs"), Does.Contain("minecraft:oak_log").And.Contain("minecraft:stripped_birch_wood"));
        Assert.That(D.Recipes["minecraft:crafting_table"]?["type"]?.GetValue<string>(), Is.EqualTo("minecraft:crafting_shaped"));
        Assert.That(D.Entity("player")!.Height, Is.EqualTo(1.8f));
        Assert.That(D.Entity("player")!.EyeHeight, Is.EqualTo(1.62f));
        Assert.That(D.RegistryName("minecraft:attribute", 21), Is.EqualTo("minecraft:movement_speed"));
        Assert.That(D.RegistryName("minecraft:menu", 2), Is.EqualTo("minecraft:generic_9x3"));
    }
}
