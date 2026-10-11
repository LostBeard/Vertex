using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Data;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>Vertex's own pieces that need no server or GPU.</summary>
public class VertexUnitTests
{
    private static double Power(float[] pcm, double hz, int rate)
    {
        var k = 2 * Math.Cos(2 * Math.PI * hz / rate);
        double s1 = 0, s2 = 0;
        foreach (var x in pcm)
        {
            var s0 = x + k * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - k * s1 * s2;
    }

    [Test]
    public void Downsample_KeepsSpeechBand_AndRemovesWhatWouldAlias()
    {
        short[] Tone(double hz) => Enumerable.Range(0, 48000).Select(i => (short)(Math.Sin(2 * Math.PI * hz * i / 48000) * 16000)).ToArray();
        var speech = Audio.Downsample48To16(Tone(440));
        Assert.That(speech.Length, Is.EqualTo(16000));
        var rms = Math.Sqrt(speech.Skip(100).Take(15800).Average(x => x * (double)x));
        Assert.That(rms, Is.EqualTo(16000 / 32768.0 / Math.Sqrt(2)).Within(0.01), "440 Hz passes at full level");

        // 10 kHz is above the 8 kHz Nyquist of 16 kHz: unfiltered it would fold down to 6 kHz.
        var high = Audio.Downsample48To16(Tone(10000));
        Assert.That(Power(high, 6000, 16000), Is.LessThan(Power(speech, 440, 16000) * 1e-3), "10 kHz does not alias to 6 kHz");
    }

    [Test]
    public void Upsample_DoublesTheRate()
    {
        var input = Enumerable.Range(0, 2400).Select(i => (float)Math.Sin(2 * Math.PI * 300 * i / 24000) * 0.5f).ToArray();
        var output = Audio.Upsample24To48(input);
        Assert.That(output.Length, Is.EqualTo(4800));
        Assert.That(output[200], Is.EqualTo((short)(input[100] * 32767)));
        Assert.That(output.Max(), Is.InRange((short)16000, (short)16400));
    }

    [Test]
    public void Mouth_SplitsSentencesAndChatLines()
    {
        Assert.That(Mouth.Sentences("Hi Aubri! Want to build a castle? I have 3.5 stacks of stone."),
            Is.EqualTo(new[] { "Hi Aubri!", "Want to build a castle?", "I have 3.5 stacks of stone." }));
        Assert.That(Mouth.Sentences("no punctuation here"), Is.EqualTo(new[] { "no punctuation here" }));
        var chunks = Mouth.ChatChunks(new string('a', 100) + " " + new string('b', 200), 250).ToList();
        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.That(chunks.All(c => c.Length <= 250));
    }

    [Test]
    public void Skills_ResolveWhatPlayersCallBlocks()
    {
        var skills = new Skills(new MinecraftClient(GameProfile.Offline("t")));
        var d = GameData.Vanilla;
        Assert.That(skills.ResolveBlocks("wood"), Does.Contain(d.Block("oak_log")!.Id).And.Contain(d.Block("spruce_log")!.Id));
        Assert.That(skills.ResolveBlocks("diamonds"), Does.Contain(d.Block("diamond_ore")!.Id).And.Contain(d.Block("deepslate_diamond_ore")!.Id));
        Assert.That(skills.ResolveBlocks("stone"), Does.Contain(d.Block("stone")!.Id));
        Assert.That(skills.ResolveBlocks("dirt"), Does.Contain(d.Block("dirt")!.Id));
        Assert.That(skills.ResolveBlocks("oak logs"), Does.Contain(d.Block("oak_log")!.Id));
        Assert.That(skills.ResolveBlocks("unobtainium"), Is.Empty);
    }

    [Test]
    public void Characters_AreSeededAndEditable()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vertex-unit-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new CharacterStore(folder);
            Assert.That(store.All().Select(c => c.Name), Is.SupersetOf(new[] { "Vertex", "Pixel", "Captain", "Bolt" }));
            var pixel = store.Find("pixel")!;
            pixel.Style.Bravery = 0.1;
            store.Save(pixel);
            Assert.That(new CharacterStore(folder).Find("Pixel")!.Style.Bravery, Is.EqualTo(0.1));
            Assert.That(VertexAi.Voices, Does.Contain(store.Find("Captain")!.Voice), "every built-in voice exists");
            foreach (var c in Character.BuiltIn) Assert.That(VertexAi.Voices, Does.Contain(c.Voice), c.Name);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void WorldMemory_FindsPlacesByLooseNames_AndKeepsThemPerServer()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vertex-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = WorldMemory.Load(folder, "mc.spawndev.com_25565");
            a.SetPlace("our home", "minecraft:overworld", 10, 64, -20, "Aubri");
            a.SetPlace("the mine", "minecraft:overworld", 100, 12, 5, "TJ");
            a.SetPlace("TJ's house", "minecraft:overworld", 1, 2, 3, "TJ");
            a.AddFact("Aubri loves axolotls");
            Assert.That(a.FindPlace("home")?.X, Is.EqualTo(10), "articles do not matter");
            Assert.That(a.FindPlace("The Mine")?.Y, Is.EqualTo(12));
            Assert.That(a.FindPlace("hom")?.Name, Is.EqualTo("home"), "speech-to-text spelling");
            Assert.That(a.FindPlace("tjs house")?.Z, Is.EqualTo(3));
            Assert.That(a.FindPlace("castle"), Is.Null);
            a.SetPlace("home", "minecraft:overworld", 11, 65, -21, "TJ");
            Assert.That(a.Places.Count(p => p.Name == "home"), Is.EqualTo(1), "marking again moves it");

            var again = WorldMemory.Load(folder, "mc.spawndev.com_25565");
            Assert.That(again.FindPlace("home"), Is.EqualTo(new Place("home", "minecraft:overworld", 11, 65, -21, "TJ")));
            Assert.That(again.Facts, Is.EqualTo(new[] { "Aubri loves axolotls" }));
            Assert.That(WorldMemory.Load(folder, "localhost_25565").Places, Is.Empty, "another server has its own world");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Test]
    public void BuiltBlocks_AreThePlayersBuildsNotNature()
    {
        var data = GameData.Vanilla;
        bool Built(string n) => MinecraftClient.DefaultIsBuiltBlock(data.Block(n)!);
        string[] built = ["oak_planks", "glass", "white_wool", "oak_door", "red_bed", "chest", "stone_bricks", "white_concrete", "oak_stairs",
            "cobblestone_wall", "farmland", "diamond_block", "jack_o_lantern", "oak_fence_gate", "torch", "obsidian", "spawner", "glass_pane", "bookshelf"];
        string[] natural = ["stone", "dirt", "grass_block", "oak_log", "deepslate", "sand", "gravel", "netherrack", "red_terracotta",
            "raw_copper_block", "redstone_ore", "deepslate_diamond_ore", "glowstone", "basalt", "oak_leaves", "cobblestone", "andesite", "clay", "snow_block", "ice"];
        Assert.That(built.Where(n => !Built(n)), Is.Empty, "should be protected");
        Assert.That(natural.Where(Built), Is.Empty, "should be diggable");
    }

