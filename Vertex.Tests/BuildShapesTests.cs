using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Data;
using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>The named shapes (house, platform, pyramid, castle): the words people use, the sizes they can be, and that every script they generate is one the builder accepts.</summary>
public class BuildShapesTests
{
    private static readonly GameData Data = GameData.Vanilla;

    [TestCase("house", "house")]
    [TestCase("Cabin", "house")]
    [TestCase("hut", "house")]
    [TestCase("small house", "house")]
    [TestCase("deck", "platform")]
    [TestCase("Floor", "platform")]
    [TestCase("pyramid", "pyramid")]
    [TestCase("castle", "castle")]
    [TestCase("fortress", "castle")]
    [TestCase("pen", "pen")]
    [TestCase("corral", "pen")]
    [TestCase("animal pen", "pen")]
    public void PeopleSayItManyWays(string said, string shape) => Assert.That(BuildShapes.Normalize(said), Is.EqualTo(shape));

    [Test]
    public void APenIsAFenceRingWithOneGate_OnEverySide()
    {
        foreach (var side in new[] { BlockFace.North, BlockFace.South, BlockFace.East, BlockFace.West })
        {
            var script = BuildScript.Parse(BuildShapes.Script("pen", 7, 7, 1, "fence", "fence", side, false, false, false), Data);
            Assert.That(script.Needs["fence"], Is.EqualTo(23), $"a 7 x 7 ring is 24 blocks, one of them the gate ({side})");
            Assert.That(script.Needs["gate"], Is.EqualTo(1));
            Assert.That(script.Cells.Select(c => c.Y).Distinct(), Is.EqualTo(new[] { 0 }), "one layer");
            var gate = script.Cells.Single(c => c.Item == "gate");
            Assert.That(gate.Facing, Is.EqualTo(side), "the gate faces across the side it is in");
        }
        Assert.That(() => BuildScript.Parse("gate 0 0 0", Data), Throws.TypeOf<BuildScriptException>().With.Message.Contains("needs a side"));
    }

    [Test]
    public void ThingsItCannotBuildAreNotShapes()
    {
        Assert.That(BuildShapes.Normalize("spaceship"), Is.Null);
        Assert.That(BuildShapes.Normalize(null), Is.Null);
        Assert.That(BuildShapes.Normalize(""), Is.Null);
    }

    [Test]
    public void SizesAreAskedForOrDefaultedAndAlwaysWithinWhatCanBeBuilt()
    {
        Assert.That(BuildShapes.Fit("house", 0, 0, 0), Is.EqualTo((5, 5, 3)));
        Assert.That(BuildShapes.Fit("house", 7, 0, 0), Is.EqualTo((7, 7, 3)), "only a width: square");
        Assert.That(BuildShapes.Fit("house", 6, 9, 2), Is.EqualTo((6, 9, 2)));
        Assert.That(BuildShapes.Fit("house", 40, 40, 40), Is.EqualTo((11, 11, 4)), "never bigger than 11 wide or 4 high walls");
        Assert.That(BuildShapes.Fit("house", 1, 1, 1), Is.EqualTo((3, 3, 2)), "never smaller than something to stand in");
        Assert.That(BuildShapes.Fit("platform", 0, 0, 0), Is.EqualTo((7, 7, 1)));
        Assert.That(BuildShapes.Fit("platform", 30, 30, 5), Is.EqualTo((21, 21, 1)));
        Assert.That(BuildShapes.Fit("pyramid", 9, 3, 0), Is.EqualTo((9, 9, 1)), "a pyramid is square");
        Assert.That(BuildShapes.Fit("pyramid", 50, 0, 0), Is.EqualTo((11, 11, 1)), "11 wide is 6 layers: as high as can be reached from the ground");
        Assert.That(BuildShapes.Fit("castle", 0, 0, 0), Is.EqualTo((13, 13, 3)));
        Assert.That(BuildShapes.Fit("castle", 5, 0, 0), Is.EqualTo((9, 9, 3)), "a castle needs room for its towers");
    }

    [Test]
    public void EveryShapeAtEverySizeAndSideIsAScriptTheBuilderAccepts()
    {
        foreach (var shape in BuildShapes.Names)
        foreach (var size in new[] { 0, 3, 5, 6, 9, 11, 13, 17, 40 })
        foreach (var side in new[] { BlockFace.North, BlockFace.South, BlockFace.East, BlockFace.West })
        foreach (var extras in new[] { true, false })
        {
            var (w, d, h) = BuildShapes.Fit(shape, size, 0, 0);
            var text = BuildShapes.Script(shape, w, d, h, "minecraft:cobblestone", "planks", side, extras, extras, extras);
            BuildScript script;
            try { script = BuildScript.Parse(text, Data); }
            catch (BuildScriptException e) { throw new AssertionException($"{shape} {w}x{d}x{h} door {side}: {e.Message}\n{text}"); }
            Assert.That(script.Width, Is.LessThanOrEqualTo(w), $"{shape} {w}x{d}x{h}");
            Assert.That(script.Depth, Is.LessThanOrEqualTo(d));
            Assert.That(script.Height, Is.LessThanOrEqualTo(6));
        }
    }

