using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Crafting;
using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Entities;
using SpawnDev.Minecraft.Items;
using SpawnDev.Minecraft.Pathing;

namespace Vertex.Brain;

/// <summary>
/// Getting anything, from nothing, the way a player thinks about it: what is it made of, what is that made of, and where do I get the rest. The recipes come from the game's own data; what cannot be crafted comes from
/// where it is dug (with the pickaxe it needs, itself obtained if he has none) or from the animal that drops it. Every step reads what he carries now, so a job cut short by a monster or a death picks up from wherever
/// he stands. This is what replaces fixed scripts such as "the kit": asking for an iron pickaxe with empty pockets walks the whole chain.
/// </summary>
public sealed partial class Skills
{
    /// <summary>Items that are dug: which block to look for, and the pickaxe it takes (0 none, 1 wood, 2 stone, 3 iron).</summary>
    private static readonly Dictionary<string, (string Block, int Tier)> Dug = new()
    {
        ["cobblestone"] = ("stone", 1), ["coal"] = ("coal", 1), ["raw_iron"] = ("iron", 2), ["raw_copper"] = ("copper", 2), ["lapis_lazuli"] = ("lapis", 2),
        ["raw_gold"] = ("gold", 3), ["diamond"] = ("diamond", 3), ["redstone"] = ("redstone", 3), ["emerald"] = ("emerald", 3),
        ["sand"] = ("sand", 0), ["gravel"] = ("gravel", 0), ["flint"] = ("gravel", 0), ["dirt"] = ("dirt", 0), ["clay_ball"] = ("clay", 0),
    };

    /// <summary>Items that creatures drop.</summary>
    private static readonly Dictionary<string, string[]> Dropped = new()
    {
        ["beef"] = ["cow"], ["leather"] = ["cow"], ["porkchop"] = ["pig"], ["mutton"] = ["sheep"], ["white_wool"] = ["sheep"], ["chicken"] = ["chicken"], ["feather"] = ["chicken"], ["rabbit"] = ["rabbit"],
        // (Not arrows: they are made, from flint, a stick and a feather, four at a time; a skeleton that leaves some leaves them on top of that. Hunting skeletons for them is the most dangerous way there is.)
        ["string"] = ["spider"], ["bone"] = ["skeleton"], ["rotten_flesh"] = ["zombie"],
    };

    private const int MaxObtainDepth = 8;

    /// <summary>Gets `count` of an item in his pockets, doing whatever that takes. Says what it got, or the first thing in the chain that stopped it.</summary>
    public async Task<(bool, string)> ObtainAsync(string item, int count, CancellationToken ct)
    {
        var name = GameData.Normalize(item.Trim().ToLowerInvariant().Replace(' ', '_'));
        var bare = name.Bare();
        // What people call things: "wood" or "logs" is any log, a "bed" is any bed.
        if (bare is "wood" or "log" or "logs") return await WoodForAsync(count * 4, ct) is null ? (true, $"I have wood. I have: {InventorySummary()}.") : (false, "I couldn't get any wood.");
        if (bare == "bed") return await ObtainBedAsync(ct);
        var target = client.Inventory.Count(name) + Math.Max(1, count);
        var why = await ObtainStepAsync(name, target, [], ct);
        return why == null
            ? (true, $"I got {count} {Plain(name)}. I have: {InventorySummary()}.")
            : (false, $"I couldn't get {Plain(name)}: {why} I have: {InventorySummary()}.");
    }

    /// <summary>
    /// A bed, of any colour: three wool of one colour and three planks. Sheep come in colours and a sheep killed gives one wool, so he goes on until three match: asking for white only, he held light gray wool and looked for more white sheep (2026-11-04).
    /// </summary>
    private async Task<(bool, string)> ObtainBedAsync(CancellationToken ct)
    {
        if (client.Inventory.Find(s => s.Name.EndsWith("_bed")).Any()) return (true, "I have a bed.");
        var outOfReach = new HashSet<int>();
        for (var round = 0; round < 12; round++)
        {
            ct.ThrowIfCancellationRequested();
            var best = client.Inventory.Slots.Skip(PlayerInventory.MainStart).Where(s => !s.IsEmpty && s.Name.EndsWith("_wool")).GroupBy(s => s.Name).Select(g => (Name: g.Key, Count: g.Sum(s => s.Count))).OrderByDescending(g => g.Count).FirstOrDefault();
            if (best.Count >= 3)
            {
                var bed = best.Name.Replace("_wool", "_bed");
                var why = await ObtainStepAsync(bed, 1, [], ct);
                return why == null ? (true, $"I made a {Plain(bed)}. I have: {InventorySummary()}.") : (false, $"I couldn't make a bed: {why}");
            }
            Entity? Find() { var (x, y, z) = client.Position; return client.Entities.Values.Where(e => e.Name == "minecraft:sheep" && !IsBaby(e) && !outOfReach.Contains(e.Id)).OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault(); }
            var (_, legs, walked) = await LookOverLandAsync(() => Find() != null, 3, ct);
            if (Find() is not { } sheep) return (false, "I looked for sheep for a bed and found none" + Looked(legs, walked) + ".");
            Trace?.Invoke("hunting a sheep for its wool, for a bed");
            await CatchAsync(sheep, outOfReach, ct);
        }
        return (false, "I couldn't get three wool of one colour.");
    }

