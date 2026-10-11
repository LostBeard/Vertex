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

    /// <summary>
    /// Called with every execution. AiChatEngine runs tools inside its own loop and reports only calls it did NOT
    /// execute in AiChatResult.ToolCalls, so the tool itself is where "what did Vertex do" is visible.
    /// </summary>
    public Action<string>? OnRun { get; set; }

    public async Task<AiToolExecutionResult> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var result = await run(doc.RootElement);
            OnRun?.Invoke($"tool {name}({argumentsJson}) -> {result}");
            return new AiToolExecutionResult(result);
        }
        catch (Exception e)
        {
            OnRun?.Invoke($"tool {name}({argumentsJson}) failed: {e.Message}");
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
            a => { var p = Who(a); tasks.Start($"following {p}", ct => skills.FollowAsync(p, ct)); return Task.FromResult($"OK: I started following {p}. I keep following until told to stop."); });

        yield return new GameTool("come_here",
            "Walk over to a player (the person talking by default).",
            """{"type":"object","properties":{"player":{"type":"string"}}}""",
            a => { var p = Who(a); tasks.Start($"walking to {p}", ct => skills.GoToPlayerAsync(p, ct)); return Task.FromResult($"OK: I started walking over to {p}. It takes a few seconds - I am NOT there yet."); });

        yield return new GameTool("go_to",
            "Walk to coordinates x y z.",
            """{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"z":{"type":"integer"}},"required":["x","y","z"]}""",
            a =>
            {
                int x = Int(a, "x", 0), y = Int(a, "y", 64), z = Int(a, "z", 0);
                tasks.Start($"walking to {x} {y} {z}", ct => skills.GoToAsync(x, y, z, ct));
                return Task.FromResult($"OK: I started walking to {x} {y} {z}. I am not there yet.");
            });

        yield return new GameTool("collect",
            "Mine or chop blocks and pick them up, e.g. wood, stone, dirt, sand, coal, iron, diamonds.",
            """{"type":"object","properties":{"block":{"type":"string","description":"what to collect, e.g. wood, stone, dirt, iron"},"count":{"type":"integer","description":"how many blocks"}},"required":["block"]}""",
            a =>
            {
                var what = Str(a, "block") ?? "wood";
                var count = Math.Clamp(Int(a, "count", 8), 1, 64);
                tasks.Start($"collecting {count} {what}", ct => skills.CollectAsync(what, count, ct));
                return Task.FromResult($"OK: I started collecting {count} {what}. It takes a while; I will say when I am done.");
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
                return Task.FromResult($"OK: I am bringing {item} to {p} now.");
            });

        yield return new GameTool("attack",
            "Fight mobs: a type like zombie, skeleton, spider, or 'monsters' for any hostile mob nearby.",
            """{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}""",
            a =>
            {
                var t = Str(a, "target") ?? "monsters";
                tasks.Start($"fighting {t}", ct => skills.AttackAsync(t, ct));
                return Task.FromResult($"OK: I am going after the {t} now.");
            });

        yield return new GameTool("craft",
            "Craft items (tools, torches, planks, a crafting table...). Makes missing parts on the way when you have the raw materials.",
            """{"type":"object","properties":{"item":{"type":"string","description":"what to craft, e.g. wooden_pickaxe, torch, oak_planks"},"count":{"type":"integer"}},"required":["item"]}""",
            a =>
            {
                var item = (Str(a, "item") ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
                var count = Math.Clamp(Int(a, "count", 1), 1, 64);
                tasks.Start($"crafting {count} {item.Replace('_', ' ')}", async ct =>
                {
                    var (ok, message) = await client.CraftItemAsync(item, count, ct);
                    return (ok, message + ". I have: " + skills.InventorySummary());
                });
                return Task.FromResult($"OK: I started crafting {count} {item.Replace('_', ' ')}.");
            });

        yield return new GameTool("guard",
            "Protect a player: stay with them and fight any monster that comes near them, until told to stop.",
            """{"type":"object","properties":{"player":{"type":"string","description":"player name, or 'me' for the person talking"}}}""",
            a => { var p = Who(a); tasks.Start($"guarding {p}", ct => skills.GuardAsync(p, ct)); return Task.FromResult($"OK: I started guarding {p}. I stay with them and fight monsters that come near, until told to stop."); });

        yield return new GameTool("store_items",
            "Put items into the nearest chest or barrel. One kind of item, or 'everything' (keeps tools, weapons, armor, food and torches).",
            """{"type":"object","properties":{"item":{"type":"string","description":"what to store, e.g. cobblestone, dirt, or everything"}}}""",
            a =>
            {
                var what = Str(a, "item") ?? "everything";
                tasks.Start($"storing {what}", ct => skills.StoreAsync(what, ct));
                return Task.FromResult($"OK: I started putting {what} in the chest. It is NOT done yet.");
            });

        yield return new GameTool("take_items",
            "Get items out of the nearest chests or barrels.",
            """{"type":"object","properties":{"item":{"type":"string","description":"what to take, e.g. iron_ingot, bread"},"count":{"type":"integer"}},"required":["item"]}""",
            a =>
            {
                var what = Str(a, "item") ?? "";
                var count = Math.Clamp(Int(a, "count", 64), 1, 64 * 36);
                tasks.Start($"getting {what} from the chests", ct => skills.TakeAsync(what, count, ct));
                return Task.FromResult($"OK: I started looking in the chests for {what}. It is NOT done yet.");
            });

        yield return new GameTool("sleep",
            "Sleep in the nearest bed (only works at night or in a thunderstorm). The night is skipped when everyone online sleeps.",
            NoArgs,
            _ =>
            {
                tasks.Start("sleeping", ct => skills.SleepAsync(ct));
                return Task.FromResult("OK: I am going to bed.");
            });

        yield return new GameTool("build_house",
            "Build a small house near the person talking (walls, roof, windows if you have glass, a door and a torch if you have them). Needs about 70 blocks of one kind (planks, cobblestone...); 7x7 with about 115.",
            """{"type":"object","properties":{"material":{"type":"string","description":"what to build with, e.g. planks, cobblestone; empty = what you have most of"},"player":{"type":"string","description":"who to build it near; 'me' = the person talking"}}}""",
            a =>
            {
                var p = Who(a);
                var material = Str(a, "material");
                tasks.Start("building a house", ct => skills.BuildHouseAsync(p, material, ct));
                return Task.FromResult("OK: I started building a house. It takes a minute; I am NOT done yet.");
            });

        yield return new GameTool("build_wall",
            "Build a wall: a straight line going a direction from the person talking, or all the way around them (a pen for animals, a fence for a base) with a gateway.",
            """{"type":"object","properties":{"shape":{"type":"string","enum":["line","around"],"description":"line = straight; around = a square around the player"},"direction":{"type":"string","description":"for a line: north, south, east, west, or empty = the way the player faces"},"length":{"type":"integer","description":"line length in blocks (default 10), or for around the distance from the player to the wall (default 4)"},"height":{"type":"integer","description":"blocks high (default 2)"},"material":{"type":"string","description":"what to build with; empty = what you have most of"},"player":{"type":"string","description":"'me' = the person talking"}}}""",
            a =>
            {
                var p = Who(a);
                var around = Str(a, "shape")?.Trim().ToLowerInvariant() is "around" or "ring" or "square" or "pen";
                var length = Int(a, "length", around ? 4 : 10);
                var height = Int(a, "height", 2);
                var direction = Str(a, "direction");
                var material = Str(a, "material");
                tasks.Start(around ? "building a wall around " + p : "building a wall", ct => skills.BuildWallAsync(p, direction, length, height, around, material, ct));
                return Task.FromResult("OK: I started building the wall. I am NOT done yet.");
            });

        yield return new GameTool("build_tower",
            "Build a 3x3 lookout tower with a ladder up the outside, near the person talking. Needs about 9 blocks per block of height, plus ladders (made from sticks if needed).",
            """{"type":"object","properties":{"height":{"type":"integer","description":"blocks high, 4 to 16 (default 8)"},"material":{"type":"string","description":"what to build with; empty = what you have most of"},"player":{"type":"string","description":"'me' = the person talking"}}}""",
            a =>
            {
                var p = Who(a);
                var height = Int(a, "height", 8);
                var material = Str(a, "material");
                tasks.Start("building a tower", ct => skills.BuildTowerAsync(p, height, material, ct));
                return Task.FromResult("OK: I started building a tower. It takes a minute; I am NOT done yet.");
            });

        yield return new GameTool("build_bridge",
            "Build a bridge across water, lava or a ravine, going a direction from the person talking, to the ground on the other side.",
            """{"type":"object","properties":{"direction":{"type":"string","description":"north, south, east, west, or empty = the way the player faces"},"material":{"type":"string","description":"what to build with; empty = what you have most of"},"player":{"type":"string","description":"'me' = the person talking"}}}""",
            a =>
            {
                var p = Who(a);
                var direction = Str(a, "direction");
                var material = Str(a, "material");
                tasks.Start("building a bridge", ct => skills.BuildBridgeAsync(p, direction, material, ct));
                return Task.FromResult("OK: I started building a bridge. I am NOT done yet.");
            });

        yield return new GameTool("explore",
            "Go exploring in a direction and come back to tell what you found (villages, animals, ores, chests).",
            """{"type":"object","properties":{"direction":{"type":"string","description":"north, south, east, west, or empty for any"},"distance":{"type":"integer","description":"how far, in blocks (default 100)"}}}""",
            a =>
            {
                var dir = Str(a, "direction");
                var distance = Math.Clamp(Int(a, "distance", 100), 16, 400);
                tasks.Start($"exploring {dir ?? "around"}", ct => skills.ExploreAsync(dir, distance, brain.Memory, ct));
                return Task.FromResult($"OK: I set off exploring {(string.IsNullOrEmpty(dir) ? "" : dir + " ")}about {distance} blocks. I will come back and tell you what I find.");
            });

        yield return new GameTool("farm",
            "Harvest the ripe crops nearby (wheat, carrots, potatoes, beetroot) and replant them.",
            NoArgs,
            _ =>
            {
                tasks.Start("farming", ct => skills.FarmAsync(ct));
                return Task.FromResult("OK: I started harvesting the ripe crops. I will replant as I go.");
            });

        yield return new GameTool("smelt",
            "Smelt or cook in a furnace: iron_ingot from raw iron, glass from sand, cooked_beef from beef, charcoal from logs. Builds a furnace from cobblestone if none is near. Takes 10 seconds per item.",
            """{"type":"object","properties":{"result":{"type":"string","description":"what to make, e.g. iron_ingot, glass, cooked_porkchop"},"count":{"type":"integer"}},"required":["result"]}""",
            a =>
            {
                var item = (Str(a, "result") ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
                var count = Math.Clamp(Int(a, "count", 1), 1, 64);
                tasks.Start($"smelting {count} {item.Replace('_', ' ')}", async ct =>
                {
                    var (ok, message) = await client.SmeltAsync(item, count, ct);
                    return (ok, message + ". I have: " + skills.InventorySummary());
                });
                return Task.FromResult($"OK: I started smelting {count} {item.Replace('_', ' ')}. It is NOT done yet.");
            });

        yield return new GameTool("eat", "Eat some food from your inventory.", NoArgs,
            async _ => (await skills.EatAsync(CancellationToken.None)).Item2);

        yield return new GameTool("stop", "Stop what you are doing and stand still.", NoArgs,
            _ => { tasks.Stop(); client.Controls.Clear(); return Task.FromResult("OK: I stopped and I am standing still."); });

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

        // "my house" is the speaker's house: TJ's and Aubri's are different places.
        string Owned(string name) =>
            name.Trim() is var n && n.StartsWith("my ", StringComparison.OrdinalIgnoreCase) && brain.CurrentSpeaker is { } who ? $"{who}'s {n[3..].Trim()}" : name.Trim();

        yield return new GameTool("mark_place",
            "Remember a place by name (home, the mine, the farm) so you can go back later. Marks where the person talking is standing, or where you are.",
            """{"type":"object","properties":{"name":{"type":"string","description":"what to call the place"},"where":{"type":"string","description":"'me' = where the person talking stands (default), 'you' = where you are"}},"required":["name"]}""",
            a =>
            {
                var name = Owned(Str(a, "name") ?? "");
                if (name.Length == 0) return Task.FromResult("I need a name for the place.");
                var mine = (Str(a, "where") ?? "me").Trim().ToLowerInvariant() is "you" or "yourself" or "vertex" or "your spot";
                var speaker = mine ? null : skills.FindPlayer(brain.CurrentSpeaker);
                var (px, py, pz) = speaker != null ? (speaker.X, speaker.Y, speaker.Z) : client.Position;
                var place = brain.Memory.SetPlace(name, client.World.Dimension, (int)Math.Floor(px), (int)Math.Floor(py + 1e-6), (int)Math.Floor(pz), brain.CurrentSpeaker);
                return Task.FromResult($"OK: I will remember {place.Name} at {place.X} {place.Y} {place.Z}.");
            });

        yield return new GameTool("go_to_place",
            "Walk to a place you remember by name (see 'Places you know').",
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
            a =>
            {
                var name = Owned(Str(a, "name") ?? "");
                var place = brain.Memory.FindPlace(name);
                if (place == null) return Task.FromResult($"I don't know a place called {name}. Places I know: {(brain.Memory.Places.Count == 0 ? "none yet" : string.Join(", ", brain.Memory.Places.Select(p => p.Name)))}.");
                if (place.Dimension != client.World.Dimension) return Task.FromResult($"{place.Name} is in another dimension; I can't get there from here.");
                tasks.Start($"going to {place.Name}", ct => skills.TravelAsync(place.X, place.Y, place.Z, place.Name, ct));
                return Task.FromResult($"OK: I started walking to {place.Name}. I am NOT there yet.");
            });

        yield return new GameTool("forget_place",
            "Forget a place you remembered.",
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
            a => Task.FromResult(brain.Memory.ForgetPlace(Owned(Str(a, "name") ?? "")) ? "OK: forgotten." : "I don't know that place."));

        yield return new GameTool("remember",
            "Remember an important fact for later. Name who it is about, e.g. \"Aubs loves axolotls\" or \"TJ is building a castle\".",
            """{"type":"object","properties":{"fact":{"type":"string"}},"required":["fact"]}""",
            a => Task.FromResult(brain.Remember(Str(a, "fact") ?? "")));
    }
}