    [Test]
    public void AHouseHasAWayInOnTheRightSide_WindowsOnTheOthers_AndAPitchedRoofOnlyWhereItCanBeReached()
    {
        // 5 x 5, walls 2 high: a gable's highest layer is at y = 2 + 3 - 1 = 4, within reach.
        var low = BuildScript.Parse(BuildShapes.Script("house", 5, 5, 2, "minecraft:cobblestone", "planks", BlockFace.South, true, true, true), Data);
        Assert.That(low.Cells.Max(c => c.Y), Is.EqualTo(4));
        Assert.That(low.Cells.Where(c => c.Item == "planks").Select(c => c.Y).Distinct().OrderBy(y => y), Is.EqualTo(new[] { 2, 3, 4 }), "three stepped roof layers");
        Assert.That(low.Cells.Single(c => c.Item == "door"), Is.EqualTo(new BuildCell(2, 0, 4, "door", BlockFace.South, true)), "the door in the middle of the south wall");
        Assert.That(low.Cells.Where(c => c.Item == "minecraft:glass").Select(c => (c.X, c.Z)), Is.EquivalentTo(new[] { (2, 0), (0, 2), (4, 2) }), "a window in each OTHER wall");
        Assert.That(low.Cells.Any(c => c is { Item: "minecraft:cobblestone", X: 2, Z: 4, Y: < 2 }), Is.False, "the doorway is open");

        // Walls 3 high would put the ridge at y = 5: flat roof instead.
        var tall = BuildScript.Parse(BuildShapes.Script("house", 5, 5, 3, "minecraft:cobblestone", "planks", BlockFace.North, false, false, false), Data);
        Assert.That(tall.Cells.Where(c => c.Item == "planks").Select(c => c.Y).Distinct(), Is.EqualTo(new[] { 3 }), "one flat layer");
        Assert.That(tall.Cells.Any(c => c.Item is "door" or "minecraft:glass" or "minecraft:torch"), Is.False, "no door, glass or torch when we have none");
    }

    [Test]
    public void ACastleHasAGateFourTowersAndBattlements()
    {
        var castle = BuildScript.Parse(BuildShapes.Script("castle", 13, 13, 3, "minecraft:stone_bricks", "planks", BlockFace.South, false, false, false), Data);
        Assert.That(castle.Cells.Count, Is.InRange(200, 300));
        Assert.That(castle.Needs.Keys, Is.EquivalentTo(new[] { "minecraft:stone_bricks" }), "a castle is built of one material");
        Assert.That(castle.Height, Is.EqualTo(5), "towers a block above the wall, with battlements");
        // The gate: three wide and two high in the middle of the south wall.
        for (var x = 5; x <= 7; x++)
            Assert.That(castle.Cells.Any(c => c.X == x && c.Z == 12 && c.Y < 2), Is.False, $"gate at x={x}");
        Assert.That(castle.Cells.Any(c => c.X == 4 && c.Z == 12 && c.Y == 0), Is.True, "the wall stands beside the gate");
        // Four towers: the corner columns reach the tower height.
        foreach (var (x, z) in new[] { (0, 0), (12, 0), (0, 12), (12, 12) })
            Assert.That(castle.Cells.Any(c => c.X == x && c.Z == z && c.Y == 3), Is.True, $"tower at {x},{z}");
        // Battlements: every other block along the wall top, gaps between.
        var topOfNorthWall = castle.Cells.Where(c => c.Z == 0 && c.Y == 3 && c.X is > 2 and < 10).Select(c => c.X).OrderBy(x => x).ToList();
        Assert.That(topOfNorthWall, Is.EqualTo(new[] { 4, 6, 8 }), "merlons on even blocks, gaps between");
    }

    [Test]
    public void PlatformsAndPyramidsAreSolid()
    {
        var platform = BuildScript.Parse(BuildShapes.Script("platform", 7, 5, 1, "planks", "planks", BlockFace.North, false, false, false), Data);
        Assert.That(platform.Cells, Has.Count.EqualTo(35));
        Assert.That(platform.Cells.All(c => c.Y == 0), Is.True);
        var pyramid = BuildScript.Parse(BuildShapes.Script("pyramid", 7, 7, 1, "minecraft:sandstone", "planks", BlockFace.North, false, false, false), Data);
        Assert.That(pyramid.Cells, Has.Count.EqualTo(49 + 25 + 9 + 1));
        Assert.That(pyramid.Height, Is.EqualTo(4));
    }
}
