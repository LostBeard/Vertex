using System.Text.Json;
using SpawnDev.AI;
using SpawnDev.Minecraft;

namespace Vertex.Brain;

/// <summary>A tool the language model can call, backed by a delegate.</summary>
public sealed class GameTool(string name, string description, string schema, Func<JsonElement, Task<string>> run) : IAiTool
{
    public string Name => name;
    public string Description => description;
    public string ParametersJsonSchema => schema;

    public async Task<AiToolExecutionResult> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return new AiToolExecutionResult(await run(doc.RootElement));
        }
        catch (Exception e)
        {
            return new AiToolExecutionResult("error: " + e.Message) { IsError = true };
        }
    }
}

/// <summary>The tools Vertex gives its language model: everything it can do in the game.</summary>
public static class GameTools
{
    private static string? Str(JsonElement a, string p) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int Int(JsonElement a, string p, int fallback)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(p, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j) ? j : fallback;
    }

    private const string NoArgs = """{"type":"object","properties":{}}""";

    public static IEnumerable<IAiTool> Create(VertexBrain brain)
    {
        var skills = brain.Skills;
        var tasks = brain.Tasks;
        var client = brain.Client;

        // "me"/"here" in a request means whoever is talking.
        string? Who(JsonElement a) => Str(a, "player") is { Length: > 0 } p && p.ToLowerInvariant() is not ("me" or "you" or "here" or "i") ? p : brain.CurrentSpeaker;

        yield return new GameTool("follow_player",
            "Follow a player around (keeps going until told to stop or given another job). Use when asked to follow or come along.",
            """{"type":"object","properties":{"player":{"type":"string","description":"player name, or 'me' for the person talking"}}}""",
            a => { var p = Who(a); tasks.Start($"following {p}", ct => skills.FollowAsync(p, ct)); return Task.FromResult($"started following {p}"); });

        yield return new GameTool("come_here",
            "Walk over to a player (the person talking by default).",
            """{"type":"object","properties":{"player":{"type":"string"}}}""",
            a => { var p = Who(a); tasks.Start($"walking to {p}", ct => skills.GoToPlayerAsync(p, ct)); return Task.FromResult($"walking to {p}"); });

        yield return new GameTool("go_to",
            "Walk to coordinates x y z.",
            """{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"z":{"type":"integer"}},"required":["x","y","z"]}""",
            a =>
            {
                int x = Int(a, "x", 0), y = Int(a, "y", 64), z = Int(a, "z", 0);
                tasks.Start($"walking to {x} {y} {z}", ct => skills.GoToAsync(x, y, z, ct));
                return Task.FromResult($"walking to {x} {y} {z}");
            });

        yield return new GameTool("collect",
            "Mine or chop blocks and pick them up, e.g. wood, stone, dirt, sand, coal, iron, diamonds.",
            """{"type":"object","properties":{"block":{"type":"string","description":"what to collect, e.g. wood, stone, dirt, iron"},"count":{"type":"integer","description":"how many blocks"}},"required":["block"]}""",
            a =>
            {
                var what = Str(a, "block") ?? "wood";
                var count = Math.Clamp(Int(a, "count", 8), 1, 64);
                tasks.Start($"collecting {count} {what}", ct => skills.CollectAsync(what, count, ct));
                return Task.FromResult($"started collecting {count} {what}");
            });

        yield return new GameTool("give",
            "Give items from your inventory to a player (walks over and drops them).",
            """{"type":"object","properties":{"player":{"type":"string"},"item":{"type":"string"},"count":{"type":"integer"}},"required":["item"]}""",
            a =>
            {
                var p = Who(a);
                var item = Str(a, "item") ?? "";
                var count = Int(a, "count", 0);
                tasks.Start($"giving {item} to {p}", ct => skills.GiveAsync(p, item, count, ct));
                return Task.FromResult($"bringing {item} to {p}");
            });

        yield return new GameTool("attack",
            "Fight mobs: a type like zombie, skeleton, spider, or 'monsters' for any hostile mob nearby.",
            """{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}""",
            a =>
            {
                var t = Str(a, "target") ?? "monsters";
                tasks.Start($"fighting {t}", ct => skills.AttackAsync(t, ct));
                return Task.FromResult($"attacking {t}");
            });

        yield return new GameTool("eat", "Eat some food from your inventory.", NoArgs,
            async _ => (await skills.EatAsync(CancellationToken.None)).Item2);

        yield return new GameTool("stop", "Stop what you are doing and stand still.", NoArgs,
            _ => { tasks.Stop(); client.Controls.Clear(); return Task.FromResult("stopped"); });

        yield return new GameTool("look_at", "Turn to look at a player.",
            """{"type":"object","properties":{"player":{"type":"string"}}}""",
            a =>
            {
                var p = skills.FindPlayer(Who(a));
                if (p == null) return Task.FromResult("can't see them");
                client.LookAt(p.X, p.Y + 1.6, p.Z);
                return Task.FromResult($"looking at {p.PlayerName}");
            });

        yield return new GameTool("inventory", "List what you are carrying.", NoArgs,
            _ => Task.FromResult("I have: " + skills.InventorySummary()));

        yield return new GameTool("switch_character",
            "Become a different character (changes your name, personality and voice). Use when asked to be someone else.",
            """{"type":"object","properties":{"name":{"type":"string","description":"character name"}},"required":["name"]}""",
            a => Task.FromResult(brain.SwitchCharacter(Str(a, "name") ?? "")));

        yield return new GameTool("remember",
            "Remember an important fact for later (a player's favourite things, where something is).",
            """{"type":"object","properties":{"fact":{"type":"string"}},"required":["fact"]}""",
            a => Task.FromResult(brain.Remember(Str(a, "fact") ?? "")));
    }
}
