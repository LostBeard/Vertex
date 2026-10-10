using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Entities;
using SpawnDev.Minecraft.Items;
using SpawnDev.Minecraft.Pathing;

namespace Vertex.Brain;

/// <summary>
/// What Vertex can DO, composed from SpawnDev.Minecraft's actions: follow, go places, collect blocks, give items,
/// fight, eat. Each returns a short summary the language model reads. Names the players say ("wood", "Aubrey",
/// "zombies") are matched forgivingly: speech-to-text and kids' names are both fuzzy.
/// </summary>
public sealed class Skills(MinecraftClient client)
{
    private GameData D => client.World.Data;

    public static readonly HashSet<string> Hostile =
    [
        "minecraft:zombie", "minecraft:husk", "minecraft:drowned", "minecraft:zombie_villager", "minecraft:skeleton", "minecraft:stray",
        "minecraft:spider", "minecraft:cave_spider", "minecraft:creeper", "minecraft:witch", "minecraft:slime", "minecraft:phantom",
        "minecraft:pillager", "minecraft:vindicator", "minecraft:silverfish", "minecraft:magma_cube", "minecraft:blaze",
        "minecraft:wither_skeleton", "minecraft:piglin_brute", "minecraft:hoglin", "minecraft:zoglin", "minecraft:bogged",
        "minecraft:breeze", "minecraft:endermite", "minecraft:guardian", "minecraft:ravager", "minecraft:vex", "minecraft:evoker",
    ];

    // ---------------------------------------------------------------- finding things

    /// <summary>A player entity by (fuzzy) name, or the nearest player when the name is empty.</summary>
    public Entity? FindPlayer(string? name)
    {
        var players = client.Entities.Values.Where(e => e.IsPlayer && e.PlayerName != null && e.Uuid != client.Profile.Id).ToList();
        if (players.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(name)) return Nearest(players);
        var n = Simplify(name);
        return players.FirstOrDefault(p => Simplify(p.PlayerName!) == n)
            ?? players.FirstOrDefault(p => Simplify(p.PlayerName!).StartsWith(n) || n.StartsWith(Simplify(p.PlayerName!)))
            ?? players.Where(p => Distance(Simplify(p.PlayerName!), n) <= Math.Max(2, n.Length / 3)).OrderBy(p => Distance(Simplify(p.PlayerName!), n)).FirstOrDefault();
    }

    private Entity Nearest(IEnumerable<Entity> list)
    {
        var (x, y, z) = client.Position;
        return list.OrderBy(e => e.DistanceTo(x, y, z)).First();
    }

