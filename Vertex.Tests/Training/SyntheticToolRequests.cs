using System.Text.Json;

namespace Vertex.Tests.Training;

/// <summary>What a request must produce: the tool (null: no tool at all) and what its arguments must hold.</summary>
public sealed record ToolRequest(string Says, string? Tool, Func<JsonElement, bool> ArgsOk);

/// <summary>
/// Requests written for training an adapter, covering every tool (Tuvok's spec, 2026-11-04): about 30 per tool in varied wording, values and speakers, plus about 15% that want no tool at all, so the adapter does not learn to call one for everything.
/// Nothing here came from anyone in the family: the speakers are made up. None may match a ToolChoiceTests case, which stay the gate and are never trained on (checked by <see cref="SyntheticToolRequestTests"/>).
/// </summary>
public static class SyntheticToolRequests
{
    public const int PerTool = 30;
    private static readonly string[] Frames =
    [
        "{0}", "please {0}", "can you {0}?", "could you {0}?", "hey, {0}", "{0} please", "I need you to {0}", "would you {0}?", "go ahead and {0}", "quick, {0}", "okay, {0}", "{0} for me", "I want you to {0}", "will you {0}?", "time to {0}", "let's have you {0}",
    ];
    private static readonly string[] Players = ["Sam", "Robin", "Alex", "Jo", "Kim", "Pat"];
    private static readonly string[] Blocks = ["wood", "stone", "dirt", "sand", "coal", "iron", "gravel", "clay"];
    private static readonly string[] Items = ["bread", "torch", "stick", "cobblestone", "iron_ingot", "carrot", "wheat", "apple"];
    private static readonly string[] Mobs = ["zombie", "skeleton", "spider", "creeper", "witch", "husk"];
    private static readonly string[] Dirs = ["north", "south", "east", "west"];
    private static readonly string[] Materials = ["stone", "wood", "cobblestone", "planks"];
    private static readonly string[] Places = ["home", "the mine", "the farm", "the lake", "the tower", "the big tree"];
    private static readonly string[] Crops = ["wheat", "carrots", "potatoes", "beetroot"];
    private static readonly string[] Animals = ["cow", "sheep", "pig", "chicken"];
    private static readonly string[] Numbers = ["3", "5", "8", "12", "20"];

    private static string Text(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()).Trim().ToLowerInvariant() : "";
    private static bool Has(JsonElement a, string k, string part) => Text(a, k).Replace('_', ' ').Contains(part.ToLowerInvariant().Replace('_', ' '), StringComparison.Ordinal);
    private static bool Blank(JsonElement a, string k) => Text(a, k).Length == 0;
    private static bool Num(JsonElement a, string k, string n) => Text(a, k) == n;
    private static bool MeOr(JsonElement a, string k, string? name) => name != null ? Has(a, k, name) : Blank(a, k) || Text(a, k) == "me" || Players.Any(p => Text(a, k) == p.ToLowerInvariant());

