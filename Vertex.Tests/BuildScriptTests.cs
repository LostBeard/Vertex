using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Data;
using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>
/// The build script a language model (or a shape) writes: its shapes by cell count, the material names people say, every way it can be wrong (with a message the model can correct itself from),
/// and the order blocks are placed in (never a block with nothing to click against, the doorway last).
/// </summary>
public class BuildScriptTests
{
    private static readonly GameData Data = GameData.Vanilla;

    private static BuildScript Parse(string script) => BuildScript.Parse(script, Data);

    [Test]
    public void BoxesAreBuiltAsTheirShapes()
    {
        Assert.That(Parse("fill 0 0 0 2 1 2 cobblestone").Cells, Has.Count.EqualTo(3 * 2 * 3));
        Assert.That(Parse("hollow 0 0 0 4 3 4 cobblestone").Cells, Has.Count.EqualTo(5 * 4 * 5 - 3 * 2 * 3), "a 5x4x5 box minus its 3x2x3 inside");
        Assert.That(Parse("walls 0 0 0 4 2 4 cobblestone").Cells, Has.Count.EqualTo(16 * 3), "a ring of 16 cells, three high, no floor or roof");
        Assert.That(Parse("floor 0 0 5 3 0 planks").Cells, Has.Count.EqualTo(6 * 4));
        Assert.That(Parse("line 0 0 0 5 0 0 cobblestone").Cells, Has.Count.EqualTo(6));
        var s = Parse("fill 0 0 0 4 2 6 dirt");
        Assert.That((s.Width, s.Depth, s.Height), Is.EqualTo((5, 7, 3)), "the footprint and height of what is built");
    }

    [Test]
    public void RoofsAreSteppedFullBlocks()
    {
        Assert.That(Parse("roof flat 0 0 4 4 3 planks").Cells, Has.Count.EqualTo(25));
        var pyramid = Parse("roof pyramid 0 0 4 4 0 planks");
        Assert.That(pyramid.Cells, Has.Count.EqualTo(25 + 9 + 1));
        Assert.That(pyramid.Height, Is.EqualTo(3));
        // Gable over a 7 (x) by 5 (z) rectangle: the ridge runs along x; 5, 3 and 1 rows of 7.
        var gable = Parse("roof gable 0 0 6 4 3 planks");
        Assert.That(gable.Cells, Has.Count.EqualTo(35 + 21 + 7));
        Assert.That(gable.Cells.Where(c => c.Y == 5).All(c => c.Z == 2), Is.True, "the ridge is the middle row");
        // And along z when the rectangle is longer that way.
        var other = Parse("roof gable 0 0 4 6 0 planks");
        Assert.That(other.Cells.Where(c => c.Y == 2).All(c => c.X == 2), Is.True);
    }

    [Test]
    public void ClearCutsADoorwayAndLaterLinesWin()
    {
        var withDoorway = Parse("walls 0 0 0 4 2 4 cobblestone\nclear 2 0 0 2 1 0");
        Assert.That(withDoorway.Cells, Has.Count.EqualTo(48 - 2));
        Assert.That(withDoorway.Cells.Any(c => c is { X: 2, Z: 0, Y: < 2 }), Is.False);

        var window = Parse("walls 0 0 0 4 2 4 cobblestone\nblock 2 1 0 glass");
        Assert.That(window.Cells.Single(c => c is { X: 2, Y: 1, Z: 0 }).Item, Is.EqualTo("minecraft:glass"), "the later line replaces the wall block");
        Assert.That(window.Needs["minecraft:glass"], Is.EqualTo(1));
        Assert.That(window.Needs["minecraft:cobblestone"], Is.EqualTo(47));
    }

    [Test]
    public void MaterialsAreNamedTheWayPeopleSayThem()
    {
        Assert.That(BuildScript.ResolveMaterial("stone bricks", Data), Is.EqualTo("minecraft:stone_bricks"));
        Assert.That(BuildScript.ResolveMaterial("Stone-Brick", Data), Is.EqualTo("minecraft:stone_bricks"));
        Assert.That(BuildScript.ResolveMaterial("oak planks", Data), Is.EqualTo("minecraft:oak_planks"));
        Assert.That(BuildScript.ResolveMaterial("wood", Data), Is.EqualTo("planks"), "wood means any planks");
        Assert.That(BuildScript.ResolveMaterial("wooden", Data), Is.EqualTo("planks"));
        Assert.That(BuildScript.ResolveMaterial("cobble", Data), Is.EqualTo("minecraft:cobblestone"));
        Assert.That(BuildScript.ResolveMaterial("minecraft:glass", Data), Is.EqualTo("minecraft:glass"));
        Assert.That(BuildScript.ResolveMaterial("bricks", Data), Is.EqualTo("minecraft:bricks"));
        Assert.That(BuildScript.ResolveMaterial("brick", Data), Is.EqualTo("minecraft:bricks"));
        Assert.That(BuildScript.Matcher("planks")(new SpawnDev.Minecraft.Items.ItemStack(Data.Item("minecraft:birch_planks")!.Id, 3, new Dictionary<string, object?>(), [])), Is.True);
        Assert.That(BuildScript.Matcher("minecraft:cobblestone")(new SpawnDev.Minecraft.Items.ItemStack(Data.Item("minecraft:stone")!.Id, 3, new Dictionary<string, object?>(), [])), Is.False);
    }