    [Test]
    public async Task TaskRunner_Interrupt_RunsTheUrgentJob_ThenResumes()
    {
        var tasks = new TaskRunner();
        var ended = new List<string>();
        tasks.Finished += o => { lock (ended) ended.Add(o.Task); };
        var followStarts = 0;
        async Task<(bool, string)> Follow(CancellationToken ct)
        {
            Interlocked.Increment(ref followStarts);
            await Task.Delay(Timeout.Infinite, ct);
            return (true, "");
        }
        tasks.Start("following Aubri", Follow);
        await Wait.UntilAsync(() => followStarts == 1, TimeSpan.FromSeconds(5), () => "follow started");

        tasks.Interrupt("getting away from a creeper", async ct => { await Task.Delay(50, ct); return (true, "ran"); });
        Assert.That(tasks.Current, Is.EqualTo("getting away from a creeper"));
        await Wait.UntilAsync(() => followStarts == 2, TimeSpan.FromSeconds(5), () => $"{followStarts} follow starts, now {tasks.Current}");
        Assert.That(tasks.Current, Is.EqualTo("following Aubri"), "back to following");
        lock (ended) Assert.That(ended, Is.EqualTo(new[] { "getting away from a creeper" }), "the interrupted job is not reported as ended");

        // Something new started during the urgent job wins: no resume over it.
        tasks.Interrupt("getting away from a creeper", async ct => { await Task.Delay(100, ct); return (true, "ran"); });
        tasks.Start("collecting wood", Follow);
        await Task.Delay(300);
        Assert.That(tasks.Current, Is.EqualTo("collecting wood"));
        Assert.That(followStarts, Is.EqualTo(3));

        // Idle when interrupted: idle afterwards.
        tasks.Stop();
        tasks.Interrupt("getting away from a creeper", async ct => { await Task.Delay(10, ct); return (true, "ran"); });
        await Wait.UntilAsync(() => tasks.Current == null, TimeSpan.FromSeconds(5), () => $"now {tasks.Current}");
        Assert.That(followStarts, Is.EqualTo(3));
    }