    /// <summary>Makes sure he holds `target` of the item. Null when he does, else why not.</summary>
    private async Task<string?> ObtainStepAsync(string item, int target, HashSet<string> making, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var have = client.Inventory.Count(item);
        if (have >= target) return null;
        if (making.Count >= MaxObtainDepth || !making.Add(item)) return $"{Plain(item)} goes round in circles.";
        try
        {
            var bare = item.Bare();
            var missing = target - have;
            if (bare.EndsWith("_log")) return await WoodForAsync(missing * 4, ct);
            if (Dug.TryGetValue(bare, out var dug)) return await DigForAsync(item, dug.Block, dug.Tier, target, making, ct);
            if (Dropped.TryGetValue(bare, out var kinds)) return await HuntForAsync(item, kinds, target, ct);
            Trace?.Invoke($"making {missing} {Plain(item)}");
            // A furnace first when it is made in one: iron ingots also have a crafting recipe, from iron blocks, which would send him looking for what he is trying to make.
            var book = RecipeBook.Vanilla;
            string? why = null;
            if (book.CookingRecipesFor(item).Any(r => r.Type == "minecraft:smelting"))
            {
                why = await SmeltForAsync(item, missing, making, ct);
                if (why == null) return null;
            }
            var recipes = book.RecipesFor(item);
            if (recipes.Count == 0) return why ?? $"I don't know how to make {Plain(item)}.";
            foreach (var recipe in recipes.Take(4))
            {
                if (recipe.Cells.Any(c => c != null && c.Items.Any(making.Contains))) continue;      // made of what he is in the middle of making
                var batches = (missing + recipe.ResultCount - 1) / recipe.ResultCount;
                var materials = await MaterialsForAsync(recipe, batches, making, ct);
                if (materials != null) { why ??= materials; continue; }
                var (made, message) = await client.CraftItemAsync(item, missing, ct);
                if (made) return null;
                // Short of a stick or a plank after all (a sub-craft ate what another part needed): one more log and once more.
                if (message.Contains("stick") || message.Contains("planks") || message.Contains("materials"))
                {
                    var logsBefore = Have(s => s.Name.EndsWith("_log"));
                    await CollectAsync("wood", 1, ct);
                    if (Have(s => s.Name.EndsWith("_log")) > logsBefore)
                    {
                        (made, message) = await client.CraftItemAsync(item, missing, ct);
                        if (made) return null;
                    }
                }
                why ??= message + ".";
            }
            return why ?? $"nothing I tried made {Plain(item)}.";
        }
        finally { making.Remove(item); }
    }

    private static bool Woody(Ingredient i) => i.Items.Any(n => n.EndsWith("_planks") || n == "minecraft:stick" || n.EndsWith("_log"));

    /// <summary>Everything a recipe needs, in his pockets. Wood is counted once for the whole recipe (a pickaxe's planks and sticks come from the same logs), and the table a large recipe needs is counted in.</summary>
    private async Task<string?> MaterialsForAsync(CraftingRecipe recipe, int batches, HashSet<string> making, CancellationToken ct)
    {
        // Getting one thing can cost another (blocks he stood on to reach a log were the cobblestone he had just mined): look again until it all holds, a few times at most.
        var needs = recipe.Cells.Where(c => c != null).GroupBy(c => c!).Select(g => (Ingredient: g.Key, Count: g.Count() * batches)).ToList();
        for (var pass = 0; ; pass++)
        {
            if (await GatherMaterialsAsync(recipe, needs, making, ct) is { } why) return why;
            if (pass >= 2 || needs.Where(n => !Woody(n.Ingredient)).All(n => n.Ingredient.Items.Sum(client.Inventory.Count) >= n.Count)) return null;
        }
    }