    [Test]
    public void EveryWayAScriptCanBeWrongSaysWhatToFix()
    {
        string Message(string script) => Assert.Throws<BuildScriptException>(() => Parse(script))!.Message;

        Assert.That(Message("paint 0 0 0 cobblestone"), Does.Contain("don't know the command \"paint\"").And.Contain("fill x1 y1 z1"), "an unknown command gets the grammar");
        Assert.That(Message("fill 0 0 0 1 1 1 unobtainium"), Does.Contain("I don't know a block called \"unobtainium\""));
        Assert.That(Message("fill 0 0 0 1 1 1 water_bucket"), Does.Contain("don't know a block called"), "an item that is not a block cannot be built with");
        Assert.That(Message("fill 0 0 0 1 1 cobblestone"), Does.Contain("expected a number"));
        Assert.That(Message("fill 0 0 0 30 1 1 cobblestone"), Does.Contain("between 0 and 23"));
        Assert.That(Message("fill 0 0 0 1 9 1 cobblestone"), Does.Contain("y must be between 0 and 5").And.Contain("reach"));
        Assert.That(Message("door 1 0 0"), Does.Contain("needs a side"));
        Assert.That(Message("roof dome 0 0 4 4 0 planks"), Does.Contain("flat, gable or pyramid"));
        Assert.That(Message("# nothing here\n\n"), Does.Contain("builds nothing"));
        Assert.That(Message("door"), Does.Contain("line 1"), "the line is named");
        Assert.That(Message("fill 0 0 0 23 5 23 cobblestone"), Does.Contain("more than 1500"));
        Assert.That(Message("block 0 0 0 stone\nblock 1 0 0 dirt\nblock 2 0 0 sand\nblock 3 0 0 glass\nblock 4 0 0 bricks\nblock 5 0 0 planks\nblock 6 0 0 cobblestone"),
            Does.Contain("7 different materials"));
        Assert.That(Message("block 0 0 0 door"), Does.Contain("use the door command"));
    }

    [Test]
    public void DoorsAndTorchesAreLate_AndDoorsNeedASide()
    {
        var s = Parse("walls 0 0 0 4 2 4 cobblestone\nclear 2 0 0 2 1 0\ndoor 2 0 0 south\ntorch 1 0 1");
        var door = s.Cells.Single(c => c.Item == "door");
        Assert.That((door.Late, door.Facing), Is.EqualTo((true, BlockFace.South)));
        Assert.That(s.Cells.Single(c => c.Item == "minecraft:torch").Late, Is.True);
        Assert.That(s.Needs["door"], Is.EqualTo(1));

        // Placed last, whatever the order the script gave them in.
        var order = BuildScript.Order(s.Cells, (x, y, z) => y < 0);
        Assert.That(order.TakeLast(2).Select(c => c.Item), Is.EquivalentTo(new[] { "door", "minecraft:torch" }));
    }

    [Test]
    public void EveryBlockHasSomethingToClickAgainstWhenItsTurnComes_LayerByLayer()
    {
        var script = Parse("walls 0 0 0 4 2 6 cobblestone\nfloor 0 0 4 6 0 planks\nclear 2 0 0 2 1 0\nroof gable 0 0 4 6 3 planks\ndoor 2 0 0 south");
        var order = BuildScript.Order(script.Cells, (x, y, z) => y < 0);       // the world: solid ground below y = 0, nothing else
        Assert.That(order, Has.Count.EqualTo(script.Cells.Count), "nothing is lost");
        var placed = new HashSet<(int, int, int)>();
        bool Solid(int x, int y, int z) => y < 0 || placed.Contains((x, y, z));
        foreach (var c in order)
        {
            var supported = Solid(c.X, c.Y - 1, c.Z) || Solid(c.X + 1, c.Y, c.Z) || Solid(c.X - 1, c.Y, c.Z) || Solid(c.X, c.Y, c.Z + 1) || Solid(c.X, c.Y, c.Z - 1) || Solid(c.X, c.Y + 1, c.Z);
            Assert.That(supported, Is.True, $"{c} had nothing to lean on when its turn came");
            placed.Add((c.X, c.Y, c.Z));
        }
        // Layers rise: once a layer is finished the builder never has to come back down to it (the doorway cells are the late ones).
        var body = order.Where(c => !c.Late).Select(c => c.Y).ToList();
        Assert.That(body, Is.Ordered, "ground first, roof last");
        Assert.That(order[^1].Item, Is.EqualTo("door"));
    }

    [Test]
    public void FloatingCellsGoLast_SoTheyDoNotHoldTheRestUp()
    {
        var order = BuildScript.Order(Parse("fill 0 0 0 2 0 2 cobblestone\nblock 10 4 10 glass").Cells, (x, y, z) => y < 0);
        Assert.That(order[^1].Item, Is.EqualTo("minecraft:glass"), "nothing to lean on, so after everything that can be built");
        Assert.That(order, Has.Count.EqualTo(10));
    }

    [Test]
    public void ABuildIsBoundedInEverything()
    {
        Assert.That(new BuildLimits(), Is.EqualTo(new BuildLimits(1500, 24, 6, 6)));
        // A big shell is still a legal script (21 x 21 x 6 is 1202 blocks), and a bigger one is too much.
        Assert.That(Parse("hollow 0 0 0 20 5 20 cobblestone").Cells.Count, Is.EqualTo(21 * 21 * 6 - 19 * 19 * 4));
        Assert.Throws<BuildScriptException>(() => Parse("hollow 0 0 0 23 5 23 cobblestone"));
    }
}