    [Test]
    public void HouseBlueprint_CountsAndOrder()
    {
        static bool Planks(SpawnDev.Minecraft.Items.ItemStack s) => s.Name.EndsWith("_planks");
        var plan = Blueprints.House(0, 64, 0, 5, BlockFace.North, Planks, glass: true, torch: true, door: true);
        Assert.That(plan.Count(p => p.Description is "wall blocks" or "roof blocks"), Is.EqualTo(Blueprints.HouseBlocks(5, glass: true)).And.EqualTo(68));
        Assert.That(plan.Count(p => p.Description == "glass"), Is.EqualTo(3));
        Assert.That(plan.Count, Is.EqualTo(68 + 3 + 1 + 1));
        Assert.That(plan.Select(p => (p.X, p.Y, p.Z)).Distinct().Count(), Is.EqualTo(plan.Count), "no cell twice");
        Assert.That(plan[^1], Is.EqualTo(plan.Single(p => p.Description == "a door")), "door last");
        Assert.That((plan[^1].X, plan[^1].Y, plan[^1].Z, plan[^1].Facing), Is.EqualTo((0, 64, -2, (BlockFace?)BlockFace.North)));
        Assert.That(plan.Any(p => p.X == 0 && p.Z == 0 && p.Y < 66), Is.False, "nothing where the builder stands");
        Assert.That(plan.Any(p => p.X == 0 && p.Z == -2 && p.Y == 65), Is.False, "the doorway's top half stays open");
        // Every block leans on the ground or on a block placed before it.
        var placed = new HashSet<(int, int, int)>();
        foreach (var p in plan)
        {
            var leans = p.Y == 64 || placed.Contains((p.X, p.Y - 1, p.Z)) || placed.Contains((p.X + 1, p.Y, p.Z)) || placed.Contains((p.X - 1, p.Y, p.Z))
                || placed.Contains((p.X, p.Y, p.Z + 1)) || placed.Contains((p.X, p.Y, p.Z - 1));
            Assert.That(leans, $"{p} has nothing to be placed against");
            placed.Add((p.X, p.Y, p.Z));
        }
        Assert.That(Blueprints.HouseBlocks(7, glass: false), Is.EqualTo(24 * 3 - 2 + 49));
    }

    [Test]
    public void FastIntents_CleanStripsTheNameAndPoliteness()
    {
        Assert.That(FastIntents.Clean("Hey Vertex, can you follow me please?", "Vertex"), Is.EqualTo("follow me"));
        Assert.That(FastIntents.Clean("PIXEL! Come here!!", "Pixel"), Is.EqualTo("come here"));
        Assert.That(FastIntents.Clean("okay vertex don't follow me", "Vertex"), Is.EqualTo("don't follow me"));
    }
}