    private async Task<string?> GatherMaterialsAsync(CraftingRecipe recipe, List<(Ingredient Ingredient, int Count)> needs, HashSet<string> making, CancellationToken ct)
    {
        foreach (var (ingredient, count) in needs.Where(n => !Woody(n.Ingredient)))
        {
            var have = ingredient.Items.Sum(client.Inventory.Count);
            if (have >= count) continue;
            // What it can be: what he has some of first, then what the game makes easy (a table says where it comes from), then whatever else.
            string? why = null;
            var done = false;
            foreach (var variant in ingredient.Items.OrderByDescending(client.Inventory.Count).ThenByDescending(v => Dug.ContainsKey(v.Bare()) || Dropped.ContainsKey(v.Bare()) ? 1 : 0).Take(3))
            {
                var failed = await ObtainStepAsync(variant, client.Inventory.Count(variant) + (count - have), making, ct);
                if (failed == null) { done = true; break; }
                why ??= failed;      // the first is the real reason: it is what he would use; the rest are rarer stuff tried after it
            }
            if (!done) return why;
        }
        // The whole recipe's wood, in planks, against everything he carries that can become planks. Sticks come four from two planks, so they cost whole batches; and what is already sticks needs no planks.
        var planks = 0;
        foreach (var (ingredient, count) in needs.Where(n => Woody(n.Ingredient)))
        {
            if (ingredient.Items.Contains("minecraft:stick")) planks += (int)Math.Ceiling(Math.Max(0, count - client.Inventory.Count("stick")) / 4.0) * 2;
            else if (ingredient.Items.Any(i => i.EndsWith("_planks"))) planks += count;
            else planks += Math.Max(0, count - ingredient.Items.Sum(client.Inventory.Count)) * 4;
        }
        if (!recipe.FitsInInventory && client.Inventory.Count("crafting_table") == 0 && !await GoToStationAsync("crafting_table", ct)) planks += 4;
        return planks > 0 ? await WoodForAsync(planks, ct) : null;
    }

    /// <summary>A crafting table or furnace within reach, or within a walk: walks there. A player goes back to the one he just put down rather than cutting more wood for another.</summary>
    private async Task<bool> GoToStationAsync(string block, CancellationToken ct)
    {
        var id = D.Block(block)!.Id;
        bool Is(int s) => s > 0 && D.StateBlock[s] == id;
        if (client.World.FindBlocks(Is, client.EyePosition, 4, 1).Count > 0) return true;
        var near = client.World.FindBlocks(Is, client.EyePosition, 24, 1);
        if (near.Count == 0) return false;
        var (x, y, z) = near[0];
        return await client.GoToAsync(new SpawnDev.Minecraft.Pathing.GoalReachBlock(x, y, z, 3.5), new SpawnDev.Minecraft.Pathing.PathOptions { MaxNodes = 8_000 }, ct);
    }

    /// <summary>Logs for this many planks, counting what he carries. Null when he has enough.</summary>
    private async Task<string?> WoodForAsync(double planks, CancellationToken ct)
    {
        int Held() => Have(s => s.Name.EndsWith("_log")) * 4 + Have(s => s.Name.EndsWith("_planks"));
        // What he holds is what counts, not what he broke: a log can fall where he does not find it, and the craft then fails one plank short.
        var text = "";
        for (var round = 0; round < 3 && Held() < planks; round++)
        {
            var before = Held();
            (_, text) = await CollectAsync("wood", (int)Math.Ceiling((planks - before) / 4), ct);
            if (Held() <= before) break;
        }
        return Held() >= planks ? null : (text.Length > 0 ? text : "I couldn't get enough wood.");
    }

    private bool HasPickaxe(int tier) => tier <= 0 || client.Inventory.Find(s => s.Name.EndsWith("_pickaxe") && PickaxeTier(s.Name) >= tier).Any();

    private static int PickaxeTier(string name) => name.Bare() switch
    {
        "netherite_pickaxe" or "diamond_pickaxe" => 4, "iron_pickaxe" => 3, "stone_pickaxe" => 2, "wooden_pickaxe" or "golden_pickaxe" => 1, _ => 0,
    };

