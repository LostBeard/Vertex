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
    /// <summary>
    /// Raw states of modded blocks Vertex has no data for but learned the name of, matching what was asked for
    /// ("candy cane block" finds mcwholidays:blue_candy_cane_block): every word of the request in the name.
    /// </summary>
    public HashSet<int> LearnedStatesNamed(string what)
    {
        var words = what.Trim().ToLowerInvariant().Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.TrimEnd('s')).ToArray();
        if (words.Length == 0) return [];
        return client.LearnedStates.Where(kv => kv.Value.Name is { } n && words.All(w => n[(n.IndexOf(':') + 1)..].Contains(w)))
            .Select(kv => kv.Key).ToHashSet();
    }

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
    public ItemStack? FindItem(string what) => MatchStack(client.Inventory.Slots.Skip(PlayerInventory.MainStart), what);

    /// <summary>The stack a player meant by a name: exact item name, then a name containing it, then a custom name.</summary>
    public static ItemStack? MatchStack(IEnumerable<ItemStack> slots, string what)
    {
        var w = Simplify(what.TrimEnd('s'));
        var stacks = slots.Where(s => !s.IsEmpty).ToList();
        return stacks.FirstOrDefault(s => Simplify(s.Name.Bare()) == w)
            ?? stacks.FirstOrDefault(s => Simplify(s.Name.Bare()).Contains(w))
            ?? stacks.FirstOrDefault(s => s.CustomName != null && Simplify(s.CustomName).Contains(w));
    }

    public string InventorySummary()
    {
        var counts = client.Inventory.Slots.Skip(PlayerInventory.MainStart).Where(s => !s.IsEmpty)
            .GroupBy(s => s.Name.Bare().Replace('_', ' '))
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

    /// <summary>
    /// Walks somewhere far away: plans hop after hop as the chunks on the way arrive, until there or no longer
    /// getting closer. Never breaks builds on the way (only natural blocks, and only when there is no other way).
    /// </summary>
    public async Task<(bool, string)> TravelAsync(int x, int y, int z, string label, CancellationToken ct)
    {
        double Dist() { var (px, py, pz) = client.Position; return Math.Sqrt((x + 0.5 - px) * (x + 0.5 - px) + (y - py) * (y - py) + (z + 0.5 - pz) * (z + 0.5 - pz)); }
        var best = Dist();
        var stalls = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await client.GoToAsync(Goal.Near(x + 0.5, y, z + 0.5, 2), new PathOptions { CanDig = true, MaxNodes = 40_000 }, ct))
                return (true, $"I'm at {label}.");
            var d = Dist();
            if (d < best - 4) { best = d; stalls = 0; }
            else if (++stalls >= 3) return (false, $"I got stuck {d:0} blocks from {label}.");
        }
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
        // A modded block we have no data for, but learned the name of (placed one, or broke one and saw it drop).
        var learned = LearnedStatesNamed(what);
        if (types.Count == 0 && learned.Count == 0) return (false, $"I don't know what block '{what}' is.");
        bool Wanted(int s) => s > 0 && (types.Contains(D.StateBlock[s]) || learned.Contains(s));
        var broken = 0;
        var failures = 0;
        var tried = new HashSet<(int, int, int)>();
        var nextToLava = 0;
        while (broken < count && failures < 6 && !ct.IsCancellationRequested)
        {
            var found = client.World.FindBlocks(Wanted, client.Position, 48, 32).Where(p => !tried.Contains(p)).ToList();
            // Opening a block with lava beside it (or lava above it, which falls in) lets the lava out onto whoever is digging: leave it (found 2026-10-15: a diamond ringed with lava took 11 of 20 health).
            var targets = found.Where(p => !LavaAround(p.Item1, p.Item2, p.Item3)).ToList();
            nextToLava += found.Count - targets.Count;
            foreach (var skipped in found.Except(targets)) tried.Add(skipped);
            if (targets.Count == 0) break;
            var (bx, by, bz) = targets[0];
            tried.Add((bx, by, bz));
            if (!await client.GoToAsync(new GoalReachBlock(bx, by, bz, 4.0), new PathOptions { CanDig = true, MaxNodes = 30_000 }, ct)) { failures++; continue; }
            var state = client.World.GetState(bx, by, bz);
            if (!Wanted(state)) continue;
            bool ok;
            if (D.IsUnknownBlock(state))
                // No hardness to time the dig by: the server times it (and we learn how long it takes).
                ok = await client.LearnByBreakingAsync(bx, by, bz, ct) != null;
            else
            {
                await client.EquipBestToolAsync(state, ct);
                if (!client.CanHarvest(state))
                    return (false, $"I need a better tool to get {what} (it would just break with nothing dropping).");
                ok = await client.DigAsync(bx, by, bz, ct);
            }
            if (ok)
            {
                broken++;
                await PickUpNearbyAsync(bx, by, bz, ct, mayDig: true);
            }
            else failures++;
        }
        var have = InventorySummary();
        var lavaNote = nextToLava > 0 ? $" {nextToLava} {(nextToLava == 1 ? "was" : "were")} next to lava, so I left {(nextToLava == 1 ? "it" : "them")} alone." : "";
        return broken > 0
            ? (broken >= count, $"I mined {broken} {what}{(broken < count ? $" (wanted {count}, couldn't find more nearby)" : "")}. I have: {have}.{lavaNote}")
            : (false, nextToLava > 0 ? $"I won't mine that {what}: it is next to lava, which would pour out onto me." : $"I couldn't find any {what} I could reach nearby.");
    }

    /// <summary>Lava beside a block, below it, or above it (two up: it flows down into the hole once the block is gone).</summary>
    private bool LavaAround(int x, int y, int z)
    {
        bool Lava(int dx, int dy, int dz) => client.World.GetState(x + dx, y + dy, z + dz) is var s && s > 0 && (D.StateFlags[s] & GameData.FlagLava) != 0;
        return Lava(1, 0, 0) || Lava(-1, 0, 0) || Lava(0, 0, 1) || Lava(0, 0, -1) || Lava(0, -1, 0) || Lava(0, 1, 0) || Lava(0, 2, 0);
    }

    /// <summary>
    /// Walks over the item drops near a spot. Digging to a drop is only allowed when the caller mined something that can leave one sealed in a pocket (ore) and the drop is within two blocks
    /// of the spot: with digging always on, a field's water hole made Vertex dig a shaft after the dirt it had just dropped (found 2026-10-15 on the AubsCraft dev world).
    /// </summary>
    public async Task PickUpNearbyAsync(int x, int y, int z, CancellationToken ct, bool mayDig = false, Func<Entity, bool>? leave = null)
    {
        await client.WaitTicksAsync(8, ct); // drops spawn and settle
        for (var tries = 0; tries < 4; tries++)
        {
            var drop = client.Entities.Values.Where(e => e.IsItem && e.DistanceTo(x + 0.5, y + 0.5, z + 0.5) < 5 && (leave == null || !leave(e))).OrderBy(e => e.DistanceTo(x + 0.5, y, z + 0.5)).FirstOrDefault();
            if (drop == null) return;
            // Digging allowed: a drop from ore reached from a few blocks away lies in the sealed pocket the ore left (found 2026-10-15: a diamond mined from above was never picked up).
            await client.GoToAsync(Goal.Near(drop.X, drop.Y, drop.Z, 0.8), new PathOptions { MaxNodes = 5_000, CanDig = mayDig && drop.DistanceTo(x + 0.5, y + 0.5, z + 0.5) < 2.5 }, ct);
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
        return (true, $"I gave {target.PlayerName} {dropped} {name.Bare().Replace('_', ' ')}.");
    }

    /// <summary>Fights the nearest matching mob(s) until they are gone or we must back off.</summary>
    public async Task<(bool, string)> AttackAsync(string what, CancellationToken ct)
    {
        var w = what.Trim().ToLowerInvariant();
        bool Matches(Entity e) => w is "monster" or "monsters" or "mobs" or "hostile" or "enemies" or "bad guys"
            ? Hostile.Contains(e.Name)
            : e.Name.Bare() == w.TrimEnd('s') || e.Name.Bare() == w;
        var kills = 0;
        await EquipWeaponAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            if (client.Health < 6) return (false, $"I had to back off, I'm hurt (health {client.Health / 2:0} hearts).");
            var (x, y, z) = client.Position;
            var target = client.Entities.Values.Where(e => !e.IsPlayer && Matches(e) && e.DistanceTo(x, y, z) < 24).OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
            if (target == null) break;
            if (await FightAsync(target.Id, null, ct)) kills++;
        }
        return (true, kills > 0 ? $"I defeated {kills} {what}." : $"I don't see any {what} around.");
    }

    /// <summary>
    /// Fights one mob until it is gone (true), we are hurt, or `keepGoing` says to stop chasing it (false).
    /// </summary>
    private async Task<bool> FightAsync(int id, Func<Entity, bool>? keepGoing, CancellationToken ct)
    {
        while (client.Entities.TryGetValue(id, out var t) && !ct.IsCancellationRequested)
        {
            if (client.Health < 6 || (keepGoing != null && !keepGoing(t))) return false;
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
            // Jump as the charge is about to complete and hit on the way down: a critical hit (x1.5) at no cost
            // in attack rate. Not on creepers (stay mobile) or when up close to several mobs.
            if (client.OnGround && client.TicksUntilAttackReady <= 7 && t.Name != "minecraft:creeper" && d < 2.6)
                await client.CritAttackAsync(t, ct);
            else if (client.AttackReady) await client.AttackAsync(t, ct);
            await client.WaitTicksAsync(1, ct);
        }
        return !client.Entities.ContainsKey(id);
    }

    /// <summary>
    /// Stays with a player and fights every monster that comes within 10 blocks of them (never chasing one more
    /// than 16 blocks from them), until stopped. Backs off from fights while hurt but keeps following.
    /// </summary>
    public async Task<(bool, string)> GuardAsync(string? playerName, CancellationToken ct)
    {
        var target = FindPlayer(playerName);
        if (target == null) return (false, $"I can't see {playerName ?? "anyone"} to protect.");
        var name = target.PlayerName!;
        var uuid = target.Uuid;
        Entity? Ward() => client.Entities.Values.FirstOrDefault(e => e.Uuid == uuid);
        var kills = 0;
        var lost = 0;
        await EquipWeaponAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            var p = Ward();
            if (p == null)
            {
                if (++lost > 100) return (false, $"I lost {name}. I defeated {kills} monsters while guarding.");
                await client.WaitTicksAsync(1, ct);
                continue;
            }
            lost = 0;
            var threat = client.Health < 6 ? null : client.Entities.Values
                .Where(e => !e.IsPlayer && Hostile.Contains(e.Name) && e.DistanceTo(p.X, p.Y, p.Z) < 10)
                .OrderBy(e => e.DistanceTo(p.X, p.Y, p.Z)).FirstOrDefault();
            if (threat != null)
            {
                if (await FightAsync(threat.Id, e => Ward() is { } w && e.DistanceTo(w.X, w.Y, w.Z) < 16, ct)) kills++;
                continue;
            }
            var (x, y, z) = client.Position;
            if (p.DistanceTo(x, y, z) > 4)
            {
                using var hop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                hop.CancelAfter(TimeSpan.FromSeconds(1));
                try { await client.GoToAsync(Goal.Near(p.X, p.Y, p.Z, 3), new PathOptions { MaxNodes = 20_000 }, hop.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            else await client.WaitTicksAsync(2, ct); // watch: threats are checked every few ticks
        }
        return (true, $"stopped guarding {name} ({kills} monsters defeated)");
    }

    // ---------------------------------------------------------------- chests and beds

    private static readonly string[] KeepSuffixes =
        ["_sword", "_pickaxe", "_axe", "_shovel", "_hoe", "_helmet", "_chestplate", "_leggings", "_boots"];

    /// <summary>What "store everything" leaves in our pockets: tools, weapons, armor, food and torches.</summary>
    private static bool KeepWhenStoring(ItemStack s) =>
        KeepSuffixes.Any(s.Name.EndsWith) || s.Type is { IsFood: true }
        || s.Name is "minecraft:shield" or "minecraft:bow" or "minecraft:crossbow" or "minecraft:arrow" or "minecraft:torch";

    private static bool MeansEverything(string what) =>
        what.Trim().ToLowerInvariant() is "" or "everything" or "all" or "stuff" or "items" or "all items" or "my stuff" or "inventory";

    /// <summary>Puts items into the nearest chests or barrels: one kind, or everything we don't need to carry.</summary>
    public async Task<(bool, string)> StoreAsync(string what, CancellationToken ct)
    {
        Func<ItemStack, bool> match;
        if (MeansEverything(what)) match = s => !KeepWhenStoring(s);
        else
        {
            var stack = FindItem(what);
            if (stack == null) return (false, $"I don't have any {what}.");
            var name = stack.Name;
            match = s => s.Name == name;
        }
        if (!client.Inventory.Find(match).Any()) return (true, "I have nothing to store (I keep my tools, armor, food and torches).");
        var chests = client.FindStorage();
        if (chests.Count == 0) return (false, "I don't see a chest or barrel nearby.");
        var stored = 0;
        foreach (var (x, y, z) in chests.Take(4))
        {
            if (!await client.OpenContainerAsync(x, y, z, ct)) continue;
            stored += await client.StoreAsync(match, ct);
            await client.CloseWindowAsync(ct);
            if (!client.Inventory.Find(match).Any()) break;
        }
        var left = client.Inventory.Find(match).Sum(i => client.Inventory[i].Count);
        return stored == 0 ? (false, "I couldn't put anything in the chests (full, or I couldn't reach them).")
            : (left == 0, $"I stored {stored} items{(left > 0 ? $"; {left} didn't fit" : "")}. I have: {InventorySummary()}.");
    }

    /// <summary>Looks through the nearest chests and barrels for an item and takes about `count` of it.</summary>
    public async Task<(bool, string)> TakeAsync(string what, int count, CancellationToken ct)
    {
        var chests = client.FindStorage();
        if (chests.Count == 0) return (false, "I don't see a chest or barrel nearby.");
        var taken = 0;
        string? name = null;
        foreach (var (x, y, z) in chests.Take(6))
        {
            if (!await client.OpenContainerAsync(x, y, z, ct)) continue;
            var window = client.OpenWindow!;
            name ??= MatchStack(window.Slots.Take(window.ContainerSlotCount), what)?.Name;
            if (name != null) taken += await client.TakeAsync(s => s.Name == name, count - taken, ct);
            await client.CloseWindowAsync(ct);
            if (taken >= count) break;
        }
        var pretty = (name ?? what).Bare().Replace('_', ' ');
        return taken > 0 ? (true, $"I took {taken} {pretty} from the chests. I have: {InventorySummary()}.")
            : (false, $"I couldn't find any {pretty} in the chests nearby.");
    }

    /// <summary>Sleeps in the nearest free bed until morning (the night passes when everyone online sleeps).</summary>
    public async Task<(bool, string)> SleepAsync(CancellationToken ct)
    {
        var beds = client.FindBeds();
        if (beds.Count == 0) return (false, "I don't see a bed nearby.");
        string? why = null;
        foreach (var (x, y, z) in beds.Take(4))
        {
            why = await client.LieDownAsync(x, y, z, ct);
            if (why == null) break;
            if (!why.Contains("occupied", StringComparison.OrdinalIgnoreCase)) return (false, why);
        }
        if (why != null) return (false, why);
        try
        {
            while (client.Sleeping) await client.WaitTicksAsync(5, ct);
        }
        catch (OperationCanceledException)
        {
            await client.LeaveBedAsync(CancellationToken.None);
            throw;
        }
        // The server wakes everyone at dawn but sends the clock only once a second.
        var morning = await client.WaitUntilAsync(() => client.World.DayTime % 24000 < 1000, 25, ct);
        return (morning, morning ? "I slept. Good morning!" : "I had to get out of bed (it's still night).");
    }

    // ---------------------------------------------------------------- building

    /// <summary>
    /// Builds a small house near a player (or here): picks the material we have most of, the bigger size (7) when
    /// there is enough for it, a flat clear spot, the door facing the player, then walks out through the door.
    /// </summary>
    public async Task<(bool, string)> BuildHouseAsync(string? nearPlayer, string? material, CancellationToken ct)
    {
        if (ChooseMaterial(material) is not var (_, wall, pretty, noun)) return (false, $"I can't build a house out of {material}.");
        var glass = Have(Blueprints.IsGlass) >= 3;
        var size = Have(wall) >= Blueprints.HouseBlocks(7, glass) ? 7 : 5;
        var need = Blueprints.HouseBlocks(size, glass);
        if (Have(wall) < need)
            return (false, $"I need {need} {noun} to build a house and I only have {Have(wall)}. (Wood works: one log makes 4 planks.)");

        var player = FindPlayer(nearPlayer);
        var (ox, oy, oz) = player != null ? (player.X, player.Y, player.Z) : client.Position;
        if (FindHouseSite(ox, oy, oz, size, player) is not { } site) return (false, "I can't find a flat, clear spot for a house around here.");
        var (cx, y, cz) = site;
        // The door faces whoever asked (or us), so they can walk right in.
        double fx = ox - (cx + 0.5), fz = oz - (cz + 0.5);
        var doorSide = Math.Abs(fx) > Math.Abs(fz) ? (fx > 0 ? BlockFace.East : BlockFace.West) : (fz > 0 ? BlockFace.South : BlockFace.North);
        var plan = Blueprints.House(cx, y, cz, size, doorSide, wall, glass, Have(s => s.Name == "minecraft:torch") > 0, Have(Blueprints.IsDoor) > 0);

        if (!await client.GoToAsync(Goal.Block(cx, y, cz), new PathOptions { MaxNodes = 20_000 }, ct))
            return (false, "I couldn't get to the spot I picked for the house.");
        var (placed, failed, missing) = await client.BuildAsync(plan, ct);
        // Out through the doorway (the door opens and closes behind us).
        var (dx, dz) = Blueprints.Dir(doorSide);
        var r = size / 2;
        await client.GoToAsync(Goal.Block(cx + dx * (r + 1), y, cz + dz * (r + 1)), new PathOptions { MaxNodes = 5_000 }, ct);
        if (missing != null) return (false, $"I ran out of {missing} while building the house ({placed} blocks placed).");
        var side = doorSide.ToString().ToLowerInvariant();
        return failed == 0
            ? (true, $"I built a {size} by {size} {pretty} house! The door is on the {side} side.")
            : (true, $"I built the house, but {failed} {(failed == 1 ? "block" : "blocks")} didn't go where I wanted. The door is on the {side} side.");
    }

    /// <summary>How many of the matching items we carry (main inventory and hotbar).</summary>
    private int Have(Func<ItemStack, bool> match) =>
        client.Inventory.Slots.Skip(PlayerInventory.MainStart).Where(s => !s.IsEmpty && match(s)).Sum(s => s.Count);

    /// <summary>
    /// The building material: what was asked for ("cobblestone", "wood"), else the kind we carry most of. Null when
    /// the asked-for material is not one we build with. Noun is how to count it ("planks", "cobblestone").
    /// </summary>
    private (string Name, Func<ItemStack, bool> Match, string Pretty, string Noun)? ChooseMaterial(string? material)
    {
        string? chosen = null;
        if (!string.IsNullOrWhiteSpace(material))
        {
            var m = material.Trim().ToLowerInvariant().Replace(' ', '_').TrimEnd('s');
            if (m is "wood" or "wooden" or "plank") m = "planks";
            chosen = Blueprints.HouseMaterials.FirstOrDefault(x => x.Contains(m) || m.Contains(x.Bare()));
            if (chosen == null) return null;
        }
        chosen ??= Blueprints.HouseMaterials.OrderByDescending(x => Have(Blueprints.MaterialMatch(x))).First();
        var pretty = chosen == "planks" ? "wooden" : chosen.Bare().Replace('_', ' ');
        return (chosen, Blueprints.MaterialMatch(chosen), pretty, chosen == "planks" ? "planks" : pretty);
    }

    /// <summary>
    /// The nearest spot (within 14 blocks) for a size x size house: solid full ground under every cell at one level,
    /// four blocks of air (or grass and flowers) above it, and nobody standing there.
    /// </summary>
    public (int X, int Y, int Z)? FindHouseSite(double nearX, double nearY, double nearZ, int size, Entity? keepClear) =>
        FindFlatSite(nearX, nearY, nearZ, size, 4, keepClear);

    /// <summary>Air, or something a placed block replaces (grass, flowers, snow), and no water or lava.</summary>
    private bool Open(int s) => s >= 0 && (D.IsAir(s) || (D.StateFlags[s] & GameData.FlagReplaceable) != 0) && (D.StateFlags[s] & (GameData.FlagWater | GameData.FlagLava)) == 0;

    /// <summary>Natural, solid, full-height ground to build on (not sand or gravel, and not someone's build).</summary>
    private bool Ground(int s) => s > 0 && D.HasCollision(s) && !D.IsAir(s) && (D.StateFlags[s] & GameData.FlagReplaceable) == 0
        && D.CollisionBoxes(s).Any(b => b.MaxY >= 1.0) && !client.IsBuiltBlock(D.BlockOf(s)) && D.BlockOf(s).Name is not ("minecraft:sand" or "minecraft:gravel");

    /// <summary>
    /// The nearest spot (within 14 blocks) for a size x size footprint: solid full ground under every cell at one
    /// level, `clear` blocks of open space above it, and nobody standing there.
    /// </summary>
    public (int X, int Y, int Z)? FindFlatSite(double nearX, double nearY, double nearZ, int size, int clear, Entity? keepClear)
    {
        var r = size / 2;
        var w = client.World;
        var bx = (int)Math.Floor(nearX);
        var bz = (int)Math.Floor(nearZ);
        var by = (int)Math.Floor(nearY + 1e-6);
        // Not on top of the player: start a little way off and work outward.
        var candidates = new List<(int X, int Z, int D2)>();
        for (var dx = -14; dx <= 14; dx++)
        for (var dz = -14; dz <= 14; dz++)
        {
            var d2 = dx * dx + dz * dz;
            if (d2 <= 196 && Math.Max(Math.Abs(dx), Math.Abs(dz)) > r + 1) candidates.Add((bx + dx, bz + dz, d2));
        }
        foreach (var (x, z, _) in candidates.OrderBy(c => c.D2))
        {
            if (!w.IsLoaded(x - r, z - r) || !w.IsLoaded(x + r, z + r) || !w.IsLoaded(x - r, z + r) || !w.IsLoaded(x + r, z - r)) continue;
            // The ground level under the centre, near the player's height.
            int? level = null;
            for (var y = by + 3; y >= by - 4 && level == null; y--)
                if (Ground(w.GetState(x, y - 1, z)) && Open(w.GetState(x, y, z))) level = y;
            if (level is not int ly) continue;
            var ok = true;
            for (var dx = -r; dx <= r && ok; dx++)
            for (var dz = -r; dz <= r && ok; dz++)
            {
                if (!Ground(w.GetState(x + dx, ly - 1, z + dz))) ok = false;
                for (var dy = 0; dy < clear && ok; dy++)
                    if (!Open(w.GetState(x + dx, ly + dy, z + dz))) ok = false;
            }
            if (!ok) continue;
            // Nobody inside the footprint (or right next to it).
            if (client.Entities.Values.Any(e => e.IsPlayer && Math.Abs(e.X - (x + 0.5)) < r + 1.5 && Math.Abs(e.Z - (z + 0.5)) < r + 1.5)) continue;
            if (keepClear != null && Math.Abs(keepClear.X - (x + 0.5)) < r + 1.5 && Math.Abs(keepClear.Z - (z + 0.5)) < r + 1.5) continue;
            return (x, ly, z);
        }
        return null;
    }

    // ---------------------------------------------------------------- custom builds (scripts and shapes)

    /// <summary>The last build that was started: its script and where its corner is, so "keep building" can finish it (blocks already standing are skipped).</summary>
    private (string Script, int X, int Y, int Z)? _lastBuild;

    /// <summary>
    /// The nearest spot (within 14 blocks) for a width x depth footprint with `clear` open blocks above it: solid natural ground under every cell at one level and nobody standing there.
    /// Returns the footprint's north-west corner (lowest x and z) and the ground level.
    /// </summary>
    public (int X, int Y, int Z)? FindFlatRect(double nearX, double nearY, double nearZ, int width, int depth, int clear, Entity? keepClear, Func<int, int, int, bool>? groundOk = null)
    {
        var w = client.World;
        int bx = (int)Math.Floor(nearX), bz = (int)Math.Floor(nearZ), by = (int)Math.Floor(nearY + 1e-6);
        int hw = width / 2, hd = depth / 2, keep = Math.Max(width, depth) / 2 + 1;
        var candidates = new List<(int X, int Z, int D2)>();
        for (var dx = -14; dx <= 14; dx++)
        for (var dz = -14; dz <= 14; dz++)
        {
            var d2 = dx * dx + dz * dz;
            if (d2 <= 196 && Math.Max(Math.Abs(dx), Math.Abs(dz)) > keep) candidates.Add((bx + dx, bz + dz, d2));
        }
        foreach (var (cx, cz, _) in candidates.OrderBy(c => c.D2))
        {
            int x0 = cx - hw, z0 = cz - hd, x1 = x0 + width - 1, z1 = z0 + depth - 1;
            if (!w.IsLoaded(x0, z0) || !w.IsLoaded(x1, z1) || !w.IsLoaded(x0, z1) || !w.IsLoaded(x1, z0)) continue;
            int? level = null;
            for (var y = by + 3; y >= by - 4 && level == null; y--)
                if ((groundOk != null ? groundOk(cx, y - 1, cz) : Ground(w.GetState(cx, y - 1, cz))) && Open(w.GetState(cx, y, cz))) level = y;
            if (level is not int ly) continue;
            var ok = true;
            for (var x = x0; x <= x1 && ok; x++)
            for (var z = z0; z <= z1 && ok; z++)
            {
                if (groundOk != null ? !groundOk(x, ly - 1, z) : !Ground(w.GetState(x, ly - 1, z))) ok = false;
                for (var dy = 0; dy < clear && ok; dy++)
                    if (!Open(w.GetState(x, ly + dy, z))) ok = false;
            }
            if (!ok) continue;
            if (client.Entities.Values.Any(e => e.IsPlayer && e.X > x0 - 1.5 && e.X < x1 + 2.5 && e.Z > z0 - 1.5 && e.Z < z1 + 2.5)) continue;
            if (keepClear != null && keepClear.X > x0 - 1.5 && keepClear.X < x1 + 2.5 && keepClear.Z > z0 - 1.5 && keepClear.Z < z1 + 2.5) continue;
            return (x0, ly, z0);
        }
        return null;
    }

    /// <summary>
    /// Builds what a script describes (see <see cref="BuildScript"/>) on a flat spot near a player (or here): checks the script and the materials first, picks the site, places the blocks
    /// layer by layer with something to click against each time, and goes round again for any that failed. The script and place are kept: <see cref="ResumeBuildAsync"/> finishes an
    /// interrupted build.
    /// </summary>
    public async Task<(bool, string)> BuildScriptAsync(string scriptText, string? nearPlayer, CancellationToken ct)
    {
        BuildScript script;
        try { script = BuildScript.Parse(scriptText, D); }
        catch (BuildScriptException e) { return (false, "That build plan has a problem: " + e.Message); }

        var player = FindPlayer(nearPlayer);
        var (ox, oy, oz) = player != null ? (player.X, player.Y, player.Z) : client.Position;
        if (FindFlatRect(ox, oy, oz, script.Width, script.Depth, script.Height + 1, player) is not { } site)
            return (false, $"I can't find a flat, clear spot {script.Width} by {script.Depth} blocks around here.");
        return await BuildAtAsync(scriptText, site, ct);
    }

    /// <summary>Checks that we carry what a script needs, remembers it as the build in progress, and builds it with its corner at <paramref name="corner"/>.</summary>
    private async Task<(bool, string)> BuildAtAsync(string scriptText, (int X, int Y, int Z) corner, CancellationToken ct)
    {
        BuildScript script;
        try { script = BuildScript.Parse(scriptText, D); }
        catch (BuildScriptException e) { return (false, "That build plan has a problem: " + e.Message); }
        var shortages = script.Needs.Select(kv => (Item: kv.Key, Need: kv.Value, Have: Have(BuildScript.Matcher(kv.Key)))).Where(x => x.Have < x.Need).ToList();
        if (shortages.Count > 0)
            return (false, "I don't have enough to build that: " + string.Join(", ", shortages.Select(x => $"I need {x.Need} {BuildScript.Pretty(x.Item)} and have {x.Have}")) + ".");
        _lastBuild = (scriptText, corner.X, corner.Y, corner.Z);
        return await RunBuildAsync(script, corner, ct);
    }

    /// <summary>
    /// Builds a named shape (see <see cref="BuildShapes"/>): picks the material we carry most of unless one was asked for, a flat spot near the player, the doorway facing the player unless a
    /// side was given, and uses windows, a door and a torch only if we carry them.
    /// </summary>
    public async Task<(bool, string)> BuildShapeAsync(string shape, int width, int depth, int height, string? material, string? roofMaterial, string? doorSide, string? nearPlayer, CancellationToken ct)
    {
        var kind = BuildShapes.Normalize(shape);
        if (kind == null) return (false, $"I don't know how to build a {shape}. I can build a {string.Join(", ", BuildShapes.Names)}, or follow a plan you describe.");
        var (w, d, h) = BuildShapes.Fit(kind, width, depth, height);
        if (ChooseMaterial(material) is not var (wallName, _, wallPretty, _)) return (false, $"I can't build with {material}.");
        string roof;
        try { roof = string.IsNullOrWhiteSpace(roofMaterial) ? wallName : BuildScript.ResolveMaterial(roofMaterial, D); }
        catch (BuildScriptException e) { return (false, e.Message); }

        var rise = kind == "house" ? (Math.Min(w, d) + 1) / 2 : kind == "pyramid" ? (w + 1) / 2 : 1;
        var player = FindPlayer(nearPlayer);
        var (ox, oy, oz) = player != null ? (player.X, player.Y, player.Z) : client.Position;
        if (FindFlatRect(ox, oy, oz, w, d, h + rise + 2, player) is not { } site)
            return (false, $"I can't find a flat, clear spot {w} by {d} blocks around here for a {kind}.");

        // The doorway (or gate) faces whoever asked, so they can walk right in, unless a side was given.
        var side = doorSide?.Trim().ToLowerInvariant() switch
        {
            "north" or "n" => BlockFace.North, "south" or "s" => BlockFace.South, "east" or "e" => BlockFace.East, "west" or "w" => BlockFace.West,
            _ => Facing(site.X + w / 2.0, site.Z + d / 2.0, ox, oz),
        };
        var script = BuildShapes.Script(kind, w, d, h, wallName, roof, side,
            windows: Have(s => s.Name == "minecraft:glass") >= 3, door: Have(Blueprints.IsDoor) > 0, torch: Have(s => s.Name == "minecraft:torch") > 0);
        var (ok, text) = await BuildAtAsync(script, site, ct);
        if (!ok) return (false, text.Replace("build that", $"build a {w} by {d} {wallPretty} {kind}"));
        return (true, $"{text} It is a {wallPretty} {kind}" + (kind is "house" or "castle" ? $", with the {(kind == "castle" ? "gate" : "door")} on the {Name(side)} side." : "."));
    }

    /// <summary>Carries on with the last build where it stands: blocks already placed are left alone, the rest are placed.</summary>
    public async Task<(bool, string)> ResumeBuildAsync(CancellationToken ct)
    {
        if (_lastBuild is not { } last) return (false, "I haven't started a build to carry on with.");
        BuildScript script;
        try { script = BuildScript.Parse(last.Script, D); }
        catch (BuildScriptException e) { return (false, "The plan I was building has a problem: " + e.Message); }
        return await RunBuildAsync(script, (last.X, last.Y, last.Z), ct);
    }

    private async Task<(bool, string)> RunBuildAsync(BuildScript script, (int X, int Y, int Z) corner, CancellationToken ct)
    {
        var world = script.Cells.Select(c => c with { X = c.X + corner.X, Y = c.Y + corner.Y, Z = c.Z + corner.Z }).ToList();
        var ordered = BuildScript.Order(world, (x, y, z) => Standable(client.World.GetState(x, y, z)));
        var plan = ordered.Select(c => new Placement(c.X, c.Y, c.Z, BuildScript.Matcher(c.Item), BuildScript.Pretty(c.Item), c.Facing)).ToList();

        var placed = 0;
        var failed = 0;
        string? missing = null;
        for (var pass = 0; pass < 3 && !ct.IsCancellationRequested; pass++)
        {
            var (p, f, m) = await client.BuildAsync(plan, ct);
            placed += p;
            failed = f;
            missing = m;
            if (m != null || f == 0 || p == 0) break;       // done, out of something, or a pass that got nowhere
        }
        // Step away from it, to the open side, so we are not left standing inside our own build.
        await client.GoToAsync(Goal.Near(corner.X + script.Width / 2.0, corner.Y, corner.Z - 2.5, 2.0), new PathOptions { MaxNodes = 8_000 }, ct);
        var size = $"{script.Width} by {script.Depth}, {script.Height} high";
        if (missing != null) return (false, $"I ran out of {missing} while building ({placed} blocks placed, {size}). Give me more and say keep building.");
        return failed == 0
            ? (true, $"I built it: {placed} blocks placed ({size}).")
            : (true, $"I built most of it ({placed} blocks placed, {size}), but {failed} {(failed == 1 ? "block" : "blocks")} wouldn't go where I wanted.");
    }

    // ---------------------------------------------------------------- walls, towers, bridges

    /// <summary>A block whose top we can stand on (any full-height solid block, built or natural).</summary>
    private bool Standable(int s) => s > 0 && D.HasCollision(s) && (D.StateFlags[s] & GameData.FlagReplaceable) == 0
        && D.CollisionBoxes(s).Any(b => b.MaxY >= 1.0);

    /// <summary>The feet level of the ground in column x,z near height y (3 above to 6 below), or null (water, a deep drop, unloaded).</summary>
    private int? GroundLevel(int x, int z, int nearY)
    {
        var w = client.World;
        for (var y = nearY + 3; y >= nearY - 6; y--)
            if (Standable(w.GetState(x, y - 1, z)) && Open(w.GetState(x, y, z))) return y;
        return null;
    }

    /// <summary>A direction a player said ("north", "east"...), else the way the asker (or we) face.</summary>
    private BlockFace FaceFrom(string? direction, Entity? asker)
    {
        switch (direction?.Trim().ToLowerInvariant())
        {
            case "north" or "n": return BlockFace.North;
            case "south" or "s": return BlockFace.South;
            case "east" or "e": return BlockFace.East;
            case "west" or "w": return BlockFace.West;
        }
        // Yaw 0 looks south (+z), 90 west, 180 north, 270 east.
        var yaw = asker?.Yaw ?? client.Rotation.Yaw;
        return ((((int)Math.Round(yaw / 90.0) % 4) + 4) % 4) switch { 0 => BlockFace.South, 1 => BlockFace.West, 2 => BlockFace.North, _ => BlockFace.East };
    }

    /// <summary>Which side of (fromX, fromZ) the point (toX, toZ) is on.</summary>
    private static BlockFace Facing(double fromX, double fromZ, double toX, double toZ)
    {
        double fx = toX - fromX, fz = toZ - fromZ;
        return Math.Abs(fx) > Math.Abs(fz) ? (fx > 0 ? BlockFace.East : BlockFace.West) : (fz > 0 ? BlockFace.South : BlockFace.North);
    }

    private static string Name(BlockFace f) => f.ToString().ToLowerInvariant();

    /// <summary>
    /// Builds a wall `height` blocks high. Straight: `length` blocks going `direction` from the asker (or the way they
    /// face). Around: a square of radius `length` around the asker with a gateway on the side facing Vertex (a door
    /// in it if we carry one), e.g. a pen for animals. The wall rises with the ground; a dip is filled up to the
    /// wall's top so there is no low spot to climb over. Rocks and trees in the line are left as part of the wall;
    /// columns over water or a deep drop are skipped.
    /// </summary>
    public async Task<(bool, string)> BuildWallAsync(string? nearPlayer, string? direction, int length, int height, bool around, string? material, CancellationToken ct)
    {
        if (ChooseMaterial(material) is not var (_, match, pretty, noun)) return (false, $"I can't build a wall out of {material}.");
        height = Math.Clamp(height, 1, 5);
        var player = FindPlayer(nearPlayer);
        var (ox, oy, oz) = player != null ? (player.X, player.Y, player.Z) : client.Position;
        int bx = (int)Math.Floor(ox), by = (int)Math.Floor(oy + 1e-6), bz = (int)Math.Floor(oz);

        var columns = new List<(int X, int Z)>();
        (int X, int Z)? gate = null;
        BlockFace side;
        var radius = 0;
        if (around)
        {
            radius = Math.Clamp(length, 2, 10);
            columns.AddRange(Blueprints.Ring(bx, bz, radius));
            var (vx, _, vz) = client.Position;
            side = Facing(bx + 0.5, bz + 0.5, vx, vz);
            var (gx, gz) = Blueprints.Dir(side);
            gate = (bx + gx * radius, bz + gz * radius);
        }
        else
        {
            length = Math.Clamp(length, 2, 32);
            side = FaceFrom(direction, player);
            var (dx, dz) = Blueprints.Dir(side);
            for (var i = 1; i <= length; i++) columns.Add((bx + dx * i, bz + dz * i));
        }

        var plan = new List<Placement>();
        Placement? door = null;
        var skipped = 0;
        foreach (var (x, z) in columns)
        {
            if (GroundLevel(x, z, by) is not int g) { skipped++; continue; }
            if ((x, z) == gate)
            {
                if (height >= 2 && Have(Blueprints.IsDoor) > 0) door = new Placement(x, g, z, Blueprints.IsDoor, "a door", side);
                for (var dy = 2; dy < height; dy++) plan.Add(new Placement(x, g + dy, z, match, noun)); // above the gateway
                continue;
            }
            for (var wy = g; wy < Math.Max(g, by) + height; wy++)
                if (Open(client.World.GetState(x, wy, z))) plan.Add(new Placement(x, wy, z, match, noun));
        }
        if (plan.Count == 0) return (false, "There's nowhere to put a wall there (water or a drop all the way).");
        if (Have(match) < plan.Count) return (false, $"That wall takes {plan.Count} {noun} and I only have {Have(match)}.");
        if (door != null) plan.Add(door);

        var (placed, failed, missing) = await client.BuildAsync(plan, ct);
        if (missing != null) return (false, $"I ran out of {missing} while building the wall ({placed} blocks placed).");
        var what = around
            ? $"a {pretty} wall around {player?.PlayerName ?? "here"}, {2 * radius + 1} by {2 * radius + 1}, with {(door != null ? "a door" : "a gap")} on the {Name(side)} side"
            : $"a {pretty} wall {columns.Count} blocks long and {height} high going {Name(side)}";
        var notes = (failed > 0 ? $" {failed} {(failed == 1 ? "block" : "blocks")} didn't go where I wanted." : "")
            + (skipped > 0 ? $" I left out {skipped} {(skipped == 1 ? "spot" : "spots")} over water or a drop." : "");
        return (true, $"I built {what}.{notes}");
    }

    /// <summary>
    /// Builds a 3x3 lookout tower `height` blocks high near the asker: Vertex stands in the middle, rings itself in
    /// layer by layer and pillars up the core, hangs a ladder up the outside (facing the asker) so anyone can climb
    /// it, finishes on top behind a one-block parapet, then climbs back down the ladder.
    /// </summary>
    public async Task<(bool, string)> BuildTowerAsync(string? nearPlayer, int height, string? material, CancellationToken ct)
    {
        if (ChooseMaterial(material) is not var (_, match, pretty, noun)) return (false, $"I can't build a tower out of {material}.");
        height = Math.Clamp(height, 4, 16);
        var need = 8 * height + (height - 1);
        static bool IsLadder(ItemStack s) => s.Name == "minecraft:ladder";
        if (Have(IsLadder) < height)
        {
            var (_, why) = await client.CraftItemAsync("minecraft:ladder", height - Have(IsLadder), ct);
            if (Have(IsLadder) < height)
                return (false, $"I need {height} ladders so people can climb the tower, and I couldn't make them ({why}).");
        }
        if (Have(match) < need) return (false, $"A tower {height} high takes {need} {noun} and I only have {Have(match)}.");

        var player = FindPlayer(nearPlayer);
        var (ox, oy, oz) = player != null ? (player.X, player.Y, player.Z) : client.Position;
        // The ladder sticks out one block past the 3x3, so the site is 5x5, clear all the way up.
        if (FindFlatSite(ox, oy, oz, 5, height + 3, player) is not var (cx, y, cz)) return (false, "I can't find a flat, clear spot for a tower around here.");
        var ladderSide = Facing(cx + 0.5, cz + 0.5, ox, oz);
        var (lx, lz) = Blueprints.Dir(ladderSide);
        if (!await client.GoToAsync(Goal.Block(cx, y, cz), new PathOptions { MaxNodes = 20_000 }, ct) || client.FeetBlock != (cx, y, cz))
            return (false, "I couldn't get to the spot I picked for the tower.");

        var w = client.World;
        bool LadderAt(int level) => D.BlockOf(w.GetState(cx + 2 * lx, level, cz + 2 * lz)).Name == "minecraft:ladder";
        var top = y + height - 1; // the floor we finish on; the ring's top layer is the parapet around it
        for (var f = y; ; f++)
        {
            // The ring around us at our feet and head level (each block leans on the one below it)...
            var layer = new List<Placement>();
            for (var ly = f; ly <= Math.Min(f + 1, top); ly++)
                foreach (var (x, z) in Blueprints.Ring(cx, cz, 1)) layer.Add(new Placement(x, ly, z, match, noun));
            var (_, failed, missing) = await client.BuildAsync(layer, ct);
            if (missing != null) return (false, $"I ran out of {missing} building the tower.");
            if (failed > 0 || client.FeetBlock != (cx, f, cz)) return (false, $"Something went wrong building the tower at height {f - y + 1}.");
            // ...then the ladder on its outside face (the clicked face sets which way a ladder hangs).
            if (!LadderAt(f))
            {
                if (!await client.EquipAsync(IsLadder, ct)) return (false, "I ran out of ladders.");
                if (!await client.PlaceAgainstAsync(cx + lx, f, cz + lz, ladderSide, ct) || !LadderAt(f))
                    return (false, $"I couldn't hang a ladder at height {f - y + 1}.");
            }
            if (f == top) break;
            if (!await client.PillarUpAsync(match, ct)) return (false, $"I couldn't climb up my tower at height {f - y + 1}.");
        }

        var built = $"I built a {pretty} tower {height} blocks high with a ladder on the {Name(ladderSide)} side.";
        // Down the ladder to its foot.
        return await client.GoToAsync(Goal.Block(cx + 3 * lx, y, cz + 3 * lz), new PathOptions { MaxNodes = 10_000 }, ct)
            ? (true, built)
            : (true, built + " I'm still up on top: I couldn't find my way down.");
    }

    /// <summary>
    /// Bridges a gap (a river, lava, a ravine) going `direction` from the asker (or the way they face): walks to the
    /// edge, then places each block against the one it stands on and steps onto it, until ground at the same level
    /// on the far side (up to 48 blocks).
    /// </summary>
    public async Task<(bool, string)> BuildBridgeAsync(string? nearPlayer, string? direction, string? material, CancellationToken ct)
    {
        if (ChooseMaterial(material) is not var (_, match, pretty, noun)) return (false, $"I can't build a bridge out of {material}.");
        var player = FindPlayer(nearPlayer);
        var face = FaceFrom(direction, player);
        var (dx, dz) = Blueprints.Dir(face);
        var (ox, oy, oz) = player != null ? (player.X, player.Y, player.Z) : client.Position;
        int bx = (int)Math.Floor(ox), y = (int)Math.Floor(oy + 1e-6), bz = (int)Math.Floor(oz);
        var w = client.World;
        bool Walkable(int x, int z) => Open(w.GetState(x, y, z)) && Open(w.GetState(x, y + 1, z));
        string Blocking(int x, int z) =>
            D.BlockOf(Math.Max(0, Open(w.GetState(x, y, z)) ? w.GetState(x, y + 1, z) : w.GetState(x, y, z))).Name.Bare().Replace('_', ' ');

        // The edge within 8 blocks, then the gap, then ground at our level on the far side.
        var edge = -1;
        for (var i = 0; i <= 8 && edge < 0; i++)
        {
            var (x, z) = (bx + dx * i, bz + dz * i);
            if (!w.IsLoaded(x, z)) break;
            if (!Walkable(x, z)) return (false, $"There's {Blocking(x, z)} in the way {Name(face)} of here.");
            if (!Standable(w.GetState(x, y - 1, z))) edge = i;
        }
        if (edge < 1) return (false, $"I don't see anything to bridge {Name(face)} of here.");
        var gap = new List<(int X, int Z)>();
        var far = -1;
        for (var i = edge; i <= edge + 48 && far < 0; i++)
        {
            var (x, z) = (bx + dx * i, bz + dz * i);
            if (!w.IsLoaded(x, z)) break;
            if (!Walkable(x, z)) return (false, $"There's {Blocking(x, z)} in the way {i} blocks {Name(face)}, so I can't bridge straight across there.");
            if (Standable(w.GetState(x, y - 1, z))) far = i;
            else gap.Add((x, z));
        }
        if (far < 0) return (false, $"The other side is too far {Name(face)} (more than 48 blocks), or I can't see it.");
        if (Have(match) < gap.Count) return (false, $"The bridge needs {gap.Count} {noun} and I only have {Have(match)}.");

        if (!await client.GoToAsync(Goal.Block(bx + dx * (edge - 1), y, bz + dz * (edge - 1)), new PathOptions { MaxNodes = 20_000 }, ct))
            return (false, "I couldn't get to the edge to start the bridge.");
        var placed = 0;
        foreach (var (x, z) in gap)
        {
            ct.ThrowIfCancellationRequested();
            if (!Standable(w.GetState(x, y - 1, z)))
            {
                if (!await client.EquipAsync(match, ct)) return (false, $"I ran out of {noun} after {placed} blocks of bridge.");
                if (!await client.PlaceBlockAsync(x, y - 1, z, ct)) return (false, $"I couldn't place the next bridge block ({placed} placed).");
                placed++;
            }
            // Step onto it: always standing on the bridge, never swimming or falling for the next one.
            if (!await client.GoToAsync(Goal.Block(x, y, z), new PathOptions { MaxNodes = 2_000 }, ct)) return (false, $"I couldn't step out onto my bridge ({placed} placed).");
        }
        await client.GoToAsync(Goal.Block(bx + dx * far, y, bz + dz * far), new PathOptions { MaxNodes = 2_000 }, ct);
        return (true, $"I built a {pretty} bridge {gap.Count} blocks long going {Name(face)}, and I'm on the other side.");
    }

    // ---------------------------------------------------------------- learning unknown blocks by touch

    /// <summary>
    /// Kinds of block (raw states) near here that Vertex has no data for and has not studied yet, nearest first:
    /// a modded server's blocks when no modpack taught them.
    /// </summary>
    public List<(int X, int Y, int Z, int Raw)> UnstudiedBlocks(double radius)
    {
        var w = client.World;
        var known = D.KnownStateCount;
        var seen = new HashSet<int>();
        var result = new List<(int, int, int, int)>();
        foreach (var (x, y, z) in w.FindBlocks(s => D.IsUnknownBlock(s), client.Position, radius, 4096))
        {
            var raw = w.GetRawState(x, y, z);
            if (raw < known || client.LearnedStates.GetValueOrDefault(raw)?.Shape != null || !seen.Add(raw)) continue;
            result.Add((x, y, z, raw));
        }
        return result;
    }

    /// <summary>
    /// Door-shaped unknown blocks around that were never right-clicked (one of each kind, nearest first), when the
    /// server's block names say it has doors or gates we have no data for. Without such names, a thin plate is a
    /// decoration and is left alone.
    /// </summary>
    public List<(int X, int Y, int Z, int Raw)> UntriedDoors(double radius)
    {
        if (client.UnknownDoorNames().Count == 0) return [];
        var w = client.World;
        var seen = new HashSet<int>();
        var result = new List<(int, int, int, int)>();
        foreach (var (x, y, z) in w.FindBlocks(s => D.IsUnknownBlock(s), client.Position, radius, 4096))
        {
            var raw = w.GetRawState(x, y, z);
            if (D.Learned(raw) is not { } l || l.UseTurnsInto != null || l.UseDoesNothing || !MinecraftClient.LooksLikeDoor(l.Shape)) continue;
            // The lower half: a door's upper half is felt and switched with it.
            if (D.Learned(w.GetRawState(x, y - 1, z)) is { } below && MinecraftClient.LooksLikeDoor(below.Shape)) continue;
            if (seen.Add(raw)) result.Add((x, y, z, raw));
        }
        return result;
    }

    /// <summary>
    /// Studies the unknown blocks around: for each kind not yet learned, walks up to one and feels its shape from
    /// every open side and from above, the server deciding where its body fits (never breaking anything). From
    /// then on physics and pathing use the real shape (a doormat is a floor, a cobweb a trap to avoid). Then, if
    /// the server's block names say it has doors or gates we have no data for, right-clicks each door-shaped kind
    /// once to see whether it opens (and puts it back as it was): a learned door is one Vertex walks through.
    /// </summary>
    public async Task<(bool, string)> StudyBlocksAsync(double radius, CancellationToken ct)
    {
        var learned = 0;
        var sticky = 0;
        var tried = new HashSet<int>();
        while (!ct.IsCancellationRequested)
        {
            var next = UnstudiedBlocks(radius).FirstOrDefault(b => !tried.Contains(b.Raw));
            if (next == default) break;
            tried.Add(next.Raw);
            // The nearest instance of this kind that has room around it.
            var probe = await client.MeasureBlockAsync(next.X, next.Y, next.Z, ct);
            if (probe == null) continue;
            client.Learn(probe);
            learned++;
            if (probe.Sticky) sticky++;
        }
        var doors = 0;
        var triedDoors = new HashSet<int>();
        while (!ct.IsCancellationRequested)
        {
            var next = UntriedDoors(radius).FirstOrDefault(b => !triedDoors.Contains(b.Raw));
            if (next == default) break;
            triedDoors.Add(next.Raw);
            if (await client.LearnUseAsync(next.X, next.Y, next.Z, ct) is { Changed: true } use && D.IsLearnedDoorway(use.RawState)) doors++;
        }
        // Kinds still unknown in the end (an upper half is learned with the block under it, so count at the end).
        var unreachable = tried.Count(r => client.LearnedStates.GetValueOrDefault(r)?.Shape == null);
        if (learned == 0 && unreachable == 0 && doors == 0) return (true, "There are no blocks around here I don't know.");
        var parts = new List<string>();
        if (learned > 0) parts.Add($"I studied {(learned == 1 ? "a kind" : $"{learned} kinds")} of block I didn't know{(sticky > 0 ? $", and {(sticky == 1 && learned == 1 ? "it" : $"{sticky} of them")} would trap me, so I'll stay out of {(sticky == 1 && learned == 1 ? "it" : "those")}" : "")}.");
        if (doors > 0) parts.Add(doors == 1 ? (learned == 1 ? "It's a door: I can open it now." : "One of them is a door: I can open it now.") : $"{doors} of them are doors: I can open them now.");
        if (unreachable > 0) parts.Add(learned == 0 ? "I couldn't get next to the strange blocks to feel them out." : $"{unreachable} I couldn't get next to.");
        return (learned > 0 || doors > 0, string.Join(" ", parts));
    }

    // ---------------------------------------------------------------- trading with villagers

    /// <summary>The nearest villager or wandering trader we know of (within 48 blocks).</summary>
    public Entity? FindMerchant()
    {
        var (x, y, z) = client.Position;
        return client.Entities.Values.Where(e => e.Name is "minecraft:villager" or "minecraft:wandering_trader" && e.DistanceTo(x, y, z) < 48)
            .OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
    }

    /// <summary>Walks up to the nearest merchant and opens its trades (null window and a reason when that is not possible).</summary>
    private async Task<(ContainerWindow? Window, Entity? Merchant, string Why)> OpenNearestMerchantAsync(CancellationToken ct)
    {
        var merchant = FindMerchant();
        if (merchant == null) return (null, null, "I don't see a villager or trader anywhere near.");
        await client.GoToAsync(Goal.Near(merchant.X, merchant.Y, merchant.Z, 2.0), null, ct);
        merchant = client.Entities.TryGetValue(merchant.Id, out var now) ? now : merchant;
        var window = await client.OpenTradesAsync(merchant, ct);
        return window == null
            ? (null, merchant, $"That {merchant.Name.Bare().Replace('_', ' ')} won't trade: it may have no job yet (a nitwit or unemployed villager), be a baby, or be asleep.")
            : (window, merchant, "");
    }

    private static string LevelName(int level) => level switch { 1 => "novice", 2 => "apprentice", 3 => "journeyman", 4 => "expert", 5 => "master", _ => "villager" };

    private static string Plain(string itemName) => itemName.Bare().Replace('_', ' ');

    private static string Price(Trade t) => $"{t.PriceA} {Plain(t.CostA.Name)}" + (t.CostB != null ? $" and {t.PriceB} {Plain(t.CostB.Name)}" : "");

    /// <summary>Says what the nearest villager or trader offers (walks up to it and looks; buys nothing).</summary>
    public async Task<(bool, string)> VillagerOffersAsync(CancellationToken ct)
    {
        var (window, merchant, why) = await OpenNearestMerchantAsync(ct);
        if (window == null) return (false, why);
        try
        {
            var lines = window.Trades.Select(t => $"{Price(t)} for {t.Result.Count} {Plain(t.Result.Name)}" + (t.OutOfStock || t.UsesLeft == 0 ? " (sold out)" : ""));
            return (true, $"This {LevelName(window.MerchantLevel)} {Plain(merchant!.Name)} trades: {string.Join("; ", lines)}.");
        }
        finally { await client.CloseWindowAsync(ct); }
    }

    /// <summary>
    /// Trades with the nearest villager or trader. `want` is what to GET ("emerald", "bread"), `give` is what to hand over ("wheat"); either or both. With only `want`, buys about `count`
    /// of it; with only `give`, sells about `count` of it (0 = all we have); with both, uses the trade that gives `want` for `give`.
    /// </summary>
    public async Task<(bool, string)> TradeWithVillagerAsync(string? want, string? give, int count, CancellationToken ct)
    {
        want = string.IsNullOrWhiteSpace(want) ? null : want.Trim().ToLowerInvariant();
        give = string.IsNullOrWhiteSpace(give) ? null : give.Trim().ToLowerInvariant();
        if (want == null && give == null) return (false, "Trade what? Tell me what to buy or what to sell.");
        var (window, merchant, why) = await OpenNearestMerchantAsync(ct);
        if (window == null) return (false, why);
        try
        {
            var matching = window.Trades.Where(t => (want == null || t.Gives(want)) && (give == null || t.Wants(give))).ToList();
            if (matching.Count == 0)
                return (false, $"It doesn't {(want != null && give != null ? $"give {want} for {give}" : want != null ? $"sell {want}" : $"buy {give}")}. " + OffersSummary(window));
            // The best trade we can actually do: in stock, ingredients in hand, the most for what we have.
            var plan = matching.Where(t => t.UsesLeft > 0).Select(t => (Trade: t, Times: MaxTimes(t))).Where(x => x.Times > 0)
                .OrderByDescending(x => x.Trade.Result.Count * x.Times).ThenBy(x => x.Trade.PriceA).FirstOrDefault();
            if (plan.Trade == null)
            {
                var t = matching[0];
                return (false, t.UsesLeft == 0
                    ? $"The {Plain(merchant!.Name)} is sold out of that for now: {t}."
                    : $"I can't pay for that yet: it costs {Price(t)} and I have {client.Inventory.Count(t.CostA.Name)} {Plain(t.CostA.Name)}.");
            }
            var trade = plan.Trade;
            // How many trades: to get `count` of the result, or to sell `count` of the ingredient (all of it when 0), never more than the stock or our ingredients allow.
            var times = want != null
                ? Math.Max(1, (int)Math.Ceiling(Math.Max(count, 1) / (double)trade.Result.Count))
                : count <= 0 ? int.MaxValue : Math.Max(1, count / Math.Max(1, trade.PriceA));
            times = Math.Min(times, plan.Times);
            var costBefore = client.Inventory.Count(trade.CostA.Name);
            var gotBefore = client.Inventory.Count(trade.Result.Name);
            var done = await client.TradeAsync(trade.Index, times, ct);
            var paid = costBefore - client.Inventory.Count(trade.CostA.Name);
            var got = client.Inventory.Count(trade.Result.Name) - gotBefore;
            if (done == 0) return (false, "The trade didn't go through.");
            return (true, $"I traded {paid} {Plain(trade.CostA.Name)}" + (trade.CostB != null ? $" and {done * trade.PriceB} {Plain(trade.CostB.Name)}" : "")
                + $" for {got} {Plain(trade.Result.Name)} ({done} trade{(done == 1 ? "" : "s")}).");
        }
        finally { if (client.OpenWindow != null) await client.CloseWindowAsync(ct); }

        int MaxTimes(Trade t)
        {
            var byA = client.Inventory.Count(t.CostA.Name) / Math.Max(1, t.PriceA);
            var byB = t.CostB == null ? int.MaxValue : client.Inventory.Count(t.CostB.Name) / Math.Max(1, t.PriceB);
            return Math.Min(Math.Min(byA, byB), t.UsesLeft);
        }
    }

    private static string OffersSummary(ContainerWindow window) =>
        "It offers: " + string.Join("; ", window.Trades.Select(t => $"{Price(t)} for {t.Result.Count} {Plain(t.Result.Name)}")) + ".";

    // ---------------------------------------------------------------- farming

    /// <summary>Crops: the block, its ripe age, and the item that replants it.</summary>
    private static readonly (string Block, string RipeAge, string Seed)[] Crops =
    [
        ("minecraft:wheat", "7", "minecraft:wheat_seeds"), ("minecraft:carrots", "7", "minecraft:carrot"),
        ("minecraft:potatoes", "7", "minecraft:potato"), ("minecraft:beetroots", "3", "minecraft:beetroot_seeds"),
    ];

    /// <summary>
    /// Harvests the ripe crops within 24 blocks (leaving unripe ones), picks up what drops, and replants each spot
    /// with the matching seed, like a careful farmer.
    /// </summary>
    public async Task<(bool, string)> FarmAsync(CancellationToken ct)
    {
        var crops = Crops.Select(c => (Id: D.Block(c.Block)!.Id, c.RipeAge, c.Seed)).ToList();
        var harvested = 0;
        var replanted = 0;
        var tried = new HashSet<(int, int, int)>();
        while (!ct.IsCancellationRequested)
        {
            var ripe = client.World.FindBlocks(s => s > 0 && crops.Any(c => c.Id == D.StateBlock[s] && D.GetProperty(s, "age") == c.RipeAge), client.Position, 24, 64)
                .Where(p => !tried.Contains(p)).ToList();
            if (ripe.Count == 0) break;
            var (x, y, z) = ripe[0];
            tried.Add((x, y, z));
            var crop = crops.First(c => c.Id == D.StateBlock[client.World.GetState(x, y, z)]);
            if (!await client.GoToAsync(new GoalReachBlock(x, y, z, 3.5), new PathOptions { MaxNodes = 10_000 }, ct)) continue;
            if (!await client.DigAsync(x, y, z, ct)) continue;
            harvested++;
            await PickUpNearbyAsync(x, y, z, ct);
            // Replant on the farmland the crop stood on.
            if (client.World.GetBlock(x, y - 1, z)?.Name == "minecraft:farmland" && client.Inventory.Count(crop.Seed) > 0
                && await client.EquipAsync(crop.Seed, ct))
            {
                if (client.DistanceToBlock(x, y - 1, z) > client.BlockReach - 0.5)
                    await client.GoToAsync(new GoalReachBlock(x, y - 1, z, 3.5), new PathOptions { MaxNodes = 5_000 }, ct);
                await client.UseOnBlockAsync(x, y - 1, z, SpawnDev.Minecraft.BlockFace.Up, Hand.Main, null, ct);
                if (await client.WaitUntilAsync(() => client.World.GetBlock(x, y, z) is { } b && b.Id == crop.Id, 20, ct)) replanted++;
            }
        }
        if (harvested == 0) return (false, "I don't see any ripe crops nearby.");
        return (true, $"I harvested {harvested} crops and replanted {replanted}. I have: {InventorySummary()}.");
    }

    // ---------------------------------------------------------------- a new field

    private static bool IsHoe(ItemStack s) => s.Name.EndsWith("_hoe");

    /// <summary>Soil a hoe turns into farmland.</summary>
    private bool Tillable(int x, int y, int z) => client.World.GetBlock(x, y, z)?.Name is "minecraft:dirt" or "minecraft:grass_block" or "minecraft:dirt_path";

    /// <summary>
    /// Makes a new field of crops: a flat spot of soil near the player (a size x size square, 5 by default), water in the middle if we carry a bucket of it (it keeps the soil within four
    /// blocks wet), every other block tilled with a hoe and sown, one seed each. Makes a wooden hoe if we have none and the means to. Says what it did, and what it could not.
    /// </summary>
    public async Task<(bool, string)> PlantFieldAsync(string? crop, int size, string? nearPlayer, CancellationToken ct)
    {
        var wanted = crop?.Trim().ToLowerInvariant().TrimEnd('s');
        var matches = Crops.Where(c => string.IsNullOrEmpty(wanted) || c.Block.Bare().Contains(wanted) || c.Seed.Bare().Contains(wanted)).ToList();
        if (matches.Count == 0) return (false, $"I don't know how to grow {crop}. I can plant wheat, carrots, potatoes or beetroot.");
        var choice = matches.OrderByDescending(c => client.Inventory.Count(c.Seed)).First();
        var seeds = client.Inventory.Count(choice.Seed);
        var cropName = Plain(choice.Block);
        if (seeds == 0) return (false, $"I don't have any {Plain(choice.Seed)} to plant. (Breaking tall grass gives wheat seeds; carrots and potatoes come from villagers and zombies.)");
        size = size <= 0 ? 5 : Math.Clamp(size, 3, 9);

        if (Have(IsHoe) == 0)
        {
            var (made, why) = await client.CraftItemAsync("minecraft:wooden_hoe", 1, ct);
            if (!made || Have(IsHoe) == 0) return (false, $"I need a hoe to till the soil, and I couldn't make one: {why}");
        }

        var player = FindPlayer(nearPlayer);
        var (ox, oy, oz) = player != null ? (player.X, player.Y, player.Z) : client.Position;
        if (FindFlatRect(ox, oy, oz, size, size, 2, player, Tillable) is not { } site)
            return (false, $"I can't find a flat, clear patch of soil {size} by {size} blocks around here.");
        int gy = site.Y, cx = site.X + size / 2, cz = site.Z + size / 2;

        // Water in the middle: dig that block out and pour the bucket into the hole.
        var water = false;
        if (Have(s => s.Name == "minecraft:water_bucket") > 0 && size >= 3)
        {
            await client.GoToAsync(new GoalReachBlock(cx, gy - 1, cz, 3.5), new PathOptions { MaxNodes = 10_000 }, ct);
            if (await client.DigAsync(cx, gy - 1, cz, ct))
            {
                await PickUpNearbyAsync(cx, gy - 1, cz, ct);
                if (await client.EquipAsync(s => s.Name == "minecraft:water_bucket", ct))
                {
                    client.LookAt(cx + 0.5, gy - 1.0, cz + 0.5);
                    await client.WaitTicksAsync(2, ct);
                    await client.UseItemAsync(Hand.Main, ct);
                    // The server answers within a tick or two on a quiet machine and several seconds on a busy one (the AubsCraft dev world while its viewer copies region files): the
                    // block turning to water, or the bucket turning empty, both say it poured.
                    water = await client.WaitUntilAsync(() => client.World.GetBlock(cx, gy - 1, cz)?.Name == "minecraft:water", 100, ct);
                }
            }
        }

        // Row by row, back and forth, so the walk is short.
        var cells = new List<(int X, int Z)>();
        for (var dz = 0; dz < size; dz++)
            for (var i = 0; i < size; i++) cells.Add((site.X + (dz % 2 == 0 ? i : size - 1 - i), site.Z + dz));
        int planted = 0, skipped = 0;
        foreach (var (x, z) in cells)
        {
            if (ct.IsCancellationRequested || client.Inventory.Count(choice.Seed) == 0) break;
            if (water && x == cx && z == cz) continue;
            if (!Tillable(x, gy - 1, z)) { skipped++; continue; }
            // Tall grass and flowers on top stop a hoe: break them first (instant).
            var above = client.World.GetState(x, gy, z);
            if (above > 0 && !D.IsAir(above) && (D.StateFlags[above] & GameData.FlagReplaceable) != 0)
            {
                await client.GoToAsync(new GoalReachBlock(x, gy, z, 3.5), new PathOptions { MaxNodes = 5_000 }, ct);
                await client.DigAsync(x, gy, z, ct);
            }
            if (client.DistanceToBlock(x, gy - 1, z) > client.BlockReach - 0.8)
                await client.GoToAsync(new GoalReachBlock(x, gy - 1, z, 3.5), new PathOptions { MaxNodes = 5_000 }, ct);
            if (!await client.EquipAsync(IsHoe, ct)) return (false, "I couldn't get the hoe into my hand.");
            await client.UseOnBlockAsync(x, gy - 1, z, BlockFace.Up, Hand.Main, null, ct);
            if (!await client.WaitUntilAsync(() => client.World.GetBlock(x, gy - 1, z)?.Name == "minecraft:farmland", 20, ct)) { skipped++; continue; }
            if (!await client.EquipAsync(choice.Seed, ct)) break;
            await client.UseOnBlockAsync(x, gy - 1, z, BlockFace.Up, Hand.Main, null, ct);
            if (await client.WaitUntilAsync(() => client.World.GetBlock(x, gy, z) is { } b && b.Name == choice.Block, 20, ct)) planted++;
            else skipped++;
        }

        if (planted == 0) return (false, "I couldn't plant anything there.");
        var wet = water ? $" with water in the middle" : " (I have no water bucket, so the soil may dry out: bring me one and I will make it a proper field)";
        return (true, $"I planted a {size} by {size} field of {cropName}{wet}: {planted} planted" + (skipped > 0 ? $", {skipped} spots I couldn't use." : "."));
    }

    // ---------------------------------------------------------------- animals

    /// <summary>What each kind of farm animal eats to breed (any of the foods works).</summary>
    private static readonly Dictionary<string, string[]> BreedingFood = new()
    {
        ["minecraft:cow"] = ["minecraft:wheat"],
        ["minecraft:mooshroom"] = ["minecraft:wheat"],
        ["minecraft:sheep"] = ["minecraft:wheat"],
        ["minecraft:goat"] = ["minecraft:wheat"],
        ["minecraft:pig"] = ["minecraft:carrot", "minecraft:potato", "minecraft:beetroot"],
        ["minecraft:chicken"] = ["minecraft:wheat_seeds", "minecraft:beetroot_seeds", "minecraft:melon_seeds", "minecraft:pumpkin_seeds", "minecraft:torchflower_seeds"],
        ["minecraft:rabbit"] = ["minecraft:carrot", "minecraft:golden_carrot", "minecraft:dandelion"],
    };

    /// <summary>A baby: the ageable-mob flag at metadata index 16.</summary>
    private static bool IsBaby(Entity e) => e.Metadata.TryGetValue(16, out var v) && v is true;

    /// <summary>A sheared sheep: bit 0x10 of the sheep's flags (metadata index 17).</summary>
    private static bool IsSheared(Entity e) => e.Metadata.TryGetValue(17, out var v) && v is byte or sbyte or int && (Convert.ToInt32(v) & 0x10) != 0;

    private string? AnimalKind(string? words)
    {
        var w = words?.Trim().ToLowerInvariant().Replace(' ', '_');
        if (string.IsNullOrEmpty(w)) return null;
        w = w.TrimEnd('s');
        if (w is "cow" or "cattle") return "minecraft:cow";
        if (w is "chicken" or "hen") return "minecraft:chicken";
        if (w is "mushroom_cow") return "minecraft:mooshroom";
        return BreedingFood.Keys.FirstOrDefault(k => k.Bare() == w) ?? (D.Entity("minecraft:" + w) != null ? "minecraft:" + w : null);
    }

    /// <summary>Walks up to an animal (they wander, so in short hops) until it is within arm's reach, or it has gone.</summary>
    private async Task<bool> ApproachAsync(Entity target, double within, CancellationToken ct)
    {
        for (var hops = 0; hops < 25 && !ct.IsCancellationRequested; hops++)
        {
            if (!client.Entities.TryGetValue(target.Id, out var now)) return false;
            if (now.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z) <= within) return true;
            using var hop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hop.CancelAfter(TimeSpan.FromSeconds(2));
            try { await client.GoToAsync(Goal.Near(now.X, now.Y, now.Z, Math.Max(1.5, within - 1.0)), new PathOptions { MaxNodes = 8_000 }, hop.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        // Behind a fence we cannot get closer, but an animal inside a pen can still be fed across it: the server lets us interact up to 3 blocks of the animal's box (about 3.2 to its middle) (seen 2026-10-15 on the AubsCraft dev world).
        return client.Entities.TryGetValue(target.Id, out var last) && last.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z) <= Math.Max(within, 3.2);
    }

    private List<Entity> AnimalsNear(string kind, bool adultsOnly)
    {
        var (x, y, z) = client.Position;
        return client.Entities.Values.Where(e => e.Name == kind && (!adultsOnly || !IsBaby(e)) && e.DistanceTo(x, y, z) < 24).OrderBy(e => e.DistanceTo(x, y, z)).ToList();
    }

    /// <summary>
    /// Breeds two adults of a kind of farm animal by feeding both the food they like (wheat for cows and sheep, carrots for pigs, seeds for chickens), then waits to see the baby arrive.
    /// Repeats for more pairs while there are adults and food (`pairs`).
    /// </summary>
    public async Task<(bool, string)> BreedAnimalsAsync(string? animal, int pairs, CancellationToken ct)
    {
        var kind = AnimalKind(animal);
        if (kind == null || !BreedingFood.TryGetValue(kind, out var foods))
            return (false, $"I don't know how to breed {animal ?? "that"}. I can breed cows, sheep, pigs, chickens, rabbits and goats.");
        var foodNames = string.Join(" or ", foods.Select(Plain));
        pairs = Math.Clamp(pairs <= 0 ? 1 : pairs, 1, 5);
        var babies = 0;
        var fedAlready = new HashSet<int>();
        for (var pair = 0; pair < pairs && !ct.IsCancellationRequested; pair++)
        {
            var adults = AnimalsNear(kind, adultsOnly: true).Where(a => !fedAlready.Contains(a.Id)).ToList();
            if (adults.Count < 2) return babies > 0 ? (true, $"{Report(babies)} I need two more adult {Plain(kind)}s close by for another pair.") : (false, $"I need two adult {Plain(kind)}s close by to breed them, and I only see {adults.Count}.");
            var food = foods.FirstOrDefault(f => client.Inventory.Count(f) >= 2);
            if (food == null) return babies > 0 ? (true, $"{Report(babies)} I ran out of {foodNames}.") : (false, $"I need two {foodNames} to breed {Plain(kind)}s and I only have {foods.Sum(f => client.Inventory.Count(f))}.");

            var first = adults[0];
            var second = adults.Where(a => a.Id != first.Id).OrderBy(a => a.DistanceTo(first.X, first.Y, first.Z)).First();
            var before = client.Entities.Values.Where(e => e.Name == kind && IsBaby(e)).Select(e => e.Id).ToHashSet();
            foreach (var parent in new[] { first, second })
            {
                if (!await ApproachAsync(parent, 2.6, ct)) return babies > 0 ? (true, Report(babies)) : (false, $"I couldn't catch up with a {Plain(kind)}.");
                if (!await client.EquipAsync(s => s.Name == food, ct)) return (false, $"I couldn't get the {Plain(food)} into my hand.");
                client.LookAt(parent.X, parent.Y + parent.Type.Height * 0.5, parent.Z);
                await client.InteractAsync(client.Entities.GetValueOrDefault(parent.Id) ?? parent, Hand.Main, ct);
                fedAlready.Add(parent.Id);
                await client.WaitTicksAsync(6, ct);
            }
            // They walk to each other; a baby appears a few seconds after they meet.
            var arrived = await client.WaitUntilAsync(() => client.Entities.Values.Any(e => e.Name == kind && IsBaby(e) && !before.Contains(e.Id)), 20 * 30, ct);
            if (!arrived) return babies > 0 ? (true, $"{Report(babies)} The next pair didn't breed yet.") : (false, $"I fed two {Plain(kind)}s but no baby came yet. They may need to be closer together, or they bred recently.");
            babies++;
        }
        return (true, Report(babies));

        string Report(int n) => $"I bred {Plain(kind)}s: {n} {(n == 1 ? "baby" : "babies")} born.";
    }

    /// <summary>Shears every sheep within reach of a walk (up to `count`, all if 0) that still has its wool, and picks the wool up.</summary>
    public async Task<(bool, string)> ShearSheepAsync(int count, CancellationToken ct)
    {
        if (Have(s => s.Name == "minecraft:shears") == 0) return (false, "I need shears to shear sheep. (They are two iron ingots.)");
        var sheared = 0;
        var wool = Have(s => s.Name.EndsWith("_wool"));
        var done = new HashSet<int>();
        while (!ct.IsCancellationRequested && (count <= 0 || sheared < count))
        {
            var sheep = AnimalsNear("minecraft:sheep", adultsOnly: true).FirstOrDefault(s => !IsSheared(s) && !done.Contains(s.Id));
            if (sheep == null) break;
            done.Add(sheep.Id);
            if (!await ApproachAsync(sheep, 2.6, ct)) continue;
            if (!await client.EquipAsync(s => s.Name == "minecraft:shears", ct)) return (false, "I couldn't get the shears into my hand.");
            client.LookAt(sheep.X, sheep.Y + sheep.Type.Height * 0.5, sheep.Z);
            await client.InteractAsync(client.Entities.GetValueOrDefault(sheep.Id) ?? sheep, Hand.Main, ct);
            if (await client.WaitUntilAsync(() => client.Entities.TryGetValue(sheep.Id, out var e) && IsSheared(e), 30, ct))
            {
                sheared++;
                await client.WaitTicksAsync(8, ct);
                await PickUpNearbyAsync((int)Math.Floor(sheep.X), (int)Math.Floor(sheep.Y), (int)Math.Floor(sheep.Z), ct);
            }
        }
        if (sheared == 0) return (false, "I don't see any sheep with wool on near me.");
        var got = Have(s => s.Name.EndsWith("_wool")) - wool;
        return (true, $"I sheared {sheared} sheep and got {got} wool.");
    }

    /// <summary>Milks the nearest adult cow with a bucket.</summary>
    public async Task<(bool, string)> MilkCowAsync(CancellationToken ct)
    {
        if (Have(s => s.Name == "minecraft:bucket") == 0) return (false, "I need an empty bucket to milk a cow.");
        var cow = AnimalsNear("minecraft:cow", adultsOnly: true).FirstOrDefault() ?? AnimalsNear("minecraft:mooshroom", adultsOnly: true).FirstOrDefault();
        if (cow == null) return (false, "I don't see a cow to milk.");
        if (!await ApproachAsync(cow, 2.6, ct)) return (false, "I couldn't catch up with the cow.");
        if (!await client.EquipAsync(s => s.Name == "minecraft:bucket", ct)) return (false, "I couldn't get the bucket into my hand.");
        var before = Have(s => s.Name == "minecraft:milk_bucket");
        client.LookAt(cow.X, cow.Y + cow.Type.Height * 0.5, cow.Z);
        await client.InteractAsync(client.Entities.GetValueOrDefault(cow.Id) ?? cow, Hand.Main, ct);
        return await client.WaitUntilAsync(() => Have(s => s.Name == "minecraft:milk_bucket") > before, 30, ct)
            ? (true, "I milked the cow: I have a bucket of milk.")
            : (false, "The cow didn't give milk.");
    }

    // ---------------------------------------------------------------- lighting

    /// <summary>Torches (and the like) that already light a spot.</summary>
    private static bool IsLightSource(string block) =>
        block.EndsWith("torch") || block is "minecraft:lantern" or "minecraft:soul_lantern" or "minecraft:glowstone" or "minecraft:sea_lantern" or "minecraft:jack_o_lantern"
        or "minecraft:shroomlight" or "minecraft:campfire" or "minecraft:soul_campfire" or "minecraft:beacon" or "minecraft:end_rod" or "minecraft:froglight";

    private bool LitNearby(int x, int y, int z, int within)
    {
        for (var dx = -within; dx <= within; dx++)
        for (var dz = -within; dz <= within; dz++)
        for (var dy = -2; dy <= 3; dy++)
            if (client.World.GetBlock(x + dx, y + dy, z + dz) is { } b && IsLightSource(b.Name)) return true;
        return false;
    }

    /// <summary>
    /// Puts torches on the ground around a player (or here) so nothing can spawn in the dark: one every 6 blocks across a square of the given radius (default 10), skipping every spot that
    /// already has a torch or other light within 4 blocks. Makes torches from coal and sticks if there are none and the means to.
    /// </summary>
    public async Task<(bool, string)> LightUpAsync(int radius, string? nearPlayer, CancellationToken ct)
    {
        radius = Math.Clamp(radius <= 0 ? 10 : radius, 4, 24);
        if (Have(s => s.Name == "minecraft:torch") == 0)
        {
            var (made, why) = await client.CraftItemAsync("minecraft:torch", 8, ct);
            if (!made || Have(s => s.Name == "minecraft:torch") == 0) return (false, $"I have no torches and I couldn't make any: {why}");
        }
        var player = FindPlayer(nearPlayer);
        var (ox, oy, oz) = player != null ? (player.X, player.Y, player.Z) : client.Position;
        int cx = (int)Math.Floor(ox), cz = (int)Math.Floor(oz), cy = (int)Math.Floor(oy + 1e-6);

        const int spacing = 6;
        var spots = new List<(int X, int Y, int Z)>();
        var already = 0;
        // The grid belongs to the WORLD (multiples of 6), not to where we happen to stand: asking again from somewhere else lines up with the torches already there.
        int Floor6(int v) => (int)Math.Floor(v / (double)spacing) * spacing;
        for (var x = Floor6(cx - radius - spacing / 2); x <= cx + radius + spacing / 2; x += spacing)
        for (var z = Floor6(cz - radius - spacing / 2); z <= cz + radius + spacing / 2; z += spacing)
        {
            // Grid points just outside the circle count too (half a spacing): the ground at its edge has to be lit by a torch beyond it.
            if ((x - cx) * (x - cx) + (z - cz) * (z - cz) > (radius + spacing / 2) * (radius + spacing / 2)) continue;
            if (!client.World.IsLoaded(x, z) || GroundLevel(x, z, cy) is not int gy) continue;
            if (LitNearby(x, gy, z, 4)) { already++; continue; }
            spots.Add((x, gy, z));
        }
        if (spots.Count == 0) return (true, already > 0 ? "It is already well lit here." : "I couldn't find any ground to put torches on around here.");

        // Nearest first, then onward from each: a short walk.
        var ordered = new List<(int X, int Y, int Z)>();
        var at = (X: (int)Math.Floor(client.Position.X), Z: (int)Math.Floor(client.Position.Z));
        while (spots.Count > 0)
        {
            var next = spots.OrderBy(s => Math.Abs(s.X - at.X) + Math.Abs(s.Z - at.Z)).First();
            ordered.Add(next);
            spots.Remove(next);
            at = (next.X, next.Z);
        }
        var plan = ordered.Select(p => new Placement(p.X, p.Y, p.Z, s => s.Name == "minecraft:torch", "torches")).ToList();
        var (placed, failed, missing) = await client.BuildAsync(plan, ct);
        if (missing != null) return (placed > 0, $"I ran out of torches after putting up {placed}. Give me more (coal and sticks make them) and ask again.");
        if (placed == 0) return (false, "I couldn't put any torches down there.");
        return (true, $"I put up {placed} torch{(placed == 1 ? "" : "es")} around here, about {spacing} blocks apart, so nothing can spawn in the dark" + (failed > 0 ? $" ({failed} spots wouldn't take one)." : "."));
    }

    // ---------------------------------------------------------------- shooting

    private Func<Entity, bool> MobMatcher(string what)
    {
        var w = what.Trim().ToLowerInvariant();
        return w is "monster" or "monsters" or "mobs" or "hostile" or "enemies" or "bad guys" or ""
            ? e => Hostile.Contains(e.Name)
            : e => e.Name.Bare() == w.TrimEnd('s') || e.Name.Bare() == w;
    }

    /// <summary>
    /// Shoots mobs with a bow from a distance: draws for a full second (full power, 3 blocks a tick), points where the arrow will be at the target's height when it arrives (leading it if it
    /// walks), releases, and goes again until it is gone. Gets closer when it is too far for an arrow, and fights it by hand when it comes too close for a bow.
    /// </summary>
    public async Task<(bool, string)> ShootAsync(string what, CancellationToken ct)
    {
        if (Have(s => s.Name == "minecraft:bow") == 0) return (false, "I need a bow to shoot.");
        var matches = MobMatcher(what);
        int shots = 0, kills = 0;
        var down = new HashSet<int>();                                  // a mob stays listed for its death animation: once it is down it is not a target and not counted again
        while (!ct.IsCancellationRequested)
        {
            if (client.Health < 6) return (kills > 0, $"I had to stop shooting, I'm hurt (health {client.Health / 2:0} hearts). {Report(shots, kills)}");
            if (Have(s => s.Name == "minecraft:arrow") == 0) return (kills > 0, $"I ran out of arrows. {Report(shots, kills)}");
            var (px, py, pz) = client.Position;
            var target = client.Entities.Values.Where(e => !e.IsPlayer && matches(e) && !down.Contains(e.Id) && e.Health is not <= 0 && e.DistanceTo(px, py, pz) < 40)
                .OrderBy(e => e.DistanceTo(px, py, pz)).FirstOrDefault();
            if (target == null) break;
            var distance = target.DistanceTo(px, py, pz);
            if (distance < 5)
            {
                // Too close to draw: by hand.
                if (await FightAsync(target.Id, null, ct)) { kills++; down.Add(target.Id); }
                continue;
            }
            if (distance > 32)
            {
                await client.GoToAsync(Goal.Near(target.X, target.Y, target.Z, 24), new PathOptions { MaxNodes = 8_000 }, ct);
                continue;
            }
            if (!await client.EquipAsync(s => s.Name == "minecraft:bow", ct)) return (false, "I couldn't get the bow into my hand.");

            // Draw, watching the target for ten ticks to know how it is moving, aiming again every tick, release at full draw.
            var id = target.Id;
            await client.UseItemAsync(Hand.Main, ct);
            (double X, double Z)? early = null, late = null;
            var velocity = (X: 0.0, Z: 0.0);
            for (var tick = 0; tick < 21 && !ct.IsCancellationRequested; tick++)
            {
                if (!client.Entities.TryGetValue(id, out var t)) break;
                if (tick == 6) early = (t.X, t.Z);
                if (tick == 16) { late = (t.X, t.Z); velocity = ((late.Value.X - early!.Value.X) / 10, (late.Value.Z - early.Value.Z) / 10); }
                var (ex, ey, ez) = client.Position;
                var aimAt = Ballistics.AimAt(ex, ey + 1.62, ez, t.X, t.Y + t.Type.Height * 0.65, t.Z, velocity.X, 0, velocity.Z);
                if (aimAt is { } a) client.Look((float)a.Yaw, (float)a.Pitch);
                else client.LookAt(t.X, t.Y + t.Type.Height * 0.65, t.Z);
                await client.WaitTicksAsync(1, ct);
            }
            await client.ReleaseUseItemAsync(ct);
            shots++;
            await client.WaitTicksAsync(14, ct);                       // the flight, and the mob's death
            if (!client.Entities.ContainsKey(id) || client.Entities.TryGetValue(id, out var after) && after.Health is <= 0) { kills++; down.Add(id); }
        }
        if (shots == 0 && kills == 0) return (false, $"I don't see any {what} to shoot at.");
        return (true, Report(shots, kills));

        string Report(int s, int k) => $"I shot {s} arrow{(s == 1 ? "" : "s")} and {(k == 0 ? "none of the " + what + " are down yet" : $"brought down {k} {what}")}. I have {Have(a => a.Name == "minecraft:arrow")} arrows left.";
    }

    // ---------------------------------------------------------------- exploring

    /// <summary>Blocks worth telling friends about, by what to call them.</summary>
    private static readonly (string Block, string Says)[] NotableBlocks =
    [
        ("minecraft:bell", "a village"), ("minecraft:diamond_ore", "diamonds"), ("minecraft:deepslate_diamond_ore", "diamonds"),
        ("minecraft:emerald_ore", "emeralds"), ("minecraft:deepslate_emerald_ore", "emeralds"), ("minecraft:gold_ore", "gold"),
        ("minecraft:deepslate_gold_ore", "gold"), ("minecraft:spawner", "a monster spawner"), ("minecraft:chest", "a chest"),
        ("minecraft:lava", "lava"), ("minecraft:obsidian", "obsidian"), ("minecraft:pumpkin", "pumpkins"), ("minecraft:melon", "melons"),
        ("minecraft:sweet_berry_bush", "berry bushes"), ("minecraft:sugar_cane", "sugar cane"), ("minecraft:bee_nest", "a bee nest"),
    ];

    private static readonly HashSet<string> NotableMobs =
    [
        "minecraft:villager", "minecraft:cow", "minecraft:sheep", "minecraft:pig", "minecraft:chicken", "minecraft:horse",
        "minecraft:wolf", "minecraft:cat", "minecraft:fox", "minecraft:panda", "minecraft:axolotl", "minecraft:rabbit",
        "minecraft:llama", "minecraft:turtle", "minecraft:parrot", "minecraft:goat", "minecraft:frog", "minecraft:camel", "minecraft:sniffer",
    ];

    /// <summary>
    /// Walks `distance` blocks one way (north/south/east/west, or any way that is open), noting what it finds:
    /// villages, animals, rare ores, chests and spawners. Remembers a village as a place, then comes back.
    /// </summary>
    public async Task<(bool, string)> ExploreAsync(string? direction, int distance, WorldMemory? memory, CancellationToken ct)
    {
        var (sx, sy, sz) = client.Position;
        var start = ((int)Math.Floor(sx), (int)Math.Floor(sy), (int)Math.Floor(sz));
        var dir = (direction ?? "").Trim().ToLowerInvariant();
        var (dx, dz) = dir switch
        {
            "north" => (0, -1), "south" => (0, 1), "east" => (1, 0), "west" => (-1, 0),
            _ => new[] { (0, -1), (0, 1), (1, 0), (-1, 0) }[Random.Shared.Next(4)],
        };
        var way = dx == 0 ? (dz < 0 ? "north" : "south") : (dx > 0 ? "east" : "west");
        var stateKind = new Dictionary<int, string>();
        foreach (var (block, says) in NotableBlocks)
            if (D.Block(block) is { } b)
                for (var st = 0; st < D.StateCount; st++)
                    if (D.StateBlock[st] == b.Id) stateKind[st] = says;
        var seenBlocks = new Dictionary<string, (int X, int Y, int Z)>();
        var villages = new List<(int X, int Y, int Z)>(); // each bell 64+ blocks from the others is another village
        var seenMobs = new Dictionary<string, HashSet<int>>();
        void Look()
        {
            foreach (var (x, y, z) in client.World.FindBlocks(stateKind.ContainsKey, client.Position, 48, 4096))
            {
                var kind = stateKind[client.World.GetState(x, y, z)];
                if (kind == "a village") { if (villages.All(v => (v.X - x) * (v.X - x) + (v.Z - z) * (v.Z - z) > 64 * 64)) villages.Add((x, y, z)); }
                else if (!seenBlocks.ContainsKey(kind)) seenBlocks[kind] = (x, y, z);
            }
            foreach (var e in client.Entities.Values.Where(e => NotableMobs.Contains(e.Name)))
                (seenMobs.TryGetValue(e.Name, out var ids) ? ids : seenMobs[e.Name] = []).Add(e.Id);
        }
        Look();
        var before = seenBlocks.Keys.ToHashSet(); // what was already in view at the start is not news
        var villagesBefore = villages.Count;
        var beforeMobs = seenMobs.ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet());
        var target = (X: start.Item1 + dx * distance, Z: start.Item3 + dz * distance);
        var far = 0.0;
        for (var hop = 0; hop < distance / 8 + 4; hop++)
        {
            ct.ThrowIfCancellationRequested();
            var (px, _, pz) = client.Position;
            far = Math.Abs((px - sx) * dx + (pz - sz) * dz);
            if (far >= distance - 2) break;
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(TimeSpan.FromSeconds(4));
            try { await client.GoToAsync(Goal.XZ(target.X, target.Z), new PathOptions { CanDig = false, MaxNodes = 20_000 }, step.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            Look();
            var (nx, _, nz) = client.Position;
            if (Math.Abs((nx - sx) * dx + (nz - sz) * dz) <= far + 1 && hop > 2) break; // stuck: report what we have
        }
        var (ex, _, ez) = client.Position;
        far = Math.Abs((ex - sx) * dx + (ez - sz) * dz);

        var found = new List<string>();
        foreach (var v in villages.Skip(villagesBefore))
        {
            found.Add($"a village around {v.X} {v.Z}");
            // Remembered by name: the first is "village", more are "village 2"...
            var n = 1;
            while (memory?.Places.Any(p => p.Name == (n == 1 ? "village" : $"village {n}") && (p.X - v.X) * (p.X - v.X) + (p.Z - v.Z) * (p.Z - v.Z) > 64 * 64) == true) n++;
            memory?.SetPlace(n == 1 ? "village" : $"village {n}", client.World.Dimension, v.X, v.Y, v.Z, null);
        }
        foreach (var (kind, _) in seenBlocks.Where(kv => !before.Contains(kv.Key))) found.Add(kind);
        foreach (var (mob, ids) in seenMobs)
        {
            var count = ids.Count - (beforeMobs.TryGetValue(mob, out var b) ? b.Count : 0);
            if (count > 0) found.Add($"{count} {mob.Bare().Replace('_', ' ')}{(count > 1 && !mob.EndsWith("sheep") ? "s" : "")}");
        }
        var summary = $"I explored {far:0} blocks {way}" + (found.Count > 0 ? $" and found {string.Join(", ", found)}." : ", but didn't find anything special.");
        // Back to where we started (where our friends are).
        await TravelAsync(start.Item1, start.Item2, start.Item3, "where I started", ct);
        return (true, summary);
    }

    // ---------------------------------------------------------------- creepers

    /// <summary>
    /// A creeper about to blow up within `within` blocks: its fuse is burning (swell direction, entity data 16 above 0)
    /// or it was lit with flint and steel (ignited, 18). Ids from the server jar's own Creeper class.
    /// </summary>
    public Entity? LitCreeper(double within = 7)
    {
        var (x, y, z) = client.Position;
        return client.Entities.Values
            .Where(e => e.Name == "minecraft:creeper" && e.DistanceTo(x, y, z) < within
                && ((e.Metadata.TryGetValue(16, out var dir) && Convert.ToInt32(dir) > 0) || (e.Metadata.TryGetValue(18, out var lit) && lit is true)))
            .OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
    }

    /// <summary>
    /// Sprints away from a creeper until 8 blocks clear: a creeper's fuse stops when its target is more than 7
    /// blocks away (SwellGoal), and the explosion (power 3) barely reaches that far.
    /// </summary>
    public async Task<(bool, string)> EscapeCreeperAsync(int creeperId, CancellationToken ct)
    {
        var health = client.Health;
        for (var hop = 0; hop < 8; hop++)
        {
            if (!client.Entities.TryGetValue(creeperId, out var c))
                return client.Health < health ? (false, "A creeper blew up next to me!") : (true, "A creeper was about to blow up, so I ran.");
            var (px, py, pz) = client.Position;
            if (Math.Sqrt((px - c.X) * (px - c.X) + (pz - c.Z) * (pz - c.Z)) >= 8)
                return (true, "A creeper was about to blow up, so I ran away from it.");
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(TimeSpan.FromSeconds(1)); // re-aim as it follows
            try { await client.GoToAsync(Goal.AwayFrom(c.X, c.Z, 9), new PathOptions { Sprint = true, MaxNodes = 3_000 }, step.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return (false, "A creeper is chasing me!");
    }

    /// <summary>Runs 12 blocks straight away from a mob (what a careful character does when one hurts it).</summary>
    public async Task<(bool, string)> FleeFromAsync(Entity attacker, CancellationToken ct)
    {
        var (px, py, pz) = client.Position;
        double dx = px - attacker.X, dz = pz - attacker.Z;
        var len = Math.Max(0.1, Math.Sqrt(dx * dx + dz * dz));
        var ok = await client.GoToAsync(Goal.Near(px + dx / len * 12, py, pz + dz / len * 12, 3), null, ct);
        return (ok, ok ? "I got away from the monster." : "I couldn't get away from the monster!");
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
        return (ok, ok ? $"Yum, I ate {name.Bare().Replace('_', ' ')}." : "I couldn't eat right now.");
    }
}
