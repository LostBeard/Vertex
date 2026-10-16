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
            JsonValueKind.String => Clean(v.GetString()),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static readonly System.Text.RegularExpressions.Regex FormattingCode = new("§.?", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A tool argument as the model meant it. An 8B model playing Minecraft decorates words like chat: seen on
    /// 2026-10-11 "me" between U+00A0 spaces, "'me'" in quotes, and "§7me§f" in color codes. Removes formatting
    /// codes, then Unicode spaces and quotes around the value.
    /// </summary>
    public static string? Clean(string? value)
    {
        if (value == null) return null;
        var v = FormattingCode.Replace(value, "").Trim().Trim('\'', '"').Trim();
        // A template echoed back ("<player name: Aubri>") or a bracketed value ("[Aubri]", "<Aubri>").
        if (TemplateValue.Match(v) is { Success: true } m) v = m.Groups[1].Value.Trim();
        else if (v.Length > 2 && (v[0], v[^1]) is ('<', '>') or ('[', ']') or ('{', '}') or ('(', ')')) v = v[1..^1].Trim();
        return v;
    }

    private static readonly System.Text.RegularExpressions.Regex TemplateValue = new(@"^<[^<>:]*:\s*([^<>]+)>$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
        string? Speaker(string? name)
        {
            if (Clean(name) is not { Length: > 0 } n || n.ToLowerInvariant() is "me" or "you" or "here" or "i") return brain.CurrentSpeaker;
            // A player on the server named inside a decorated argument ("/type/player/Aubri", "<player name: Aubri>")
            // is that player: more decorations turn up than any cleaning can list.
            var words = System.Text.RegularExpressions.Regex.Split(n, @"[^\p{L}\p{N}_]+");
            return client.Players.Values.Select(p => p.Name).FirstOrDefault(p => words.Contains(p, StringComparer.OrdinalIgnoreCase)) ?? n;
        }
        string? Who(JsonElement a) => Speaker(Str(a, "player"));

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
            "Give items to a player (walks over and drops them). If you have none, you go and get it first when it is a block like wood, stone or iron ('bring me ten wood').",
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

        yield return new GameTool("learn_blocks",
            "Study the strange blocks around here that you don't know (from mods): walk up to each kind and feel its shape, so you can walk on, around or through them properly. Breaks nothing.",
            NoArgs,
            a =>
            {
                tasks.Start("studying blocks I don't know", ct => skills.StudyBlocksAsync(16, ct));
                return Task.FromResult("OK: I started studying the blocks I don't know. I am NOT done yet.");
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

        yield return new GameTool("build_shape",
            "Build near the player: a house (cabin), platform, pyramid, castle or a fenced pen for animals. shape 'continue' (and nothing else) when asked to keep building / carry on / finish the last build. Uses the wood or stone we carry most of unless a material is named, and cuts logs or mines cobblestone first when short of it.",
            """{"type":"object","properties":{"shape":{"type":"string","description":"house, platform, pyramid, castle, pen (a fenced pen for animals), or continue; with the material in front if one was asked for, e.g. 'stone castle'"}},"required":["shape"]}""",
            a =>
            {
                var p = Who(a);
                var shape = Str(a, "shape") ?? "house";
                if (shape.Trim().ToLowerInvariant() is "continue" or "resume" or "keep building" or "carry on")
                {
                    tasks.Start("carrying on with the build", ct => skills.ResumeBuildAsync(ct));
                    return Task.FromResult("OK: I started carrying on with the last build.");
                }
                int size = Int(a, "size", 0);
                var material = Str(a, "material");
                // One argument is all the schema asks for (an 8B model garbled the optional ones: a trailing comma made the call unreadable), so the material comes in the same words
                // as the shape: "stone castle" is the shape castle in stone.
                var words = shape.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
                var kindWord = words.LastOrDefault(w => BuildShapes.Normalize(w) != null);
                if (kindWord != null && words.Length > 1)
                {
                    material ??= string.Join(" ", words.Where(w => !ReferenceEquals(w, kindWord) && w.ToLowerInvariant() is not ("a" or "an" or "the" or "little" or "small" or "big" or "large" or "nice" or "new")));
                    if (string.IsNullOrWhiteSpace(material)) material = null;
                    shape = kindWord;
                }
                tasks.Start($"building a {shape}", ct => skills.BuildShapeAsync(shape, size, 0, 0, material, null, null, p, ct));
                return Task.FromResult($"OK: I started building a {shape}. It takes a while; I will say when I am done.");
            });

        if (brain.AdvancedBuildTools)
            yield return new GameTool("build_script",
                "Build something you design yourself. plan = lines, coordinates relative to the corner (x east, y up, z south). " + BuildScript.Grammar + " Walls up to 4 high and roofs no higher than y 4 are most reliable.",
                """{"type":"object","properties":{"plan":{"type":"string","description":"one command per line"},"player":{"type":"string","description":"near this player, or 'me'"}},"required":["plan"]}""",
                a =>
                {
                    var p = Who(a);
                    var plan = Str(a, "plan") ?? "";
                    tasks.Start("building from a plan", ct => skills.BuildScriptAsync(plan, p, ct));
                    return Task.FromResult("OK: I started building from the plan. It takes a while; I will say when I am done.");
                });

        yield return new GameTool("trade",
            "Talk to the nearest villager or trader. With no arguments: look at what it sells or offers (use this for 'what does the villager sell?'). 'want' = what to get (emerald, bread), 'give' = what to hand over (wheat); either or both.",
            """{"type":"object","properties":{"want":{"type":"string","description":"what to get, e.g. emerald, bread"},"give":{"type":"string","description":"what to hand over, e.g. wheat"},"count":{"type":"integer","description":"how many (0 = sell all)"}}}""",
            a =>
            {
                var want = Str(a, "want");
                var give = Str(a, "give");
                if (string.IsNullOrWhiteSpace(want) && string.IsNullOrWhiteSpace(give))
                {
                    tasks.Start("looking at a villager's trades", ct => skills.VillagerOffersAsync(ct));
                    return Task.FromResult("OK: I started walking over to the nearest villager to see its trades. I will say what they are.");
                }
                var count = Math.Clamp(Int(a, "count", 0), 0, 640);
                tasks.Start($"trading with a villager ({give ?? "?"} for {want ?? "?"})", ct => skills.TradeWithVillagerAsync(want, give, want != null && count <= 0 ? 1 : count, ct));
                return Task.FromResult("OK: I started walking over to the nearest villager to trade. It takes a few seconds - I have NOT traded yet.");
            });

        yield return new GameTool("farm",
            "Farming. action 'harvest' (default): pick the ripe crops nearby and replant. action 'plant': make a new field (till, water, sow) from seeds, carrots or potatoes we carry.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["harvest","plant"]},"crop":{"type":"string","description":"for plant: wheat, carrots, potatoes or beetroot"},"size":{"type":"integer","description":"for plant: side of the field, 3 to 9"}}}""",
            a =>
            {
                if ((Str(a, "action") ?? "harvest").Trim().ToLowerInvariant() is "plant" or "sow" or "new")
                {
                    var p = Who(a);
                    var crop = Str(a, "crop");
                    var size = Int(a, "size", 5);
                    tasks.Start($"planting a field of {crop ?? "crops"}", ct => skills.PlantFieldAsync(crop, size, p, ct));
                    return Task.FromResult($"OK: I started planting a field of {crop ?? "crops"}. It takes a while; I will say when I am done.");
                }
                tasks.Start("farming", ct => skills.FarmAsync(ct));
                return Task.FromResult("OK: I started harvesting the ripe crops. I will replant as I go.");
            });

        yield return new GameTool("animals",
            "Milk a cow, shear a sheep, or breed farm animals. action 'milk' (a cow; needs a bucket), 'shear' (a sheep; needs shears), 'breed' (feeds two adults their food so a baby is born; animal = cow, sheep, pig, chicken...).",
            """{"type":"object","properties":{"action":{"type":"string","enum":["breed","shear","milk"]},"animal":{"type":"string","description":"for breed: cow, sheep, pig, chicken, rabbit or goat"}},"required":["action"]}""",
            a =>
            {
                var action = (Str(a, "action") ?? "").Trim().ToLowerInvariant();
                switch (action)
                {
                    case "shear":
                        tasks.Start("shearing sheep", ct => skills.ShearSheepAsync(0, ct));
                        return Task.FromResult("OK: I started shearing the sheep nearby.");
                    case "milk":
                        tasks.Start("milking a cow", ct => skills.MilkCowAsync(ct));
                        return Task.FromResult("OK: I started walking over to a cow to milk it.");
                    default:
                        var animal = Str(a, "animal") ?? "cow";
                        tasks.Start($"breeding {animal}", ct => skills.BreedAnimalsAsync(animal, 1, ct));
                        return Task.FromResult($"OK: I started breeding {animal}. It takes a little while; I will say when a baby is born.");
                }
            });

        yield return new GameTool("fish",
            "Go fishing: walk to open water, cast the fishing rod, wait for a bite and reel the catch in (needs a fishing rod). count = how many (default 1).",
            """{"type":"object","properties":{"count":{"type":"integer","description":"how many to catch (default 1)"}}}""",
            a =>
            {
                var count = Math.Clamp(Int(a, "count", 1), 1, 20);
                tasks.Start("fishing", ct => skills.FishAsync(count, ct));
                return Task.FromResult("OK: I started fishing. It takes a while to get a bite; I will say what I caught.");
            });

        yield return new GameTool("light_up",
            "Put torches on the ground around the player (or here) so monsters cannot spawn in the dark. Makes torches from coal and sticks if needed. Use for 'light this up', 'put some torches around', 'it's too dark'.",
            """{"type":"object","properties":{"radius":{"type":"integer","description":"how far around, 4 to 24 blocks (default 10)"},"player":{"type":"string","description":"around this player, or 'me'"}}}""",
            a =>
            {
                var p = Who(a);
                var radius = Int(a, "radius", 10);
                tasks.Start("lighting up the area", ct => skills.LightUpAsync(radius, p, ct));
                return Task.FromResult("OK: I started putting torches around. It takes a little while; I will say when I am done.");
            });

        yield return new GameTool("shoot",
            "Shoot mobs with a bow from a distance (needs a bow and arrows). Use for 'shoot the skeleton', 'use your bow', or when a monster is far away and you have a bow.",
            """{"type":"object","properties":{"target":{"type":"string","description":"what to shoot, e.g. zombie, skeleton, monsters"}},"required":["target"]}""",
            a =>
            {
                var target = Str(a, "target") ?? "monsters";
                tasks.Start($"shooting {target}", ct => skills.ShootAsync(target, ct));
                return Task.FromResult($"OK: I started shooting {target} with my bow.");
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

        yield return new GameTool("stay", "Stay right here and wait: do not wander off or start any job on your own until you are asked to do something. Use for 'stay', 'wait here', 'don't move', 'stay put'.", NoArgs,
            _ => { tasks.Stop(); client.Controls.Clear(); brain.Staying = true; return Task.FromResult("OK: I am staying right here until you ask me to do something."); });

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
            "Remember an important fact for later, e.g. \"Aubs loves axolotls\" or \"TJ is building a castle\". If it is about a player, say who in 'about'.",
            """{"type":"object","properties":{"fact":{"type":"string"},"about":{"type":"string","description":"the player it is about ('me' = the person talking); empty for facts about the world"}},"required":["fact"]}""",
            a => Task.FromResult(brain.Remember(Str(a, "fact") ?? "", Str(a, "about") is { Length: > 0 } about ? Speaker(about) : null)));

        yield return new GameTool("set_goal",
            "Take on a long-term goal that needs many steps over time (collect 64 diamonds, find a village, explore for a stronghold). NOT for building: build_shape builds right away. You work on it by yourself whenever nobody needs you, and remember it between games.",
            """{"type":"object","properties":{"goal":{"type":"string","description":"the goal, e.g. 'find a village'"},"player":{"type":"string","description":"who it is for ('me' = the person talking)"}},"required":["goal"]}""",
            a => Task.FromResult(brain.SetGoal(Str(a, "goal") ?? "", Who(a))));

        yield return new GameTool("finish_goal",
            "Mark a long-term goal as done (or no longer wanted).",
            """{"type":"object","properties":{"goal":{"type":"string","description":"which goal; empty if there is only one"}}}""",
            a => Task.FromResult(brain.FinishGoal(Str(a, "goal"))));
    }
}