    private async Task<string?> DigForAsync(string item, string block, int tier, int target, HashSet<string> making, CancellationToken ct)
    {
        if (!HasPickaxe(tier))
        {
            var pick = tier switch { 1 => "minecraft:wooden_pickaxe", 2 => "minecraft:stone_pickaxe", _ => "minecraft:iron_pickaxe" };
            if (await ObtainStepAsync(pick, 1, making, ct) is { } noPick) return $"it takes a {Plain(pick)}: {noPick}";
        }
        // Rounds: a block may drop nothing (flint from gravel), several (coal with luck, later), or fall somewhere he misses.
        for (var round = 0; round < 3; round++)
        {
            var missing = target - client.Inventory.Count(item);
            if (missing <= 0) return null;
            Trace?.Invoke($"digging for {missing} {Plain(item)}");
            var (_, text) = await CollectAsync(block, missing, ct);
            if (client.Inventory.Count(item) >= target) return null;
            if (round == 2 || text.Contains("couldn't", StringComparison.OrdinalIgnoreCase) || text.Contains("haven't found", StringComparison.OrdinalIgnoreCase) || text.Contains("won't", StringComparison.OrdinalIgnoreCase)) return text;
        }
        return client.Inventory.Count(item) >= target ? null : $"I couldn't get enough {Plain(item)}.";
    }

    private async Task<string?> HuntForAsync(string item, string[] kinds, int target, CancellationToken ct)
    {
        var names = kinds.Select(k => "minecraft:" + k).ToHashSet();
        var outOfReach = new HashSet<int>();
        Entity? Find()
        {
            var (x, y, z) = client.Position;
            return client.Entities.Values.Where(e => names.Contains(e.Name) && !IsBaby(e) && !outOfReach.Contains(e.Id)).OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
        }
        for (var round = 0; round < 8; round++)
        {
            if (client.Inventory.Count(item) >= target) return null;
            await LookOverLandAsync(() => Find() != null, 3, ct);
            if (Find() is not { } animal) return $"I looked for a {kinds[0]} and found none.";
            Trace?.Invoke($"hunting a {kinds[0]} for {Plain(item)}");
            if (!await CatchAsync(animal, outOfReach, ct) && client.Health < 6) return "I'm too hurt to hunt right now.";
        }
        return client.Inventory.Count(item) >= target ? null : $"I couldn't get enough {Plain(item)} from {kinds[0]}s.";
    }

    private async Task<string?> SmeltForAsync(string item, int missing, HashSet<string> making, CancellationToken ct)
    {
        // What goes in: what he has some of, else what can be got (iron ingots have recipes from iron ore too, which breaking never gives: raw iron is what the ore drops).
        var book = RecipeBook.Vanilla;
        var inputs = book.CookingRecipesFor(item).Where(r => r.Type == "minecraft:smelting").SelectMany(r => r.Input.Items).Distinct()
            .Where(i => client.Inventory.Count(i) > 0 || Dug.ContainsKey(i.Bare()) || Dropped.ContainsKey(i.Bare()) || book.RecipesFor(i).Count > 0)
            .OrderByDescending(client.Inventory.Count).ThenByDescending(i => Dug.ContainsKey(i.Bare()) || Dropped.ContainsKey(i.Bare()) ? 1 : 0).ToList();
        if (inputs.Count == 0) return $"I don't know what to smelt into {Plain(item)}.";
        string? why = null;
        var ready = false;
        foreach (var input in inputs.Take(3))
        {
            var failed = await ObtainStepAsync(input, Math.Max(missing, 1), making, ct);
            if (failed == null) { ready = true; break; }
            why ??= failed;
        }
        if (!ready) return why;
        // A furnace, unless one is close or carried: eight cobblestone.
        var near = await GoToStationAsync("furnace", ct);
        if (!near && client.Inventory.Count("furnace") == 0 && await ObtainStepAsync("minecraft:furnace", 1, making, ct) is { } noFurnace) return $"it takes a furnace: {noFurnace}";
        // Fuel: coal if he has it (eight items each); a log does one and a half.
        var fuelNow = client.Inventory.Count("coal") * 8 + client.Inventory.Count("charcoal") * 8 + (Have(s => s.Name.EndsWith("_log")) + Have(s => s.Name.EndsWith("_planks"))) * 1.5;
        if (fuelNow < missing && await WoodForAsync(Math.Ceiling((missing - fuelNow) / 1.5) * 4, ct) is { } noFuel) return $"it needs fuel: {noFuel}";
        var (ok, message) = await client.SmeltAsync(item, missing, ct);
        return ok ? null : message + ".";
    }

    // ---------------------------------------------------------------- what to work toward (a player's progress through the game)

    /// <summary>One step in getting on in the world: done when `Done` says so.</summary>
    public sealed record Milestone(string Item, int Count, string Label, Func<bool> Done);

    private readonly Dictionary<string, (DateTime Until, int Failures)> _milestoneBackoff = [];