    /// <summary>All of them, in a fixed order for a given seed. Speakers and wording are drawn from a seeded random: the same list every time.</summary>
    public static List<ToolRequest> All(int seed = 20261104)
    {
        var random = new Random(seed);
        var all = new List<ToolRequest>();
        void Tool(string tool, IEnumerable<(string Says, Func<JsonElement, bool> Ok)> bases)
        {
            var list = bases.ToList();
            var seen = new HashSet<string>();
            for (var tries = 0; seen.Count < PerTool && tries < PerTool * 40; tries++)
            {
                var (says, ok) = list[random.Next(list.Count)];
                var line = string.Format(Frames[random.Next(Frames.Length)], says);
                line = random.Next(3) == 0 ? line : "Vertex, " + line;
                if (seen.Add(line)) all.Add(new ToolRequest(line, tool, ok));
            }
        }
        static (string, Func<JsonElement, bool>) R(string says, Func<JsonElement, bool>? ok = null) => (says, ok ?? (_ => true));

        Tool("follow_player", new[] { "follow me around", "come along with me", "stick with me", "walk with me" }.Select(s => R(s, a => MeOr(a, "player", null))).Concat(Players.Select(p => R($"follow {p}", a => MeOr(a, "player", p)))));
        Tool("come_here", new[] { "come over here", "come to me", "get over here", "walk over to me" }.Select(s => R(s, a => MeOr(a, "player", null))).Concat(Players.Select(p => R($"go over to {p}", a => MeOr(a, "player", p)))));
        Tool("go_to", new[] { (10, 64, -20), (250, 70, 90), (-35, 66, 400), (0, 80, 0), (77, 62, -300) }.SelectMany(c => new[] { R($"walk to {c.Item1} {c.Item2} {c.Item3}", a => Num(a, "x", c.Item1.ToString()) && Num(a, "z", c.Item3.ToString())), R($"go to coordinates {c.Item1} {c.Item2} {c.Item3}", a => Num(a, "x", c.Item1.ToString()) && Num(a, "z", c.Item3.ToString())) }));
        Tool("collect", Blocks.SelectMany(b => Numbers.Select(n => R($"mine {n} {b}", a => Has(a, "block", b) && Num(a, "count", n))).Append(R($"gather some {b}", a => Has(a, "block", b)))));
        Tool("give", Items.SelectMany(i => Numbers.Take(2).Select(n => R($"bring me {n} {i.Replace('_', ' ')}", a => Has(a, "item", i) && Num(a, "count", n)))).Concat(Players.SelectMany(p => Items.Take(3).Select(i => R($"give {p} your {i.Replace('_', ' ')}", a => Has(a, "item", i) && MeOr(a, "player", p))))));
        Tool("attack", Mobs.SelectMany(m => new[] { R($"kill that {m}", a => Has(a, "target", m)), R($"fight the {m}", a => Has(a, "target", m)), R($"attack the {m}", a => Has(a, "target", m)) }).Append(R("clear out the monsters", a => Has(a, "target", "monster"))));
        Tool("craft", new[] { "wooden_pickaxe", "stone_sword", "torch", "crafting_table", "iron_pickaxe", "chest", "ladder" }.SelectMany(i => new[] { R($"craft a {i.Replace('_', ' ')}", a => Has(a, "item", i)), R($"make me a {i.Replace('_', ' ')}", a => Has(a, "item", i)) }));
        Tool("guard", new[] { "protect me", "keep me safe from monsters", "watch my back", "be my bodyguard" }.Select(s => R(s, a => MeOr(a, "player", null))).Concat(Players.Select(p => R($"guard {p}", a => MeOr(a, "player", p)))));
        Tool("store_items", new[] { "cobblestone", "dirt", "sand" }.SelectMany(i => new[] { R($"put your {i} in the chest", a => Has(a, "item", i)), R($"store the {i}", a => Has(a, "item", i)) }).Append(R("put everything in the chest", a => Has(a, "item", "everything"))).Append(R("empty your pockets into the barrel", a => Has(a, "item", "everything"))));
        Tool("take_items", Items.SelectMany(i => new[] { R($"get the {i.Replace('_', ' ')} out of the chest", a => Has(a, "item", i)), R($"take {Numbers[0]} {i.Replace('_', ' ')} from the barrel", a => Has(a, "item", i) && Num(a, "count", Numbers[0])) }));
        Tool("sleep", new[] { "go to bed", "sleep in the bed", "get some sleep", "sleep through the night", "lie down in that bed" }.Select(s => R(s)));
        Tool("build_wall", Dirs.SelectMany(d => Numbers.Take(3).Select(n => R($"build a wall {n} long going {d}", a => Has(a, "direction", d)))).Concat(Materials.Select(m => R($"put a {m} wall around me", a => Has(a, "material", m)))));
        Tool("build_tower", Numbers.Skip(1).SelectMany(n => new[] { R($"build a lookout tower {n} high", a => Num(a, "height", n)), R($"put up a tower {n} blocks tall", a => Num(a, "height", n)) }).Concat(Materials.Select(m => R($"build a {m} tower", a => Has(a, "material", m)))));
        Tool("build_bridge", Dirs.SelectMany(d => new[] { R($"build a bridge going {d}", a => Has(a, "direction", d)), R($"make a bridge {d} across the water", a => Has(a, "direction", d)) }).Concat(Materials.Select(m => R($"build a {m} bridge across the ravine", a => Has(a, "material", m)))));
        Tool("learn_blocks", new[] { "study these strange blocks", "learn what these weird blocks are", "figure out these modded blocks", "feel out the unknown blocks here" }.Select(s => R(s)));
        Tool("explore", Dirs.SelectMany(d => new[] { R($"explore to the {d}", a => Has(a, "direction", d)), R($"go exploring {d} and tell me what you find", a => Has(a, "direction", d)) }).Concat(new[] { "200", "50", "500" }.Select(n => R($"scout around {n} blocks out", a => Num(a, "distance", n)))));
        Tool("build_shape", new[] { "pyramid", "house", "castle", "platform", "pen" }.SelectMany(s => new[] { R($"put up a {s}", a => Has(a, "shape", s)) }.Concat(Materials.Take(2).Select(m => R($"build a {m} {s}", a => Has(a, "shape", s) && Has(a, "shape", m))))).Append(R("carry on with the build", a => Has(a, "shape", "continue"))));
        Tool("trade", new[] { "emerald", "bread", "carrot" }.Select(w => R($"get a {w} from the villager", a => Has(a, "want", w) && Blank(a, "give"))).Concat(new[] { "wheat", "coal", "paper" }.Select(g => R($"trade your {g} to the villager", a => Has(a, "give", g)))).Concat(new[] { "see what the trader has", "check the villager's offers", "look at what this villager offers" }.Select(s => R(s, a => Blank(a, "want") && Blank(a, "give")))));
        Tool("farm", Crops.Select(c => R($"plant a field of {c}", a => Has(a, "action", "plant") && Has(a, "crop", c))).Concat(Crops.Select(c => R($"start a {c} patch", a => Has(a, "action", "plant") && Has(a, "crop", c)))).Concat(new[] { "pick the ripe crops", "bring in the harvest", "gather what's ripe in the field" }.Select(s => R(s, a => Blank(a, "action") || Has(a, "action", "harvest")))));
        Tool("animals", Animals.Select(x => R($"breed the {x}s", a => Has(a, "action", "breed") && Has(a, "animal", x))).Concat(new[] { "get some wool off that sheep", "use the shears on the sheep" }.Select(s => R(s, a => Has(a, "action", "shear")))).Concat(new[] { "get milk from the cow", "fill the bucket from the cow" }.Select(s => R(s, a => Has(a, "action", "milk")))));
        Tool("fish", new[] { "catch some fish", "cast your rod", "do some fishing" }.Select(s => R(s)).Concat(Numbers.Select(n => R($"catch {n} fish", a => Num(a, "count", n)))));
        Tool("light_up", new[] { "light this place up", "put torches around here", "it's getting dark here, light it up", "make it bright so nothing spawns" }.Select(s => R(s)).Concat(new[] { "8", "12", "16" }.Select(n => R($"put torches {n} blocks around", a => Num(a, "radius", n)))));
        Tool("shoot", Mobs.SelectMany(m => new[] { R($"shoot the {m}", a => Has(a, "target", m)), R($"use your bow on that {m}", a => Has(a, "target", m)) }));
        Tool("smelt", new[] { "iron_ingot", "glass", "cooked_beef", "charcoal", "cooked_porkchop" }.SelectMany(i => new[] { R($"smelt some {i.Replace('_', ' ')}", a => Has(a, "result", i)), R($"cook up {Numbers[1]} {i.Replace('_', ' ')} in the furnace", a => Has(a, "result", i) && Num(a, "count", Numbers[1])) }));
        Tool("eat", new[] { "eat something", "have a bite", "eat some food", "you should eat", "grab a snack from your pockets" }.Select(s => R(s)));
        Tool("stay", new[] { "don't move", "stay put", "wait here", "hold your position", "stay where you are" }.Select(s => R(s)));
        Tool("stop", new[] { "stop that", "stop what you're doing", "knock it off and stand still", "quit it and stop" }.Select(s => R(s)));
        Tool("look_at", Players.Select(p => R($"look at {p}", a => MeOr(a, "player", p))).Concat(new[] { "look at me", "turn and face me" }.Select(s => R(s, a => MeOr(a, "player", null)))));
        Tool("inventory", new[] { "show what you're carrying", "list your inventory", "check your pockets and tell me what's in them", "go through your inventory" }.Select(s => R(s)));
        Tool("switch_character", new[] { "Rose", "Captain", "Pixel", "Sage" }.SelectMany(n => new[] { R($"switch to being {n}", a => Has(a, "name", n)), R($"become {n}", a => Has(a, "name", n)) }));
        Tool("mark_place", Places.SelectMany(p => new[] { R($"remember this spot as {p}", a => Has(a, "name", p.Replace("the ", ""))), R($"mark this place as {p}", a => Has(a, "name", p.Replace("the ", ""))) }));
        Tool("go_to_place", Places.SelectMany(p => new[] { R($"go back to {p}", a => Has(a, "name", p.Replace("the ", ""))), R($"walk to {p}", a => Has(a, "name", p.Replace("the ", ""))) }));
        Tool("forget_place", Places.SelectMany(p => new[] { R($"forget about {p}", a => Has(a, "name", p.Replace("the ", ""))), R($"take {p} off your list of places", a => Has(a, "name", p.Replace("the ", ""))) }));
        Tool("remember", Players.SelectMany(p => new[] { "loves dragons", "is building a boat", "hates spiders" }.Select(f => R($"remember that {p} {f}", a => Has(a, "fact", f.Split(' ')[^1])))));
        Tool("look_around", new[] { "have a look around", "tell me what's around you", "check your surroundings", "where's the nearest water?", "is there a cliff near you?" }.Select(s => R(s)));
        Tool("recall", new[] { "what did you build earlier", "where did we find iron last time", "what did you do yesterday", "what happened the last time you went exploring" }.SelectMany(s => new[] { R($"think back, {s}?", a => !Blank(a, "question")), R($"look in your log: {s}?", a => !Blank(a, "question")) }));
        Tool("set_goal", new[] { "collect 64 diamonds", "find a village", "explore for a stronghold", "gather a stack of iron", "map out this island" }.SelectMany(g => new[] { R($"make it your goal to {g}", a => Has(a, "goal", g.Split(' ')[^1])), R($"over time, {g}", a => Has(a, "goal", g.Split(' ')[^1])) }));
        Tool("finish_goal", new[] { "that goal is done", "you can drop that goal now", "cross that goal off", "the goal is finished", "no need for that goal any more" }.Select(s => R(s)));

        // About 15% that want words and no tool: small talk, and questions he answers from the note.
        var chat = new[]
        {
            "hi there!", "good morning", "thanks, that was great", "nice work", "you're funny", "what's your favourite animal?", "do you like the rain?", "how are you feeling?", "what time of day is it?", "how much health have you got?", "are you hungry?",
            "tell me a joke", "what's the best thing to build?", "what do creepers do?", "why do skeletons shoot?", "where do you like to go?", "I'm back!", "that was close", "good night", "wow, look at that sunset", "how do you make a torch?", "what are you doing right now?",
            "what's your name?", "do you have any friends?", "is it safe out here?", "are there monsters near you?", "I had a great day today", "you're doing really well", "see you later", "what's a good name for a dog?",
        };
        var chatWanted = all.Count * 15 / 85;
        var chatSeen = new HashSet<string>();
        for (var tries = 0; chatSeen.Count < chatWanted && tries < chatWanted * 60; tries++)
        {
            var line = chat[random.Next(chat.Length)];
            line = random.Next(3) switch { 0 => "Vertex, " + line, 1 => "hey Vertex, " + line, _ => line };
            line = random.Next(4) == 0 ? line + " " + chat[random.Next(chat.Length)] : line;
            if (chatSeen.Add(line)) all.Add(new ToolRequest(line, null, _ => true));
        }
        return all;
    }

