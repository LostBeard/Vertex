using System.Text.Json;

namespace Vertex.Tests;

/// <summary>
/// The strict score for ToolChoiceTests (Tuvok's gate for the adapter work, 2026-11-04): the right tool AND the arguments the request implies. The loose check passes "what does that villager sell?" with invented arguments that would send him to trade emeralds for
/// wheat, and "buy some bread" with wheat offered in payment, so it has no room to see an adapter make arguments better or worse. Written down from what each tool does with its arguments (GameTools.cs), before seeing what any adapter does.
/// </summary>
public static class ToolChoiceScoring
{
    private static string Text(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()).Trim().ToLowerInvariant() : "";

    private static bool Has(JsonElement args, string name, string part) => Text(args, name).Contains(part, StringComparison.Ordinal);
    private static bool Empty(JsonElement args, string name) => Text(args, name).Length == 0;
    private static bool Number(JsonElement args, string name, int value) => int.TryParse(Text(args, name), out var n) && n == value;
    /// <summary>What he can pay with when nobody said: nothing named, or emeralds.</summary>
    private static bool NoInventedPayment(JsonElement a) => Empty(a, "give") || Has(a, "give", "emerald");

    /// <summary>The right tool with the right arguments? The case's request decides what its arguments must be. An unknown request is a mistake in the test, not a pass.</summary>
    public static bool Strict(string says, string wantedTool, string? gotTool, string? argsJson)
    {
        if (gotTool != wantedTool) return false;
        JsonElement a;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            a = doc.RootElement.Clone();
        }
        catch (JsonException) { return false; }
        return says switch
        {
            "Vertex, can you build me a castle?" => Has(a, "shape", "castle"),
            "Vertex, build a little cabin please" => Has(a, "shape", "house") || Has(a, "shape", "cabin"),
            "Vertex, make a platform to stand on" => Has(a, "shape", "platform"),
            "Vertex, keep building where you left off" => Has(a, "shape", "continue"),
            // Looking at the trades: the tool does that only with neither of these set.
            "Vertex, what does that villager sell?" => Empty(a, "want") && Empty(a, "give"),
            // All of it, for whatever he pays: nothing wanted in particular, or emeralds. A count of 0 or none means all.
            "Vertex, sell all my wheat to the villager" => Has(a, "give", "wheat") && (Empty(a, "want") || Has(a, "want", "emerald")) && (Empty(a, "count") || Number(a, "count", 0)),
            "Vertex, buy some bread from the villager" => Has(a, "want", "bread") && NoInventedPayment(a),
            "Vertex, make me a wheat farm" => Has(a, "action", "plant") && Has(a, "crop", "wheat"),
            "Vertex, harvest the crops" => Empty(a, "action") || Has(a, "action", "harvest"),
            "Vertex, breed the cows" => Has(a, "action", "breed") && Has(a, "animal", "cow"),
            "Vertex, shear the sheep" => Has(a, "action", "shear"),
            "Vertex, can you milk the cow?" => Has(a, "action", "milk"),
            "Vertex, stay right here and wait for me" => true,
            "Vertex, it is too dark, put some torches around" => true,
            "Vertex, shoot that zombie with your bow" => Has(a, "target", "zombie"),
            "Vertex, follow me" => Empty(a, "player") || Has(a, "player", "aubri") || Text(a, "player") == "me",
            "Vertex, get me ten wood" => (Has(a, "block", "wood") || Has(a, "block", "log")) && Number(a, "count", 10),
            "Vertex, let's go fishing!" => true,
            "Vertex, look around, what do you see?" => true,
            "Vertex, can you see any land from here?" => true,
            _ => throw new ArgumentException("no strict rule for: " + says),
        };
    }
}

public class ToolChoiceScoringTests
{
    [Test]
    public void TheStrictScore_FailsWhatTheLooseOnePassed()
    {
        // Each of these passed the loose check in the 20 of 20 run of 2026-11-04.
        Assert.That(ToolChoiceScoring.Strict("Vertex, what does that villager sell?", "trade", "trade", """{"want": "emeralds", "give": "wheat", "count": 0}"""), Is.False, "invented a trade");
        Assert.That(ToolChoiceScoring.Strict("Vertex, buy some bread from the villager", "trade", "trade", """{"want": "bread", "give": "wheat", "count": "0"}"""), Is.False, "invented the payment");
    }

    [Test]
    public void TheStrictScore_PassesTheRightArguments_AndTheWrongToolNever()
    {
        Assert.That(ToolChoiceScoring.Strict("Vertex, what does that villager sell?", "trade", "trade", "{}"), Is.True);
        Assert.That(ToolChoiceScoring.Strict("Vertex, sell all my wheat to the villager", "trade", "trade", """{"want": "", "give": "wheat", "count": "0"}"""), Is.True);
        Assert.That(ToolChoiceScoring.Strict("Vertex, get me ten wood", "collect", "collect", """{"block": "wood", "count": 10}"""), Is.True);
        Assert.That(ToolChoiceScoring.Strict("Vertex, get me ten wood", "collect", "collect", """{"block": "wood", "count": 5}"""), Is.False);
        Assert.That(ToolChoiceScoring.Strict("Vertex, follow me", "follow_player", "come_here", "{}"), Is.False);
        Assert.That(ToolChoiceScoring.Strict("Vertex, follow me", "follow_player", null, null), Is.False);
    }

    [Test]
    public void GarbledArguments_AreWrong_NotACrash() => Assert.That(ToolChoiceScoring.Strict("Vertex, stay right here and wait for me", "stay", "stay", "{not json"), Is.False);
}