    /// <summary>Reached a step ("I have a stone pickaxe now"): for talking about it.</summary>
    public event Action<Milestone>? MilestoneReached;

    private bool HasAny(params string[] items) => items.Any(i => client.Inventory.Count(i) > 0);

    /// <summary>
    /// The order a player gets on in the game: tools before everything, then food, then a bed (which moves where he comes back to after a death, away from where the monsters are waiting), then iron, armor, diamond. Read off
    /// what he has right now: after a death he starts again at the top with no bookkeeping.
    /// </summary>
    public IEnumerable<Milestone> Milestones()
    {
        yield return new("minecraft:wooden_pickaxe", 1, "a wooden pickaxe", () => HasPickaxe(1));
        yield return new("minecraft:wooden_sword", 1, "a wooden sword", () => BestWeaponName != null);
        // Before anything else that a death would cost him: a bed is the one thing that stays, and without one every death puts him back where he started, however far that is.
        // His own, set as where he comes back to: a bed standing within 96 blocks counted as done, and at 11:47 on 2026-11-04 he went on to iron with his respawn still on the burning island, and lost it all at nightfall.
        yield return new("bed", 1, "a bed of my own", () => HomeBed != null);
        yield return new("food", 3, "some food", () => EdibleStacks.Sum(s => s.Count) >= 3);
        yield return new("minecraft:stone_pickaxe", 1, "a stone pickaxe", () => HasPickaxe(2));
        yield return new("minecraft:stone_sword", 1, "a stone sword", () => HasAny("stone_sword", "iron_sword", "diamond_sword", "netherite_sword"));
        // Most of what hits him comes from a distance (17 of 22 hits were arrows, in 17 minutes live, 2026-11-04). What it is worth, he finds out in DangerLearner.
        yield return new("minecraft:bow", 1, "a bow", () => HasAny("bow"));
        yield return new("minecraft:arrow", 8, "some arrows", () => HasAny("arrow"));
        yield return new("minecraft:iron_pickaxe", 1, "an iron pickaxe", () => HasPickaxe(3));
        yield return new("minecraft:iron_sword", 1, "an iron sword", () => HasAny("iron_sword", "diamond_sword", "netherite_sword"));
        yield return new("minecraft:iron_chestplate", 1, "an iron chestplate", () => HasAny("iron_chestplate", "diamond_chestplate", "netherite_chestplate") || WearingBetter("chestplate"));
        yield return new("minecraft:iron_helmet", 1, "an iron helmet", () => HasAny("iron_helmet", "diamond_helmet", "netherite_helmet") || WearingBetter("helmet"));
        yield return new("minecraft:iron_leggings", 1, "iron leggings", () => HasAny("iron_leggings", "diamond_leggings", "netherite_leggings") || WearingBetter("leggings"));
        yield return new("minecraft:iron_boots", 1, "iron boots", () => HasAny("iron_boots", "diamond_boots", "netherite_boots") || WearingBetter("boots"));
        yield return new("minecraft:shield", 1, "a shield", () => HasAny("shield") || client.Inventory.OffHandItem.Name == "minecraft:shield");
        yield return new("minecraft:diamond_pickaxe", 1, "a diamond pickaxe", () => HasPickaxe(4));
    }

    private bool WearingBetter(string piece) => client.Inventory.Slots.Take(PlayerInventory.MainStart).Any(s => !s.IsEmpty && s.Name.EndsWith("_" + piece) && !s.Name.Contains("leather") && !s.Name.Contains("golden"));

    /// <summary>The next step not yet reached and not given up on for now, or null when there is nothing left to do (or everything left has just failed).</summary>
    public Milestone? NextMilestone()
    {
        var now = DateTime.UtcNow;
        bool Open(Milestone m) => !m.Done() && (!_milestoneBackoff.TryGetValue(m.Item, out var b) || b.Until <= now);
        // Hurt and unable to heal (natural healing stops under 18 food) with nothing to eat: food before tools. He lost one stone pickaxe after another at 2 hearts and 16 food (2026-11-04): tools do not stay with someone who cannot heal.
        if (client.Health < 14 && client.Food < 18 && !HasFood && Milestones().FirstOrDefault(m => m.Item == "food") is { } food && Open(food)) return food;
        return Milestones().FirstOrDefault(Open);
    }

    /// <summary>The next step not yet reached, whether or not he is trying it just now: what he would say he is working toward.</summary>
    public Milestone? WorkingToward() => Milestones().FirstOrDefault(m => !m.Done());