    /// <summary>How the request reaches the model: a made-up friend's chat line, in the form MinecraftChannel gives it.</summary>
    public static string Spoken(ToolRequest r, Random random) => $"{Players[random.Next(Players.Length)]} says (in chat): {r.Says}";

    /// <summary>A state note in the form the game body writes it (VertexBrain.DescribeNow), with varied values. Holds nobody's name: the about-a-person lines are what the real log refuses.</summary>
    public static string StateNote(Random random)
    {
        string[] doing = ["not doing anything special", "working toward a stone pickaxe", "looking for somewhere I can live", "collecting 8 wood"];
        string[] carrying = ["nothing", "3 oak log, 1 wooden pickaxe", "12 dirt, 1 stone sword, 2 bread", "1 iron pickaxe, 20 cobblestone, 5 torch"];
        string[] time = ["daytime", "sunset", "night", "sunrise"];
        string[] near = ["nobody", "cow (12 blocks)", "zombie (9 blocks), spider (14 blocks)", "sheep (5 blocks), Sam (player, 7 blocks)"];
        return "What is going on around you (background for you, not something to say):\n"
            + $"- In the game you are at x {random.Next(-900, 900)} y {random.Next(55, 90)} z {random.Next(-900, 900)} in overworld, health {random.Next(4, 21) / 2.0:0.#} of 10 hearts, food {random.Next(8, 21) / 2.0:0.#} of 10, game mode survival.\n"
            + $"- It is {time[random.Next(time.Length)]} in the game.\n"
            + $"- You are carrying: {carrying[random.Next(carrying.Length)]}. Holding: nothing.\n"
            + $"- You are {doing[random.Next(doing.Length)]}.\n"
            + $"- Nearby: {near[random.Next(near.Length)]}.\n";
    }
}

