using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>System One's instinct: which tool a request wants, from words it has never seen (none of these sentences is in InstinctData).</summary>
public class InstinctTests
{
    private static readonly Instinct Instinct = new();

    // The 18 sentences of ToolChoiceTests, with what the language model should call.
    private static readonly (string Says, string Tool)[] Held =
    [
        ("Vertex, can you build me a castle?", "build_shape"),
        ("Vertex, build a little cabin please", "build_shape"),
        ("Vertex, make a platform to stand on", "build_shape"),
        ("Vertex, keep building where you left off", "build_shape"),
        ("Vertex, what does that villager sell?", "trade"),
        ("Vertex, sell all my wheat to the villager", "trade"),
        ("Vertex, buy some bread from the villager", "trade"),
        ("Vertex, make me a wheat farm", "farm"),
        ("Vertex, harvest the crops", "farm"),
        ("Vertex, breed the cows", "animals"),
        ("Vertex, shear the sheep", "animals"),
        ("Vertex, can you milk the cow?", "animals"),
        ("Vertex, stay right here and wait for me", "stay"),
        ("Vertex, it is too dark, put some torches around", "light_up"),
        ("Vertex, shoot that zombie with your bow", "shoot"),
        ("Vertex, follow me", "follow_player"),
        ("Vertex, get me ten wood", "collect"),
        ("Vertex, let's go fishing!", "fish"),
    ];

    // More that a child might say.
    private static readonly (string Says, string Tool)[] More =
    [
        ("Vertex I want a really big castle with towers", "build_shape"),
        ("hey Vertex make me a tiny house", "build_shape"),
        ("Vertex build something for the chickens to live in", "build_shape"),
        ("Vertex can you chop down that oak tree", "collect"),
        ("Vertex dig up ten dirt for me", "collect"),
        ("Vertex please mine some redstone", "collect"),
        ("Vertex fight the zombie over there", "attack"),
        ("Vertex protect me please", "guard"),
        ("Vertex give me a few apples", "give"),
        ("Vertex put everything in the chest", "store_items"),
        ("Vertex can you cook this fish", "smelt"),
        ("Vertex time for bed", "sleep"),
        ("Vertex don't move", "stay"),
        ("Vertex come here right now", "come_here"),
        ("Vertex come with me to the cave", "follow_player"),
        ("Vertex plant some wheat over there", "farm"),
        ("Vertex feed the pigs carrots so they breed", "animals"),
        ("Vertex I want to go fishing in the lake", "fish"),
        ("Vertex light up the cave", "light_up"),
        ("Vertex use your bow on the skeleton", "shoot"),
        ("Vertex what is in your backpack", "inventory"),
        ("Vertex stop that", "stop"),
        ("Vertex how are you feeling today", "chat"),
        ("Vertex I love you", "chat"),
        ("Vertex what is your favorite animal", "chat"),
        ("Vertex tell me about dragons", "chat"),
        ("Vertex let's go on an adventure", "explore"),
        ("Vertex trade with the villager over there", "trade"),
        ("Vertex let's sell our wheat to him", "trade"),
        ("Vertex eat some bread", "eat"),
    ];

    private static (int Right, List<string> Misses) Score((string Says, string Tool)[] set, float minConfidence = 0f)
    {
        var misses = new List<string>();
        var right = 0;
        foreach (var (says, tool) in set)
        {
            var (guess, confidence) = Instinct.Guess(says);
            if (guess == tool) right++;
            else misses.Add($"\"{says}\": wanted {tool}, instinct {guess} ({confidence:P0})");
        }
        return (right, misses);
    }

    [Test]
    public void TheEighteenToolChoiceSentences_AreGuessedRight()
    {
        var (right, misses) = Score(Held);
        TestContext.Out.WriteLine($"{right} of {Held.Length}\n" + string.Join("\n", misses));
        Assert.That(right, Is.GreaterThanOrEqualTo(15), string.Join("\n", misses));
    }

    [Test]
    public void OtherPhrasings_AreGuessedRight()
    {
        var (right, misses) = Score(More);
        TestContext.Out.WriteLine($"{right} of {More.Length}\n" + string.Join("\n", misses));
        Assert.That(right, Is.GreaterThanOrEqualTo(More.Length * 8 / 10), string.Join("\n", misses));
    }

    [Test]
    public void WhenItIsSure_ItIsRight()
    {
        // What the hint would say: only guesses above the threshold are passed on, and those must be nearly always right.
        var all = Held.Concat(More).ToArray();
        var sure = all.Select(s => (s, g: Instinct.Guess(s.Says))).Where(x => x.g.Confidence >= 0.5f).ToList();
        var wrong = sure.Where(x => x.g.Tool != x.s.Tool).Select(x => $"\"{x.s.Says}\": wanted {x.s.Tool}, instinct {x.g.Tool} ({x.g.Confidence:P0})").ToList();
        TestContext.Out.WriteLine($"{sure.Count} of {all.Length} above 50%, {wrong.Count} of them wrong\n" + string.Join("\n", wrong));
        Assert.That(sure.Count, Is.GreaterThanOrEqualTo(all.Length / 2), "it is sure about at least half");
        Assert.That(wrong.Count, Is.LessThanOrEqualTo(sure.Count / 10), "and wrong about at most one in ten of those: " + string.Join("\n", wrong));
    }

    [Test]
    public void ItIsFast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) Instinct.Guess("Vertex, can you build me a castle?");
        sw.Stop();
        TestContext.Out.WriteLine($"{sw.Elapsed.TotalMilliseconds / 1000:0.000} ms per guess");
        Assert.That(sw.Elapsed.TotalMilliseconds / 1000, Is.LessThan(2.0));
    }
}

/// <summary>The compact form of a tool definition (what the language model is shown).</summary>
public class GameToolCompactTests
{
    [Test]
    public void FirstSentence_KeepsTheFirstSentenceAndCutsLongOnesAtAWord()
    {
        Assert.That(GameTool.FirstSentence("Build near the player. Uses wood. Many more words."), Is.EqualTo("Build near the player."));
        Assert.That(GameTool.FirstSentence("Short one"), Is.EqualTo("Short one"));
        var longOne = GameTool.FirstSentence(string.Join(" ", Enumerable.Repeat("word", 60)));
        Assert.That(longOne.Length, Is.LessThanOrEqualTo(152));
        Assert.That(longOne, Does.EndWith("word.").Or.EndsWith("word"));
    }

    [Test]
    public void WithoutDescriptions_CutsLongDescriptions_AndKeepsNamesTypesEnumsAndRequired()
    {
        var full = """{"type":"object","properties":{"action":{"type":"string","enum":["a","b"],"description":"which one"},"count":{"type":"integer","description":"how many"}},"required":["action"]}""";
        var compact = GameTool.WithoutDescriptions(full);
        Assert.That(compact, Does.Contain("which one").And.Contain("how many"), "short descriptions stay");
        var longDesc = GameTool.WithoutDescriptions("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\",\"description\":\"" + string.Join(" ", Enumerable.Repeat("long", 40)) + "\"}}}");
        Assert.That(longDesc.Length, Is.LessThan(160), "long ones are cut");
        Assert.That(compact, Does.Contain("\"enum\":[\"a\",\"b\"]").And.Contain("\"required\":[\"action\"]").And.Contain("\"count\""));
        Assert.That(GameTool.WithoutDescriptions("not json"), Is.EqualTo("not json"));
    }
}