    /// <summary>
    /// Works on one step. A failed step waits (1 minute, then 2, 4, up to 15, and at least three times what the try took) while he goes on to the next: failing to find iron is no reason to stand still, and the world round him changes as he moves. Time he was
    /// interrupted does not count as failing (he just goes back to it).
    /// </summary>
    public async Task<(bool, string)> AdvanceAsync(Milestone step, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        (bool Ok, string Text) result;
        if (step.Item == "food") result = await GetEnoughFoodAsync(step.Count, ct);
        // A bed close by will do as well as one he makes: lying down in it is what makes it where he comes back to.
        else if (step.Item == "bed" && client.FindBeds(32, 1) is { Count: > 0 } beds) result = await UseBedAsync(beds[0], ct);
        else result = await ObtainAsync(step.Item, step.Count, ct);
        if (result.Ok && step.Item == "bed" && HomeBed == null)
        {
            var (placed, text) = await SetUpBedAsync(ct);
            result = (placed, result.Text + " " + text);
        }
        if (step.Done() || result.Ok)
        {
            // Where things went right is worth coming back to (his bed, once he has one, stays the first choice).
            if (HomeBed == null && (HotAt?.Invoke() is not double hotAt || !KnownTooHot(BiomeHere, hotAt))) { var (hx, _, hz) = client.FeetBlock; Home = (hx, hz); }
            _milestoneBackoff.Remove(step.Item);
            MilestoneReached?.Invoke(step);
            return result;
        }
        var failures = _milestoneBackoff.TryGetValue(step.Item, out var b) ? b.Failures + 1 : 1;
        // At least three times as long as the try took: a search takes minutes, and with a fixed wait two failing searches took turns for ever and he never got to the stone tools he could have had (19 minutes live, 2026-11-04).
        var wait = TimeSpan.FromMinutes(Math.Min(15, 1 << Math.Min(failures - 1, 4)));
        var tookThrice = (DateTime.UtcNow - started) * 3;
        _milestoneBackoff[step.Item] = (DateTime.UtcNow + (tookThrice > wait ? tookThrice : wait), failures);
        Trace?.Invoke($"not reached yet, {step.Label}: {result.Text}");
        return result;
    }

    private static readonly HashSet<string> Fish = ["minecraft:cod", "minecraft:salmon", "minecraft:tropical_fish"];

    /// <summary>Food enough for a while: whatever meat is nearest. Hunted bare-handed if it comes to that.</summary>
    private async Task<(bool, string)> GetEnoughFoodAsync(int pieces, CancellationToken ct)
    {
        // Fish too: he spent 19 minutes hungry at sea looking for cows with cod and salmon all round him (2026-11-04).
        var names = new HashSet<string> { "minecraft:cow", "minecraft:pig", "minecraft:chicken", "minecraft:sheep", "minecraft:rabbit", "minecraft:cod", "minecraft:salmon", "minecraft:tropical_fish" };
        var out_of_reach = new HashSet<int>();
        for (var round = 0; round < 6 && EdibleStacks.Sum(s => s.Count) < pieces; round++)
        {
            // Fish only when they are close: they swim off, and 15 of 20 fish hunts came to nothing, minutes at a time out on the water among pufferfish, once with a full stomach (live 2026-11-05). Anything on land, as far as he can see.
            Entity? Find() { var (x, y, z) = client.Position; return client.Entities.Values.Where(e => names.Contains(e.Name) && !IsBaby(e) && !out_of_reach.Contains(e.Id) && (!Fish.Contains(e.Name) || e.DistanceTo(x, y, z) <= 16)).OrderBy(e => Fish.Contains(e.Name) ? 1 : 0).ThenBy(e => e.DistanceTo(x, y, z)).FirstOrDefault(); }
            var (_, legs, walked) = await LookOverLandAsync(() => Find() != null, 3, ct);
            if (Find() is not { } animal) return (false, "I looked for an animal to eat and found none" + Looked(legs, walked) + ".");
            if (!await CatchAsync(animal, out_of_reach, ct) && client.Health < 6) return (false, "I'm too hurt to hunt right now.");
        }
        return (EdibleStacks.Sum(s => s.Count) >= pieces, $"I have: {InventorySummary()}.");
    }

