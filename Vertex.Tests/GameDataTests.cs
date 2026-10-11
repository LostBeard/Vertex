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

    /// <summary>The block registry a Fabric server would sync: vanilla in order, then these modded blocks.</summary>
    private static Dictionary<string, int> Registry(params string[] modded)
    {
        var map = D.Blocks.Take(D.Blocks.Length - 1).ToDictionary(b => b.Name, b => b.Id);
        foreach (var m in modded) map[m] = map.Count;
        return map;
    }

    [Test]
    public void ModdedBlocks_FollowVanilla_NumberedLikeTheServer_UnknownAfterAGap()
    {
        Assert.That(GameData.ModdedCatalog, Has.Count.GreaterThanOrEqualTo(345), "tools/ModDump output is embedded");
        var door = GameData.ModdedCatalog["spookydoors:spooky_oak_door"];
        var pumpkin = GameData.ModdedCatalog["mcwholidays:small_pumpkin"];
        var vanillaBlocks = D.Blocks.Length - 1;

        var m = D.WithModdedBlocks(Registry("spookydoors:spooky_oak_door", "mcwholidays:small_pumpkin"));
        Assert.That((m.ModdedBlockCount, m.KnownStateCount), Is.EqualTo((2, D.VanillaStateCount + door.StateCount + pumpkin.StateCount)));
        var d = m.Block("spookydoors:spooky_oak_door")!;
        var p = m.Block("mcwholidays:small_pumpkin")!;
        Assert.That((d.Id, d.MinState, p.Id, p.MinState), Is.EqualTo((vanillaBlocks, D.VanillaStateCount, vanillaBlocks + 1, D.VanillaStateCount + door.StateCount)),
            "ids are the server's raw ids; states follow on in registry order");
        // Properties sorted by name, last fastest, exactly like vanilla doors (same StateDefinition code).
        var oak = D.Block("oak_door")!;
        Assert.That(d.Properties.Select(x => x.Name), Is.EqualTo(oak.Properties.Select(x => x.Name)));
        var closedEastLower = m.WithProperty(m.WithProperty(m.WithProperty(d.MinState, "facing", "east"), "half", "lower"), "open", "false");
        Assert.That(closedEastLower - d.MinState, Is.EqualTo(D.WithProperty(D.WithProperty(D.WithProperty(oak.MinState, "facing", "east"), "half", "lower"), "open", "false") - oak.MinState));
        Assert.That(m.CollisionBoxes(closedEastLower), Is.EqualTo(D.CollisionBoxes(D.WithProperty(D.WithProperty(D.WithProperty(oak.MinState, "facing", "east"), "half", "lower"), "open", "false"))));
        Assert.That(m.BlockOf(m.UnknownState).Name, Is.EqualTo("vertex:unknown_modded_block"));
        Assert.That(m.Known(m.UnknownState + 5), Is.EqualTo(m.UnknownState));
        Assert.That(m.Known(d.MaxState), Is.EqualTo(d.MaxState));
        Assert.That(m.CollisionBoxes(m.UnknownState), Is.EqualTo(new[] { new Box(0, 0, 0, 1, 1, 1) }));

        // A block we have no data for: it and everything after it are unknown (its state count decides where the
        // next block starts), but what comes before stays known.
        var gap = D.WithModdedBlocks(Registry("spookydoors:spooky_oak_door", "somemod:mystery", "mcwholidays:small_pumpkin"));
        Assert.That((gap.ModdedBlockCount, gap.Block("mcwholidays:small_pumpkin")), Is.EqualTo((1, (BlockType?)null)));
        Assert.That(gap.Known(gap.Block("spookydoors:spooky_oak_door")!.MaxState + 1), Is.EqualTo(gap.UnknownState));

        // Vanilla ids moved: nothing can be trusted, keep the vanilla table (modded states become unknown).
        var moved = Registry("spookydoors:spooky_oak_door");
        (moved["minecraft:stone"], moved["minecraft:granite"]) = (moved["minecraft:granite"], moved["minecraft:stone"]);
        Assert.That(D.WithModdedBlocks(moved), Is.SameAs(D));
        Assert.That(D.WithModdedBlocks(Registry()), Is.SameAs(D), "a Fabric server with no modded blocks");

        // The server's tags, by raw id, put modded blocks in vanilla tags.
        var tagged = m.WithBlockTags(new Dictionary<string, int[]> { ["minecraft:wooden_doors"] = [oak.Id, d.Id, 999_999] });
        Assert.That(tagged.BlockTag("wooden_doors"), Is.EquivalentTo(new[] { "minecraft:oak_door", "spookydoors:spooky_oak_door" }));
        Assert.That(m.BlockTag("wooden_doors"), Does.Not.Contain("spookydoors:spooky_oak_door"), "the table it came from is unchanged");
    }

    [Test]
    public void LearnedShapes_ApplyToTheirStates_OthersStayUnknown()
    {
        var mat = new[] { new Box(0, 0, 0, 1, 0.0625, 1) };
        var l = D.WithLearnedStates(new Dictionary<int, Box[]> { [D.KnownStateCount + 5] = mat, [D.KnownStateCount + 9] = [], [3] = [] },
            new HashSet<int> { D.KnownStateCount + 9 });
        Assert.That(l.LearnedStateCount, Is.EqualTo(2), "a vanilla state is never relearned");
        Assert.That(l.CollisionBoxes(D.KnownStateCount + 5), Is.EqualTo(mat));
        Assert.That(l.HasCollision(D.KnownStateCount + 9), Is.False);
        Assert.That(l.StateFlags[D.KnownStateCount + 9] & GameData.FlagSticky, Is.Not.Zero);
        Assert.That(l.IsUnknownBlock(D.KnownStateCount + 5), "learned by touch: still nameless, never dug");
        // Neither known nor learned: the solid unknown cube, gaps included.
        foreach (var s in new[] { D.KnownStateCount + 6, D.KnownStateCount + 40 })
        {
            var k = l.Known(s);
            Assert.That(l.CollisionBoxes(k), Is.EqualTo(new[] { new Box(0, 0, 0, 1, 1, 1) }), $"state {s}");
            Assert.That(l.IsUnknownBlock(k));
        }
        Assert.That(l.Known(D.KnownStateCount + 5), Is.EqualTo(D.KnownStateCount + 5));
        Assert.That(l.BlockOf(l.UnknownState).Name, Is.EqualTo("vertex:unknown_modded_block"));
        Assert.That(D.WithLearnedStates(new Dictionary<int, Box[]>()), Is.SameAs(D));

        // On a modded server tables cover every state id from the start, so learning never changes their length:
        // an id read through a newer table is always safe to look up in an older one some code still holds.
        var before = D.ForModdedServer();
        var after = before.WithLearnedStates(new Dictionary<int, Box[]> { [60_000] = mat });
        Assert.That((before.StateCount, after.StateCount), Is.EqualTo((GameData.ModdedTableSize, GameData.ModdedTableSize)));
        Assert.That(before.CollisionBoxes(after.Known(60_000)), Is.EqualTo(new[] { new Box(0, 0, 0, 1, 1, 1) }), "the older table: still unknown, no exception");
        Assert.That(after.CollisionBoxes(after.Known(60_000)), Is.EqualTo(mat));
    }
}