public class SyntheticToolRequestTests
{
    private static readonly List<ToolRequest> Requests = SyntheticToolRequests.All();

    [Test]
    public void EveryToolHasItsShare_AndAbout15PercentWantNoTool()
    {
        var byTool = Requests.Where(r => r.Tool != null).GroupBy(r => r.Tool!).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (tool, n) in byTool) TestContext.Out.WriteLine($"{tool}: {n}");
        Assert.That(byTool.Count, Is.EqualTo(38), "every tool on offer (build_script is off)");
        Assert.That(byTool.Values.Min(), Is.GreaterThanOrEqualTo(25), "about 30 each: " + string.Join(", ", byTool.Where(k => k.Value < 25).Select(k => k.Key)));
        var share = Requests.Count(r => r.Tool == null) / (double)Requests.Count;
        Assert.That(share, Is.InRange(0.10, 0.20));
    }

    [Test]
    public void NoRequestRepeats_AndTheListIsTheSameEveryTime()
    {
        Assert.That(Requests.Select(r => r.Says).Distinct().Count(), Is.EqualTo(Requests.Count));
        Assert.That(SyntheticToolRequests.All().Select(r => r.Says), Is.EqualTo(Requests.Select(r => r.Says)));
    }

    [Test]
    public void NothingIsTheWordingOfAToolChoiceCase_TheGateIsNeverTrainedOn()
    {
        static string Norm(string s) => new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Replace("vertex ", "").Replace("please", "").Replace("can you", "").Trim();
        var gate = ToolChoiceTests.CaseWordings.Select(Norm).ToHashSet();
        var clash = Requests.Where(r => gate.Contains(Norm(r.Says))).Select(r => r.Says).ToList();
        Assert.That(clash, Is.Empty);
    }

    [Test]
    public void NoFamilyNames()
    {
        foreach (var name in new[] { "Aubri", "Aubs", "Aubriella", "TJ", "Todd" })
            Assert.That(Requests.Where(r => r.Says.Contains(name, StringComparison.OrdinalIgnoreCase)), Is.Empty, name);
    }
}