    /// <summary>
    /// Goes to a creature and kills it, and picks up what falls. Going there is a walk of its own: he sees spiders 80 blocks off and more, and the fight's own short steps never got there: 16 hunts live, 13 given up after 9 seconds where he stood, 3 "killed" that had only
    /// gone out of sight, no string, and so no bow (2026-11-04). One he cannot get to goes into `outOfReach`. True only when it died within his reach.
    /// </summary>
    private async Task<bool> CatchAsync(Entity target, HashSet<int> outOfReach, CancellationToken ct)
    {
        double Away() => target.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z);
        var from = Away();
        var started = DateTime.UtcNow;
        await EquipWeaponAsync(ct);
        // Within a dozen blocks the fight's own steps do it, and they swim after fish, which a walk to where a fish was does not.
        for (var hop = 0; hop < 8 && Away() > 12 && client.Entities.ContainsKey(target.Id); hop++)
        {
            var before = Away();
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(TimeSpan.FromSeconds(6));
            try { await client.GoToAsync(Goal.Near(target.X, target.Y, target.Z, 2.5), new PathOptions { MaxNodes = 15_000, Sprint = true }, step.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (Away() > before - 1) break;      // no nearer
        }
        var killed = false;
        if (!client.Entities.ContainsKey(target.Id) || Away() > 12) outOfReach.Add(target.Id);
        else
        {
            killed = await FightAsync(target.Id, null, ct) && Away() < 6;
            if (killed) await PickUpNearbyAsync((int)Math.Floor(target.X), (int)Math.Floor(target.Y), (int)Math.Floor(target.Z), ct);
            else outOfReach.Add(target.Id);
        }
        Trace?.Invoke($"hunted a {target.Name.Bare().Replace('_', ' ')} from {from:0} blocks: {(killed ? "killed it" : "did not")}, {(DateTime.UtcNow - started).TotalSeconds:0} s");
        return killed;
    }

    /// <summary>
    /// Puts the bed he carries down beside him and uses it, which makes it where he comes back to after a death (by day the game says he cannot sleep, but it still sets the respawn point). He comes back from the same
    /// spot every time otherwise, with whatever killed him last still standing around it (17 deaths in 9.4 minutes at the world's spawn, 2026-11-03).
    /// </summary>
    public async Task<(bool, string)> SetUpBedAsync(CancellationToken ct)
    {
        var (x, y, z) = client.FeetBlock;
        if (!await client.EquipAsync(s => s.Name.EndsWith("_bed"), ct)) return (false, "I have no bed to put down.");
        var placed = false;
        foreach (var (dx, dz) in new[] { (2, 0), (-2, 0), (0, 2), (0, -2), (2, 2), (-2, -2), (2, -2), (-2, 2), (3, 0), (-3, 0), (0, 3), (0, -3) })
        {
            // A bed is two long, along the way he faces: the spot and the next one must be clear, on solid ground.
            if (!Open(client.World.GetState(x + dx, y, z + dz)) || !Standable(client.World.GetState(x + dx, y - 1, z + dz))) continue;
            client.LookAt(x + dx + 0.5, y, z + dz + 0.5);
            await client.WaitTicksAsync(2, ct);
            if (await client.PlaceBlockAsync(x + dx, y, z + dz, ct) && client.FindBeds(6, 1).Count > 0) { placed = true; break; }
        }
        if (!placed) return (false, "I couldn't find a spot for the bed here.");
        return await UseBedAsync(client.FindBeds(6, 1)[0], ct);
    }

    /// <summary>Lies down in a bed, which is what makes it where he comes back to (by day the game says he cannot sleep, and still sets it), and remembers it.</summary>
    private async Task<(bool, string)> UseBedAsync((int X, int Y, int Z) bed, CancellationToken ct)
    {
        var (bx, by, bz) = bed;
        var set = false;
        void OnChat(ChatMessage m) { if (m.Text.Contains("Respawn point set", StringComparison.OrdinalIgnoreCase)) set = true; }
        client.ChatReceived += OnChat;
        string? refusal;
        try
        {
            refusal = await client.LieDownAsync(bx, by, bz, ct);
            await client.WaitTicksAsync(10, ct);
            if (client.Sleeping) { set = true; await client.LeaveBedAsync(ct); }
        }
        finally { client.ChatReceived -= OnChat; }
        // Only if the game took it: someone's bed, or one he could not reach, is not his home.
        if (!set && refusal != null && (refusal.Contains("could not get", StringComparison.OrdinalIgnoreCase) || refusal.Contains("occupied", StringComparison.OrdinalIgnoreCase))) return (false, $"I couldn't use the bed at {bx} {by} {bz}: {refusal}.");
        HomeBed = (bx, by, bz);
        Home = (bx, bz);
        return (true, set ? $"I set up my bed at {bx} {by} {bz}: this is where I come back to now." : $"I lay down in the bed at {bx} {by} {bz}.");
    }

    // ---------------------------------------------------------------- his things, where he died

    private bool _hadTools;
    private (int X, int Y, int Z, DateTime At)? _dropped;

    /// <summary>What he carries, looked at while he is alive: by the time the death packet comes the inventory may already be empty.</summary>
    public void NoteKit() { if (client.Health > 0) _hadTools = HasPickaxe(1); }

    /// <summary>He died here. His things lie here for five minutes, if he had any worth the walk.</summary>
    public void NoteDiedHere(double x, double y, double z)
    {
        _dropped = _hadTools ? ((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), DateTime.UtcNow) : null;
        _hadTools = false;
    }

    /// <summary>Do his things still lie where he died? Items last five minutes in the game; the walk takes time and the last half minute is no use.</summary>
    public bool ThingsToFetch => _dropped is { } d && DateTime.UtcNow - d.At < TimeSpan.FromMinutes(4.5);

    /// <summary>
    /// Back to where he died for what he dropped, as a player does: dying cost him the tools of 11 minutes' work, lying about 310 blocks away from where he came back, and he started again from a tree (live 2026-11-04). Dying on the way costs nothing, he
    /// has nothing on him. Over when he has been: restarted after an interruption, it carries on.
    /// </summary>
    public async Task<(bool, string)> FetchThingsAsync(CancellationToken ct)
    {
        if (_dropped is not { } d) return (false, "I have nothing to go back for.");
        var stuck = 0;
        while (ThingsToFetch)
        {
            ct.ThrowIfCancellationRequested();
            var (fx, _, fz) = client.Position;
            if (Math.Sqrt((fx - d.X) * (fx - d.X) + (fz - d.Z) * (fz - d.Z)) <= 24) break;
            Trace?.Invoke($"going back for my things at {d.X} {d.Y} {d.Z}");
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(TimeSpan.FromSeconds(8));
            try { await client.GoToAsync(Goal.XZ(d.X, d.Z), new PathOptions { MaxNodes = 15_000, Sprint = true }, step.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            var (ax, _, az) = client.Position;
            if (Math.Sqrt((ax - fx) * (ax - fx) + (az - fz) * (az - fz)) >= 2) { stuck = 0; continue; }
            if (++stuck >= 4) { _dropped = null; return (false, $"I couldn't get back to where I died, at {d.X} {d.Y} {d.Z}."); }
            await client.WaitTicksAsync(20, ct);
        }
        // Near enough to see them. They may be down in a cave, so he may dig.
        var given = new HashSet<int>();
        var been = false;
        for (var tries = 0; tries < 16 && ThingsToFetch; tries++)
        {
            ct.ThrowIfCancellationRequested();
            var (px, py, pz) = client.Position;
            var drop = client.Entities.Values.Where(e => e.IsItem && !given.Contains(e.Id) && e.DistanceTo(d.X + 0.5, d.Y + 0.5, d.Z + 0.5) < 20).OrderBy(e => e.DistanceTo(px, py, pz)).FirstOrDefault();
            if (drop == null && been) break;
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(TimeSpan.FromSeconds(15));
            var options = new PathOptions { MaxNodes = 8_000, CanDig = true };
            var reached = false;
            try { reached = await client.GoToAsync(drop != null ? Goal.Near(drop.X, drop.Y, drop.Z, 0.8) : Goal.Near(d.X + 0.5, d.Y, d.Z + 0.5, 3), options, step.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (drop == null) been = true;
            else if (!reached) given.Add(drop.Id);
            await client.WaitTicksAsync(4, ct);
        }
        _dropped = null;
        return HasPickaxe(1) ? (true, $"I went back to where I died and picked up my things. I have: {InventorySummary()}.") : (false, "I went back to where I died and my tools weren't there any more.");
    }

    /// <summary>Where he last made his bed the place he comes back to, or null. Kept by the brain across restarts.</summary>
    public (int X, int Y, int Z)? HomeBed { get; set; }

    /// <summary>He came back after a death: if not at his bed, the bed is gone or blocked (the game says so by putting him elsewhere), and getting one is on the list again.</summary>
    public void NoteRespawned()
    {
        if (HomeBed is not { } bed) return;
        var (x, _, z) = client.FeetBlock;
        if (Math.Abs(x - bed.X) > 16 || Math.Abs(z - bed.Z) > 16)
        {
            Trace?.Invoke($"I came back at {x} {z}, not at my bed at {bed.X} {bed.Z}: it must be gone");
            HomeBed = null;
        }
    }
}