    private static string Simplify(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>Block types matching what a player called them ("wood", "oak logs", "diamonds", "stone").</summary>
    public HashSet<int> ResolveBlocks(string what)
    {
        var w = what.Trim().ToLowerInvariant().Replace(' ', '_');
        var ids = new HashSet<int>();
        void AddTag(string tag) { foreach (var n in D.BlockTag(tag)) if (D.Block(n) is { } b) ids.Add(b.Id); }
        void AddName(string n) { if (D.Block(n) is { } b) ids.Add(b.Id); }
        switch (w)
        {
            case "wood" or "woods" or "log" or "logs" or "tree" or "trees": AddTag("minecraft:logs"); return ids;
            case "diamond" or "diamonds": AddTag("minecraft:diamond_ores"); return ids;
            case "iron": AddTag("minecraft:iron_ores"); return ids;
            case "coal": AddTag("minecraft:coal_ores"); return ids;
            case "gold": AddTag("minecraft:gold_ores"); return ids;
            case "copper": AddTag("minecraft:copper_ores"); return ids;
            case "redstone": AddTag("minecraft:redstone_ores"); return ids;
            case "sand": AddName("sand"); AddName("red_sand"); return ids;
            case "stone" or "cobblestone" or "rock": AddName("stone"); AddName("cobblestone"); return ids;
            case "leaves": AddTag("minecraft:leaves"); return ids;
        }
        foreach (var candidate in new[] { w, w.TrimEnd('s'), w.EndsWith("es") ? w[..^2] : w })
        {
            AddName(candidate);
            AddName(candidate + "_ore");
            AddTag("minecraft:" + candidate);
            AddTag("minecraft:" + candidate + "s");
        }
        return ids;
    }

    /// <summary>An item in our inventory matching what a player called it.</summary>
    public ItemStack? FindItem(string what)
    {
        var w = Simplify(what.TrimEnd('s'));
        var stacks = client.Inventory.Slots.Skip(PlayerInventory.MainStart).Where(s => !s.IsEmpty).ToList();
        return stacks.FirstOrDefault(s => Simplify(s.Name.Replace("minecraft:", "")) == w)
            ?? stacks.FirstOrDefault(s => Simplify(s.Name.Replace("minecraft:", "")).Contains(w))
            ?? stacks.FirstOrDefault(s => s.CustomName != null && Simplify(s.CustomName).Contains(w));
    }

    public string InventorySummary()
    {
        var counts = client.Inventory.Slots.Skip(PlayerInventory.MainStart).Where(s => !s.IsEmpty)
            .GroupBy(s => s.Name.Replace("minecraft:", "").Replace('_', ' '))
            .Select(g => $"{g.Sum(s => s.Count)} {g.Key}");
        var list = string.Join(", ", counts);
        return list.Length == 0 ? "nothing" : list;
    }

    // ---------------------------------------------------------------- doing things

    public async Task<(bool, string)> FollowAsync(string? playerName, CancellationToken ct)
    {
        var target = FindPlayer(playerName);
        if (target == null) return (false, $"I can't see {playerName ?? "anyone"} nearby.");
        var name = target.PlayerName!;
        var lost = 0;
        while (!ct.IsCancellationRequested)
        {
            var t = client.Entities.Values.FirstOrDefault(e => e.Uuid == target.Uuid);
            if (t == null)
            {
                if (++lost > 100) return (false, $"I lost {name}.");
                await client.WaitTicksAsync(1, ct);
                continue;
            }
            lost = 0;
            var (x, y, z) = client.Position;
            if (t.DistanceTo(x, y, z) > 3.5)
            {
                // Short hops so we track a moving target: plan to near them, re-plan every second.
                using var hop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                hop.CancelAfter(TimeSpan.FromSeconds(1.5));
                try { await client.GoToAsync(Goal.Near(t.X, t.Y, t.Z, 2.5), new PathOptions { MaxNodes = 20_000 }, hop.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            else
            {
                client.LookAt(t.X, t.Y + 1.6, t.Z);
                await client.WaitTicksAsync(5, ct);
            }
        }
        return (true, $"stopped following {name}");
    }

    public async Task<(bool, string)> GoToPlayerAsync(string? playerName, CancellationToken ct)
    {
        var target = FindPlayer(playerName);
        if (target == null) return (false, $"I can't see {playerName ?? "anyone"} nearby.");
        var ok = await client.GoToAsync(Goal.Near(target.X, target.Y, target.Z, 2.5), null, ct);
        if (ok) client.LookAt(target.X, target.Y + 1.6, target.Z);
        return (ok, ok ? $"I'm next to {target.PlayerName}." : $"I couldn't find a way to {target.PlayerName}.");
    }

    public async Task<(bool, string)> GoToAsync(int x, int y, int z, CancellationToken ct)
    {
        var ok = await client.GoToAsync(Goal.Near(x + 0.5, y, z + 0.5, 1.5), new PathOptions { CanDig = false }, ct);
        return (ok, ok ? $"I'm at {x} {y} {z}." : $"I couldn't get to {x} {y} {z}.");
    }

    /// <summary>Finds, walks to, mines and picks up blocks until `count` were broken or none are left nearby.</summary>
    public async Task<(bool, string)> CollectAsync(string what, int count, CancellationToken ct)
    {
        var types = ResolveBlocks(what);
        if (types.Count == 0) return (false, $"I don't know what block '{what}' is.");
        var broken = 0;
        var failures = 0;
        var tried = new HashSet<(int, int, int)>();
        while (broken < count && failures < 6 && !ct.IsCancellationRequested)
        {
            var targets = client.World.FindBlocks(s => s > 0 && types.Contains(D.StateBlock[s]), client.Position, 48, 32)
                .Where(p => !tried.Contains(p)).ToList();
            if (targets.Count == 0) break;
            var (bx, by, bz) = targets[0];
            tried.Add((bx, by, bz));
            if (!await client.GoToAsync(new GoalReachBlock(bx, by, bz, 4.0), new PathOptions { CanDig = true, MaxNodes = 30_000 }, ct)) { failures++; continue; }
            var state = client.World.GetState(bx, by, bz);
            if (state <= 0 || !types.Contains(D.StateBlock[state])) continue;
            await client.EquipBestToolAsync(state, ct);
            if (!client.CanHarvest(state))
                return (false, $"I need a better tool to get {what} (it would just break with nothing dropping).");
            if (await client.DigAsync(bx, by, bz, ct))
            {
                broken++;
                await PickUpNearbyAsync(bx, by, bz, ct);
            }
            else failures++;
        }
        var have = InventorySummary();
        return broken > 0
            ? (broken >= count, $"I mined {broken} {what}{(broken < count ? $" (wanted {count}, couldn't find more nearby)" : "")}. I have: {have}.")
            : (false, $"I couldn't find any {what} I could reach nearby.");
    }

    /// <summary>Walks over the item drops near a spot.</summary>
    public async Task PickUpNearbyAsync(int x, int y, int z, CancellationToken ct)
    {
        await client.WaitTicksAsync(8, ct); // drops spawn and settle
        for (var tries = 0; tries < 4; tries++)
        {
            var drop = client.Entities.Values.Where(e => e.IsItem && e.DistanceTo(x + 0.5, y + 0.5, z + 0.5) < 5).OrderBy(e => e.DistanceTo(x + 0.5, y, z + 0.5)).FirstOrDefault();
            if (drop == null) return;
            await client.GoToAsync(Goal.Near(drop.X, drop.Y, drop.Z, 0.8), new PathOptions { MaxNodes = 5_000 }, ct);
            await client.WaitTicksAsync(3, ct);
        }
    }

    /// <summary>Walks to a player and drops items for them.</summary>
    public async Task<(bool, string)> GiveAsync(string? playerName, string item, int count, CancellationToken ct)
    {
        var stack = FindItem(item);
        if (stack == null) return (false, $"I don't have any {item}.");
        var target = FindPlayer(playerName);
        if (target == null) return (false, $"I can't see {playerName ?? "anyone"} to give it to.");
        await client.GoToAsync(Goal.Near(target.X, target.Y, target.Z, 2.0), null, ct);
        var name = stack.Name;
        var have = client.Inventory.Count(name);
        var give = Math.Min(count <= 0 ? have : count, have);
        if (!await client.EquipAsync(s => s.Name == name, ct)) return (false, "I couldn't get it into my hand.");
        client.LookAt(target.X, target.Y + 1.2, target.Z);
        await client.WaitTicksAsync(2, ct);
        var dropped = 0;
        while (dropped < give && !ct.IsCancellationRequested)
        {
            if (client.Inventory.MainHand.IsEmpty && !await client.EquipAsync(s => s.Name == name, ct)) break;
            var inHand = client.Inventory.MainHand.Count;
            if (give - dropped >= inHand) { await client.DropHeldAsync(true, ct); dropped += inHand; }
            else { await client.DropHeldAsync(false, ct); dropped++; }
            await client.WaitTicksAsync(2, ct);
        }
        return (true, $"I gave {target.PlayerName} {dropped} {name.Replace("minecraft:", "").Replace('_', ' ')}.");
    }

    /// <summary>Fights the nearest matching mob(s) until they are gone or we must back off.</summary>
    public async Task<(bool, string)> AttackAsync(string what, CancellationToken ct)
    {
        var w = what.Trim().ToLowerInvariant();
        bool Matches(Entity e) => w is "monster" or "monsters" or "mobs" or "hostile" or "enemies" or "bad guys"
            ? Hostile.Contains(e.Name)
            : e.Name.Replace("minecraft:", "") == w.TrimEnd('s') || e.Name.Replace("minecraft:", "") == w;
        var kills = 0;
        await EquipWeaponAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            if (client.Health < 6) return (false, $"I had to back off, I'm hurt (health {client.Health / 2:0} hearts).");
            var (x, y, z) = client.Position;
            var target = client.Entities.Values.Where(e => !e.IsPlayer && Matches(e) && e.DistanceTo(x, y, z) < 24).OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
            if (target == null) break;
            var id = target.Id;
            while (client.Entities.ContainsKey(id) && !ct.IsCancellationRequested)
            {
                var t = client.Entities[id];
                var d = t.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z);
                if (d > 2.8)
                {
                    using var hop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    hop.CancelAfter(TimeSpan.FromSeconds(1));
                    try { await client.GoToAsync(Goal.Near(t.X, t.Y, t.Z, 2.0), new PathOptions { MaxNodes = 8_000 }, hop.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    continue;
                }
                client.LookAt(t.X, t.Y + t.Type.Height * 0.5, t.Z);
                if (client.AttackReady) await client.AttackAsync(t, ct);
                await client.WaitTicksAsync(1, ct);
                if (client.Health < 6) break;
            }
            if (!client.Entities.ContainsKey(id)) kills++;
        }
        return (true, kills > 0 ? $"I defeated {kills} {what}." : $"I don't see any {what} around.");
    }

    public async Task EquipWeaponAsync(CancellationToken ct)
    {
        string[] order = ["netherite_sword", "diamond_sword", "iron_sword", "stone_sword", "golden_sword", "wooden_sword", "netherite_axe", "diamond_axe", "iron_axe", "stone_axe", "wooden_axe"];
        foreach (var w in order)
            if (client.Inventory.Count(w) > 0 && await client.EquipAsync(w, ct)) return;
    }

    private IEnumerable<ItemStack> EdibleStacks => client.Inventory.Slots.Skip(PlayerInventory.MainStart)
        .Where(s => s.Type is { IsFood: true } && !s.Name.Contains("rotten") && !s.Name.Contains("spider_eye") && !s.Name.Contains("poisonous") && !s.Name.Contains("pufferfish"));

    public bool HasFood => EdibleStacks.Any();

    /// <summary>Eats the most filling food we carry, if not full.</summary>
    public async Task<(bool, string)> EatAsync(CancellationToken ct)
    {
        if (client.Food >= 20) return (true, "I'm not hungry.");
        var food = EdibleStacks.OrderByDescending(s => s.Type!.FoodNutrition).FirstOrDefault();
        if (food == null) return (false, "I don't have any food.");
        var name = food.Name;
        if (!await client.EquipAsync(s => s.Name == name, ct)) return (false, "I couldn't get my food out.");
        var ok = await client.EatAsync(ct);
        return (ok, ok ? $"Yum, I ate {name.Replace("minecraft:", "").Replace('_', ' ')}." : "I couldn't eat right now.");
    }
}
