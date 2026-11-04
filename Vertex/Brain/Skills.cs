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
public sealed partial class Skills(MinecraftClient client)
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

    /// <summary>
    /// Hostile mobs that fly: walking away from them gets nowhere (a phantom swoops at whatever he does). Live 2026-11-03, daytime: 3 to 8 phantoms circled him for half an hour, the "step away from
    /// monsters" reflex fired every 30 seconds, and every one of those jobs counted as busy, so he never settled into a project. They are not threats to leave on foot.
    /// </summary>
    public static readonly HashSet<string> Fliers = ["minecraft:phantom", "minecraft:vex", "minecraft:ghast", "minecraft:blaze"];

    /// <summary>A hostile mob he could walk away from: hostile, and not one that flies.</summary>
    public static bool IsThreatOnFoot(string entityName) => Hostile.Contains(entityName) && !Fliers.Contains(entityName);

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
        // What people call it is not always what it is called in the inventory: wood is logs ("oak_log"), stone is cobblestone once mined.
        var alias = w is "wood" or "log" or "tree" ? "log" : w is "stone" or "rock" ? "cobblestone" : null;
        if (alias != null && stacks.FirstOrDefault(s => Simplify(s.Name.Bare()).Contains(alias)) is { } aliased) return aliased;
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
        var digFailures = 0;
        var fetchedScaffold = false;
        (int X, int Y, int Z)? ground = null;
        var tried = new HashSet<(int, int, int)>();
        var nextToLava = 0;
        var legs = 0;
        double walked = 0;
        while (broken < count && failures < 6 && !ct.IsCancellationRequested)
        {
            // 64 blocks sideways at ANY depth (a sphere of 48 never reached the diamonds 70 blocks down: "I couldn't find any diamond ore", in two seconds, 2026-11-03).
            // Near his own level first: wood or stone under the ground is a long dig away, and a tree 30 blocks off is a short walk. (The nearest first otherwise: 3D distance puts a log in a cave under him ahead of a tree in plain sight.)
            var feetY = client.Position.Y;
            var found = client.World.FindBlocksTall(Wanted, client.Position, 64, 256).Where(p => !tried.Contains(p))
                .OrderBy(p => Math.Abs(p.Y - feetY) > 8 ? 1 : 0).Take(64).ToList();
            // Opening a block with lava beside it (or lava above it, which falls in) lets the lava out onto whoever is digging: leave it (found 2026-10-15: a diamond ringed with lava took 11 of 20 health).
            var targets = found.Where(p => !LavaAround(p.Item1, p.Item2, p.Item3)).ToList();
            nextToLava += found.Count - targets.Count;
            foreach (var skipped in found.Except(targets)) tried.Add(skipped);
            if (targets.Count == 0)
            {
                // None in what he has seen: go and look somewhere else (a leg of 48 blocks, a different way each time), then look again. Not "I couldn't find any" while there is more world to walk.
                if (found.Count > 0 || legs >= 4) break;
                legs++;
                var (lx, _, lz) = client.Position;
                var (dx, dz) = new[] { (0, -1), (1, 0), (0, 1), (-1, 0) }[(legs + (int)(Math.Abs(lx) + Math.Abs(lz))) % 4];
                var before = client.Position;
                for (var hop = 0; hop < 8; hop++)
                {
                    var (px, _, pz) = client.Position;
                    if (Math.Abs((px - lx) * dx + (pz - lz) * dz) >= 46) break;
                    using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    step.CancelAfter(TimeSpan.FromSeconds(6));
                    try { await client.GoToAsync(Goal.XZ((int)lx + dx * 48, (int)lz + dz * 48), new PathOptions { CanDig = true, MaxNodes = 20_000 }, step.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    var (nx, _, nz) = client.Position;
                    if (Math.Abs((nx - px) * dx + (nz - pz) * dz) < 1) break;       // not getting anywhere that way: try the next leg
                }
                var after = client.Position;
                walked += Math.Sqrt((after.X - before.X) * (after.X - before.X) + (after.Z - before.Z) * (after.Z - before.Z));
                continue;
            }
            var (bx, by, bz) = targets[0];
            tried.Add((bx, by, bz));
            Trace?.Invoke($"collecting {what}: going for the one at {bx} {by} {bz} ({broken} of {count} so far)");
            // With blocks to stand on he can build his way up to what is out of reach.
            var scaffold = Have(s => client.ThrowawayBlocks.Contains(s.Name));
            if (!await client.GoToAsync(new GoalReachBlock(bx, by, bz, 4.0), new PathOptions { CanDig = true, CanPlace = scaffold > 0, MaxNodes = 30_000 }, ct))
            {
                // Too high: go and get something to stand on and try again. Live 2026-11-04 the trees round his respawn spot had no trunk left below y 70, only crowns six blocks over his head: "I could see wood but I couldn't
                // get to any of it", for days. A player stacks a few blocks.
                if (by - client.FeetBlock.Y >= 4 && scaffold < 6 && !fetchedScaffold)
                {
                    fetchedScaffold = true;
                    ground = client.FeetBlock;
                    Trace?.Invoke($"collecting {what}: the one at {bx} {by} {bz} is too high, getting blocks to climb on");
                    foreach (var block in new[] { "sand", "dirt", "stone" })
                    {
                        if (block == what || Have(s => client.ThrowawayBlocks.Contains(s.Name)) >= 8) break;
                        await CollectAsync(block, 8, ct);
                    }
                    if (Have(s => client.ThrowawayBlocks.Contains(s.Name)) > scaffold) { tried.Remove((bx, by, bz)); continue; }
                }
                failures++;
                var (fx, fy, fz) = client.Position;
                Trace?.Invoke($"collecting {what}: no way to the one at {bx} {by} {bz}, I got to {fx:0} {fy:0} {fz:0} (try {failures})");
                // The rest of that tree or vein is no easier: six tries on one tree that cannot be reached must not end the whole search.
                foreach (var near in targets.Where(p => Math.Abs(p.Item1 - bx) <= 6 && Math.Abs(p.Item3 - bz) <= 6)) tried.Add(near);
                continue;
            }
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
            else
            {
                failures++;
                digFailures++;
                Trace?.Invoke($"collecting {what}: at the one at {bx} {by} {bz} the dig did not work (distance {client.DistanceToBlock(bx, by, bz):0.#}, now {client.World.GetBlock(bx, by, bz)?.Name ?? "unknown"}; try {failures})");
            }
        }
        // Back down from whatever he built to reach it: on top of a pillar there is nothing beside him to put a crafting table against, and nothing he does next starts up there.
        if (ground is { } back && client.FeetBlock.Y > back.Y + 1 && !ct.IsCancellationRequested)
            await client.GoToAsync(Goal.Near(back.X + 0.5, back.Y, back.Z + 0.5, 2), new PathOptions { CanDig = true, MaxDrop = 3, MaxNodes = 8_000 }, ct);
        var have = InventorySummary();
        var lavaNote = nextToLava > 0 ? $" {nextToLava} {(nextToLava == 1 ? "was" : "were")} next to lava, so I left {(nextToLava == 1 ? "it" : "them")} alone." : "";
        return broken > 0
            ? (broken >= count, $"I mined {broken} {what}{(broken < count ? $" (wanted {count}, couldn't find more nearby)" : "")}. I have: {have}.{lavaNote}")
            : (false, nextToLava > 0 ? $"I won't mine that {what}: it is next to lava, which would pour out onto me."
                : digFailures >= 3 ? $"I got to {what} but breaking it didn't work ({digFailures} of {failures} tries)."
                : failures >= 6 ? $"I could see {what} but I couldn't get to any of it ({failures} tries: something is in the way that I can't dig or cross)."
                : legs > 0 ? $"I looked for {what}: 64 blocks around me at every depth, and I walked about {walked:0} blocks in {legs} different directions to look further, but I haven't found any."
                : $"I couldn't find any {what} I could reach nearby.");
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
        var target = FindPlayer(playerName);
        if (target == null) return (false, $"I can't see {playerName ?? "anyone"} to give it to.");
        // "Bring me ten wood" with none in the pockets: go and get it first (blocks only: wood, stone, ores, sand...), then bring it.
        var haveNow = stack == null ? 0 : client.Inventory.Count(stack.Name);
        if (stack == null || (count > 0 && haveNow < count))
        {
            var (got, fetchText) = await CollectAsync(item, count > 0 ? count - haveNow : 8, ct);
            stack = FindItem(item);
            if (stack == null) return (false, $"I don't have any {item}, and I couldn't get any: {fetchText}");
            target = FindPlayer(playerName) ?? target;
        }
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
        await EquipShieldAsync(ct);       // a shield, when we carry one, goes to the off hand for any fight
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

    /// <summary>Puts a shield from the inventory into the off hand (true when one is there afterwards). A shield blocks nearly all of what hits us from the front while it is raised.</summary>
    public async Task<bool> EquipShieldAsync(CancellationToken ct)
    {
        if (client.Inventory.OffHandItem.Name == "minecraft:shield") return true;
        var slot = client.Inventory.Find(s => s.Name == "minecraft:shield").FirstOrDefault(-1);
        if (slot < 0) return false;
        await client.ClickAsync(0, slot, 40, 2, ct);        // mode 2 (swap), button 40: the clicked slot swaps with the off hand
        await client.WaitUntilAsync(() => client.Inventory.OffHandItem.Name == "minecraft:shield", 40, ct);
        return client.Inventory.OffHandItem.Name == "minecraft:shield";
    }

    /// <summary>Raises the shield held in the off hand (it stays up until <see cref="ShieldDownAsync"/>); false when there is none.</summary>
    public async Task<bool> ShieldUpAsync(CancellationToken ct)
    {
        if (client.Inventory.OffHandItem.Name != "minecraft:shield") return false;
        await client.UseItemAsync(Hand.Off, ct);
        return true;
    }

    public Task ShieldDownAsync(CancellationToken ct) => client.ReleaseUseItemAsync(ct);

    /// <summary>
    /// Fights one mob until it is gone (true), we are hurt, or `keepGoing` says to stop chasing it (false).
    /// </summary>
    private async Task<bool> FightAsync(int id, Func<Entity, bool>? keepGoing, CancellationToken ct)
    {
        // With a shield in the off hand it is up between swings and down for each one (you cannot swing with it raised).
        var shield = client.Inventory.OffHandItem.Name == "minecraft:shield";
        var up = false;
        try
        {
            return await FightLoopAsync(id, keepGoing, shield, v => up = v, () => up, ct);
        }
        finally
        {
            if (up) await client.ReleaseUseItemAsync(CancellationToken.None);
        }
    }

    private async Task<bool> FightLoopAsync(int id, Func<Entity, bool>? keepGoing, bool shield, Action<bool> setUp, Func<bool> isUp, CancellationToken ct)
    {
        var stuck = 0;
        while (client.Entities.TryGetValue(id, out var t) && !ct.IsCancellationRequested)
        {
            if (client.Health < 6 || (keepGoing != null && !keepGoing(t))) return false;
            // A creeper whose fuse is burning (it is within 3 blocks of us): run until its fuse stops, then carry on. (The brain's reflex does the same; a fight run on its own needs it too.)
            // Hit it once first when it is in reach and the sword is ready (the blow knocks it back; the fuse is 1.5 s and a run of eight blocks takes less): hit and run, until it falls.
            if (LitCreeper(5) is { } lit)
            {
                if (lit.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z) < 2.9 && client.AttackReady)
                {
                    client.LookAt(lit.X, lit.Y + lit.Type.Height * 0.5, lit.Z);
                    await client.AttackAsync(lit, ct);
                }
                await EscapeCreeperAsync(lit.Id, ct);
                continue;
            }
            var d = t.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z);
            // The shield is up from the moment the mob is coming (hits land while we are still walking up to it) and down only for the swing itself.
            if (shield)
            {
                var swingNow = d <= 2.8 && (client.AttackReady || client.TicksUntilAttackReady <= 1);
                if (swingNow && isUp()) { await client.ReleaseUseItemAsync(ct); setUp(false); }
                else if (!swingNow && !isUp()) { await client.UseItemAsync(Hand.Off, ct); setUp(true); }
            }
            if (d > 2.8)
            {
                using var hop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                hop.CancelAfter(TimeSpan.FromSeconds(1));
                try { await client.GoToAsync(Goal.Near(t.X, t.Y, t.Z, 2.0), new PathOptions { CanDig = true, MaxNodes = 8_000 }, hop.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                // No way to it, or no nearer: a pause, and after eight such tries in a row he gives up on this one. Without this a mob he could not reach (a cow across rough ground, a spider over water)
                // made this loop spin about 20,000 times a second for five minutes (a test, 2026-11-03: a million log lines).
                var after = client.Entities.TryGetValue(id, out var now) ? now.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z) : 0;
                if (after > d - 0.3) { if (++stuck >= 8) return false; await client.WaitTicksAsync(4, ct); }
                else stuck = 0;
                continue;
            }
            client.LookAt(t.X, t.Y + t.Type.Height * 0.5, t.Z);
            // Jump as the charge is about to complete and hit on the way down: a critical hit (x1.5) at no cost
            // in attack rate. Not on creepers (stay mobile) or when up close to several mobs.
            if (!shield && client.OnGround && client.TicksUntilAttackReady <= 7 && t.Name != "minecraft:creeper" && d < 2.6)
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
        await EquipShieldAsync(ct);       // a shield, when we carry one, goes to the off hand for any fight
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
                // A creeper (it must reach us to hurt) or a skeleton (it shoots) is better dealt with from a distance, when we have the bow for it.
                var (gx, gy, gz) = client.Position;
                if (threat.DistanceTo(gx, gy, gz) > 6 && threat.Name is "minecraft:creeper" or "minecraft:skeleton" or "minecraft:stray" or "minecraft:bogged"
                    && Have(s => s.Name == "minecraft:bow") > 0 && Have(s => s.Name == "minecraft:arrow") > 0)
                {
                    using var volley = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    volley.CancelAfter(TimeSpan.FromSeconds(12));
                    try { var (_, shot) = await ShootAsync(threat.Name.Bare(), volley.Token); if (shot.Contains("brought down")) kills++; }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    await EquipWeaponAsync(ct);
                    continue;
                }
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
        // Guarding only ends by being stopped, and stopping is a cancellation like every other task's (TaskRunner expects the throw): when the token was cancelled between two turns of the
        // loop above, the loop just ended, and the task returned normally (seen once in a full suite run, 2026-10-16).
        ct.ThrowIfCancellationRequested();
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
        var beds = client.FindBeds(48);
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

    // ---------------------------------------------------------------- making a site (the whole world can be dug and built on)

    /// <summary>What the top of one column of the world is like for building on it: the feet level over the natural ground, where water stands over it, and lava.</summary>
    private readonly record struct SiteColumn(int Top, int WaterTop, bool Lava);

    /// <summary>
    /// The ground of column x,z near height `aroundY`. Trees (logs and leaves), grass and flowers are seen through: they are cleared with the rest. Water over the ground is noted (it is filled
    /// in), lava and someone's build are not to be touched (null, or Lava). Null too where the world is not loaded.
    /// </summary>
    private SiteColumn? SiteColumnAt(int x, int z, int aroundY)
    {
        var w = client.World;
        var waterTop = int.MinValue;
        for (var y = aroundY + 16; y >= aroundY - 16; y--)
        {
            var s = w.GetState(x, y, z);
            if (s < 0) return null;
            if (s == 0 || D.IsAir(s)) continue;
            var f = D.StateFlags[s];
            if ((f & GameData.FlagLava) != 0) return new SiteColumn(y + 1, waterTop, true);
            if ((f & GameData.FlagWater) != 0) { if (waterTop == int.MinValue) waterTop = y + 1; continue; }
            var name = D.BlockOf(s).Name;
            if (name.EndsWith("_log") || name.EndsWith("_leaves") || (f & GameData.FlagReplaceable) != 0 || !D.HasCollision(s)) continue;
            if (client.IsBuiltBlock(D.BlockOf(s))) return null;
            if (Standable(s)) return new SiteColumn(y + 1, waterTop, false);
        }
        return null;
    }

    /// <summary>Where a footprint will be built, at which level, and what it takes: blocks to cut away and blocks to fill.</summary>
    public sealed record SitePlan(int X0, int Z0, int Level, int Dig, int Fill);

    /// <summary>
    /// Picks where to build a width x depth footprint with `clear` open blocks above it, within 24 blocks of (nearX, nearZ): the cheapest place to MAKE level (blocks to cut away, blocks to fill,
    /// a little for the walk), never over lava or water deeper than the floor, someone's build, or a person. Nothing has to be flat or clear already: that is what leveling is for. Null only when
    /// there is nowhere at all (all of it lava, built on or not loaded).
    /// </summary>
    public SitePlan? PlanSite(double nearX, double nearY, double nearZ, int width, int depth, int clear, Entity? keepClear)
    {
        int by = (int)Math.Floor(nearY + 1e-6), cx = (int)Math.Floor(nearX), cz = (int)Math.Floor(nearZ);
        var cache = new Dictionary<(int, int), SiteColumn?>();
        SiteColumn? Col(int x, int z) { if (!cache.TryGetValue((x, z), out var c)) cache[(x, z)] = c = SiteColumnAt(x, z, by); return c; }
        SitePlan? best = null;
        var bestCost = double.MaxValue;
        const int R = 24;
        var tops = new List<int>(width * depth);
        for (var dx = -R; dx <= R; dx += 2)
        for (var dz = -R; dz <= R; dz += 2)
        {
            var dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist > R) continue;
            int x0 = cx + dx - width / 2, z0 = cz + dz - depth / 2, x1 = x0 + width - 1, z1 = z0 + depth - 1;
            if (client.Entities.Values.Any(e => e.IsPlayer && e.X > x0 - 1.5 && e.X < x1 + 2.5 && e.Z > z0 - 1.5 && e.Z < z1 + 2.5 && e.Uuid != client.Profile.Id)) continue;
            if (keepClear != null && keepClear.X > x0 - 1.5 && keepClear.X < x1 + 2.5 && keepClear.Z > z0 - 1.5 && keepClear.Z < z1 + 2.5) continue;
            tops.Clear();
            var bad = false;
            var waterTops = new List<int>();
            for (var x = x0; x <= x1 && !bad; x++)
            for (var z = z0; z <= z1 && !bad; z++)
            {
                if (Col(x, z) is not { } c || c.Lava) { bad = true; break; }
                tops.Add(c.Top);
                if (c.WaterTop != int.MinValue) waterTops.Add(c.WaterTop);
            }
            if (bad) continue;
            var sorted = tops.OrderBy(t => t).ToList();
            var level = sorted[sorted.Count / 2];
            if (waterTops.Any(t => t > level) || waterTops.Count > tops.Count / 2) continue;       // water standing above the floor, or mostly a pond
            long dig = 0, fill = 0;
            foreach (var t in tops) { if (t > level) dig += t - level; else fill += level - t; }
            if (sorted[^1] - level > 6 || level - sorted[0] > 6 || dig + fill > 220) continue;
            var cost = dig + fill * 1.3 + dist * 0.6 + Math.Abs(level - by) * 2;
            if (cost < bestCost) { bestCost = cost; best = new SitePlan(x0, z0, level, (int)dig, (int)fill); }
        }
        return best;
    }

    /// <summary>What he fills a hollow with: dirt and ordinary stone (what digging the high ground gives him), never sand or gravel (they fall).</summary>
    private static bool IsFiller(ItemStack s) => s.Name is "minecraft:dirt" or "minecraft:coarse_dirt" or "minecraft:cobblestone" or "minecraft:stone" or "minecraft:andesite" or "minecraft:diorite"
        or "minecraft:granite" or "minecraft:tuff" or "minecraft:netherrack" or "minecraft:cobbled_deepslate" or "minecraft:deepslate" or "minecraft:grass_block" or "minecraft:podzol" or "minecraft:mud"
        or "minecraft:terracotta" || s.Name.EndsWith("sandstone") || s.Name.EndsWith("_terracotta");

    /// <summary>
    /// Makes the footprint of a plan level: goes there, cuts away everything standing in it above the floor level (hill, trees, leaves), picks up what that gave (dirt, stone, wood), fills every
    /// column up to the floor level with it (getting more dirt when it is short) and leaves a solid, open, level site. The cost of not having a "perfect" place.
    /// </summary>
    public async Task<(bool, string?)> LevelSiteAsync(SitePlan plan, int width, int depth, int clear, CancellationToken ct)
    {
        int x0 = plan.X0, z0 = plan.Z0, x1 = x0 + width - 1, z1 = z0 + depth - 1, level = plan.Level;
        double mx = x0 + width / 2.0, mz = z0 + depth / 2.0;
        var by = level;
        await client.GoToAsync(Goal.Near(mx, level, mz, 3 + Math.Max(width, depth) / 2.0), new PathOptions { CanDig = true, MaxNodes = 30_000 }, ct);

        // Cut away: every solid thing in the footprint from the floor level up (over the tallest ground in it too), highest first.
        var maxY = level + clear;
        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
            if (SiteColumnAt(x, z, by) is { } c) maxY = Math.Max(maxY, c.Top + 12);
        var cells = new List<(int X, int Y, int Z)>();
        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
            for (var y = level; y < maxY; y++)
            {
                var s = client.World.GetState(x, y, z);
                if (s <= 0 || D.IsAir(s) || (D.StateFlags[s] & (GameData.FlagLava | GameData.FlagWater)) != 0) continue;
                if (Open(s)) continue;
                if (client.IsBuiltBlock(D.BlockOf(s)) && !D.BlockOf(s).Name.EndsWith("_log")) return (false, "Someone has built there, and I won't take their build apart.");
                cells.Add((x, y, z));
            }
        var dug = 0;
        foreach (var (x, y, z) in cells.OrderByDescending(c => c.Y).ThenBy(c => Math.Abs(c.X - mx) + Math.Abs(c.Z - mz)))
        {
            ct.ThrowIfCancellationRequested();
            var state = client.World.GetState(x, y, z);
            if (Open(state) || state <= 0) continue;
            if (D.Hardness[state] < 0) return (false, "There's something I can't break in the way.");
            if (client.DistanceToBlock(x, y, z) > client.BlockReach - 0.7)
                await client.GoToAsync(new GoalReachBlock(x, y, z, client.BlockReach - 1.0), new PathOptions { CanDig = true, MaxNodes = 10_000 }, ct);
            await client.EquipBestToolAsync(state, ct);
            if (await client.DigAsync(x, y, z, ct)) dug++;
            if (dug % 12 == 11) await PickUpNearbyAsync((int)mx, level, (int)mz, ct);
        }
        // Everything that dropped lies in or by the footprint: pick it up (dirt and stone are the fill, logs are for the walls).
        for (var i = 0; i < 12; i++)
        {
            if (!client.Entities.Values.Any(e => e.IsItem && e.X > x0 - 4 && e.X < x1 + 5 && e.Z > z0 - 4 && e.Z < z1 + 5)) break;
            await PickUpNearbyAsync((int)mx, level, (int)mz, ct);
        }

        // Fill: every column up to the floor level, bottom first, with what he carries (dirt, cobblestone...), getting more dirt when short.
        var fill = new List<Placement>();
        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
        {
            var top = SiteColumnAt(x, z, by)?.Top ?? level;
            for (var y = top; y < level; y++) fill.Add(new Placement(x, y, z, IsFiller, "dirt or stone to fill with"));
        }
        if (fill.Count > 0)
        {
            var need = fill.Count - Have(IsFiller);
            if (need > 0) await CollectAsync("dirt", need, ct);
            var (placed, failed, missing) = await client.BuildAsync(fill.OrderBy(p => p.Y).ThenBy(p => Math.Abs(p.X - mx) + Math.Abs(p.Z - mz)).ToList(), ct);
            if (missing != null && placed < fill.Count / 2) return (false, $"I couldn't get enough dirt or stone to level the ground (I placed {placed} of {fill.Count}).");
        }
        return (true, dug + fill.Count > 0 ? $"I levelled the ground first ({dug} blocks cut away, {fill.Count} filled in)." : null);
    }

    /// <summary>A flat site as it stands, or one MADE level near the player: cut down the hill and the trees in the way, fill the hollow. False only when there is nowhere at all.</summary>
    private async Task<((int X, int Y, int Z)? Site, string? Note, string? Failure)> SiteForAsync(double ox, double oy, double oz, int width, int depth, int clear, Entity? player, CancellationToken ct)
    {
        if (FindFlatRect(ox, oy, oz, width, depth, clear, player) is { } flat) return (flat, null, null);
        if (PlanSite(ox, oy, oz, width, depth, clear, player) is not { } plan)
            return (null, null, "There's nowhere I can build that within about 24 blocks: it's all lava, water or other people's builds, or I haven't seen the ground yet.");
        var (ok, note) = await LevelSiteAsync(plan, width, depth, clear, ct);
        if (!ok) return (null, null, note);
        return ((plan.X0, plan.Level, plan.Z0), note, null);
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
        // Nothing has to be flat or clear: a spot is MADE level (the hill and the trees cut away, the hollow filled in), never refused for being uneven.
        var (found, levelNote, failure) = await SiteForAsync(ox, oy, oz, script.Width, script.Depth, script.Height + 1, player, ct);
        if (found is not { } site) return (false, failure!);
        var (built, text) = await BuildAtAsync(scriptText, site, ct);
        return (built, levelNote == null ? text : levelNote + " " + text);
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
        // A pen is fence, whatever material was said; the others are built of what we carry most of unless one was named.
        var chosen = kind == "pen" ? ((string, Func<ItemStack, bool>, string, string)?)("fence", BuildScript.Matcher("fence"), "wooden", "fence") : ChooseMaterial(material);
        if (chosen is not var (wallName, _, wallPretty, _)) return (false, $"I can't build with {material}.");
        string roof;
        try { roof = string.IsNullOrWhiteSpace(roofMaterial) ? wallName : BuildScript.ResolveMaterial(roofMaterial, D); }
        catch (BuildScriptException e) { return (false, e.Message); }

        var rise = kind == "house" ? (Math.Min(w, d) + 1) / 2 : kind == "pyramid" ? (w + 1) / 2 : 1;
        var player = FindPlayer(nearPlayer);
        var (ox, oy, oz) = player != null ? (player.X, player.Y, player.Z) : client.Position;
        // Nothing has to be flat or clear: a spot is MADE level (the hill and the trees cut away, the hollow filled in), never refused for being uneven (TJ, 2026-11-03: "the entire game world is
        // buildable"; every one of his own projects for two hours ended in two seconds with "can't find a flat, clear spot").
        var (found, levelNote, siteFailure) = await SiteForAsync(ox, oy, oz, w, d, h + rise + 2, player, ct);
        if (found is not { } site) return (false, siteFailure!);

        // The doorway (or gate) faces whoever asked, so they can walk right in, unless a side was given.
        var side = doorSide?.Trim().ToLowerInvariant() switch
        {
            "north" or "n" => BlockFace.North, "south" or "s" => BlockFace.South, "east" or "e" => BlockFace.East, "west" or "w" => BlockFace.West,
            _ => Facing(site.X + w / 2.0, site.Z + d / 2.0, ox, oz),
        };
        string MakeScript(string wall, string roofOf) => BuildShapes.Script(kind, w, d, h, wall, roofOf, side,
            windows: Have(s => s.Name == "minecraft:glass") >= 3, door: Have(Blueprints.IsDoor) > 0, torch: Have(s => s.Name == "minecraft:torch") > 0);
        var script = MakeScript(wallName, roof);
        // No material asked for, and the one we carry most of is short and not something we can fetch (5 dirt for a house that needs 71: live on AubsCraft, 2026-11-01, he stood still): build
        // from wood instead, which GatherForBuildAsync cuts and crafts. A material that was ASKED for is left alone, so "build it of dirt" still says how much dirt is missing.
        if (kind != "pen" && string.IsNullOrWhiteSpace(material) && string.IsNullOrWhiteSpace(roofMaterial) && wallName != "planks" && !Fetchable(wallName) && Shortage(script))
        {
            (wallName, wallPretty) = ("planks", "wooden");
            script = MakeScript(wallName, wallName);
        }
        // Wood or stone that is missing is fetched first (the spot is already chosen, so the walk to the trees does not lose it).
        var (gathered, gatherNote) = await GatherForBuildAsync(script, ct);
        if (!gathered) return (false, (levelNote == null ? "" : levelNote + " ") + gatherNote!);
        if (levelNote != null) gatherNote = levelNote + (gatherNote == null ? "" : " " + gatherNote);
        var (ok, text) = await BuildAtAsync(script, site, ct);
        if (!ok) return (false, (gatherNote == null ? "" : gatherNote + " ") + text.Replace("build that", $"build a {w} by {d} {wallPretty} {kind}"));
        if (gatherNote != null) text = gatherNote + " " + text;
        return (true, $"{text} It is a {wallPretty} {kind}" + (kind is "house" or "castle" ? $", with the {(kind == "castle" ? "gate" : "door")} on the {Name(side)} side." : "."));
    }

    /// <summary>Material <see cref="GatherForBuildAsync"/> can fetch by itself (wood via logs, cobblestone via a pickaxe).</summary>
    private static bool Fetchable(string material) => material.Bare() is "planks" or "cobblestone" || material.EndsWith("_planks");

    /// <summary>True if we carry less of something than the script needs.</summary>
    private bool Shortage(string scriptText)
    {
        try { return BuildScript.Parse(scriptText, D).Needs.Any(kv => Have(BuildScript.Matcher(kv.Key)) < kv.Value); }
        catch (BuildScriptException) { return false; }
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
        // Step away from it so we are not left standing inside our own build (live 2026-11-03: he finished a house, was still inside it 8 seconds later, and the stuck reflex dug him out through the wall).
        await LeaveFootprintAsync(corner, script.Width, script.Depth, ct);
        var size = $"{script.Width} by {script.Depth}, {script.Height} high";
        if (missing != null) return (false, $"I ran out of {missing} while building ({placed} blocks placed, {size}). Give me more and say keep building.");
        return failed == 0
            ? (true, $"I built it: {placed} blocks placed ({size}).")
            : (true, $"I built most of it ({placed} blocks placed, {size}), but {failed} {(failed == 1 ? "block" : "blocks")} wouldn't go where I wanted.");
    }

    /// <summary>
    /// Gets out of a footprint he has just built on: tries to walk to a point two blocks off each side in turn (north first, as before; the doorway is on one of them), a few seconds each, and when
    /// none can be reached from inside, digs out through the nearest wall: it is his own build, and being shut in it is worse than a hole in it.
    /// </summary>
    private async Task LeaveFootprintAsync((int X, int Y, int Z) corner, int width, int depth, CancellationToken ct)
    {
        bool Inside() { var (px, py, pz) = client.Position; return px > corner.X && px < corner.X + width && pz > corner.Z && pz < corner.Z + depth && py < corner.Y + 4; }
        double cx = corner.X + width / 2.0, cz = corner.Z + depth / 2.0;
        var spots = new (double X, double Z)[] { (cx, corner.Z - 2.5), (corner.X + width + 1.5, cz), (corner.X - 2.5, cz), (cx, corner.Z + depth + 1.5) };
        foreach (var (sx, sz) in spots)
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(TimeSpan.FromSeconds(8));
            try { await client.GoToAsync(Goal.Near(sx, corner.Y, sz, 2.0), new PathOptions { MaxNodes = 8_000 }, step.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (!Inside()) return;
        }
        // Shut in: dig out through the wall nearest an open side.
        var (x, y, z) = client.Position;
        var nearest = spots.OrderBy(s => (s.X - x) * (s.X - x) + (s.Z - z) * (s.Z - z)).First();
        using var out_ = CancellationTokenSource.CreateLinkedTokenSource(ct);
        out_.CancelAfter(TimeSpan.FromSeconds(30));
        try { await client.GoToAsync(Goal.Near(nearest.X, corner.Y, nearest.Z, 1.5), new PathOptions { CanDig = true, MaxNodes = 8_000 }, out_.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
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
            if (ct.IsCancellationRequested || client.Inventory.Count(choice.Seed) == 0 || planted >= seeds) break;       // planted >= seeds: the server's inventory update for the last seed can lag, and a ninth block was tilled with no seed left (a flaky test, 2026-10-15)
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
            // Digging allowed: hills and walls are no reason not to reach an animal (never through someone's build: the path finder will not dig those).
            try { await client.GoToAsync(Goal.Near(now.X, now.Y, now.Z, Math.Max(1.5, within - 1.0)), new PathOptions { CanDig = true, MaxNodes = 15_000 }, hop.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        // Behind a fence we cannot get closer, but an animal inside a pen can still be fed across it: the server lets us interact up to 3 blocks of the animal's box (about 3.2 to its middle) (seen 2026-10-15 on the AubsCraft dev world).
        return client.Entities.TryGetValue(target.Id, out var last) && last.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z) <= Math.Max(within, 3.2);
    }

    private List<Entity> AnimalsNear(string kind, bool adultsOnly)
    {
        var (x, y, z) = client.Position;
        return client.Entities.Values.Where(e => e.Name == kind && (!adultsOnly || !IsBaby(e)) && e.DistanceTo(x, y, z) < 44).OrderBy(e => e.DistanceTo(x, y, z)).ToList();
    }

    /// <summary>
    /// Goes and looks: walks up to `maxLegs` legs of about 48 blocks, a different way each time, and stops as soon as `found()` is true. "I don't see any sheep" is not an answer while there is more world
    /// to walk (he said it, and "I couldn't find any diamond ore", without moving a block: TJ, 2026-11-03). Returns whether it was found, how many legs, and how far he walked.
    /// </summary>
    private async Task<(bool Found, int Legs, double Walked)> LookFurtherAsync(Func<bool> found, int maxLegs, CancellationToken ct)
    {
        var legs = 0;
        double walked = 0;
        while (!found() && legs < maxLegs && !ct.IsCancellationRequested)
        {
            legs++;
            var (lx, _, lz) = client.Position;
            var (dx, dz) = new[] { (0, -1), (1, 0), (0, 1), (-1, 0) }[(legs + (int)(Math.Abs(lx) + Math.Abs(lz))) % 4];
            var before = client.Position;
            for (var hop = 0; hop < 8 && !found(); hop++)
            {
                var (px, _, pz) = client.Position;
                if (Math.Abs((px - lx) * dx + (pz - lz) * dz) >= 46) break;
                using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
                step.CancelAfter(TimeSpan.FromSeconds(6));
                try { await client.GoToAsync(Goal.XZ((int)lx + dx * 48, (int)lz + dz * 48), new PathOptions { CanDig = true, MaxNodes = 20_000 }, step.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                var (nx, _, nz) = client.Position;
                if (Math.Abs((nx - px) * dx + (nz - pz) * dz) < 1) break;
            }
            var after = client.Position;
            walked += Math.Sqrt((after.X - before.X) * (after.X - before.X) + (after.Z - before.Z) * (after.Z - before.Z));
        }
        return (found(), legs, walked);
    }

    private static string Looked(int legs, double walked) => legs == 0 ? "" : $" (I walked about {walked:0} blocks in {legs} {(legs == 1 ? "direction" : "different directions")} to look)";

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
            var lookedLegs = 0; double lookedWalked = 0;
            if (adults.Count < 2)
            {
                (_, lookedLegs, lookedWalked) = await LookFurtherAsync(() => AnimalsNear(kind, adultsOnly: true).Count(a => !fedAlready.Contains(a.Id)) >= 2, 4, ct);
                adults = AnimalsNear(kind, adultsOnly: true).Where(a => !fedAlready.Contains(a.Id)).ToList();
            }
            if (adults.Count < 2) return babies > 0 ? (true, $"{Report(babies)} I need two more adult {Plain(kind)}s close by for another pair.") : (false, $"I need two adult {Plain(kind)}s to breed them, and I only found {adults.Count}{Looked(lookedLegs, lookedWalked)}.");
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
        var (_, legs, walked) = await LookFurtherAsync(() => AnimalsNear("minecraft:sheep", adultsOnly: true).Any(s => !IsSheared(s)), 4, ct);
        var unreached = 0;
        while (!ct.IsCancellationRequested && (count <= 0 || sheared < count))
        {
            var sheep = AnimalsNear("minecraft:sheep", adultsOnly: true).FirstOrDefault(s => !IsSheared(s) && !done.Contains(s.Id));
            if (sheep == null) break;
            done.Add(sheep.Id);
            if (!await ApproachAsync(sheep, 2.6, ct)) { unreached++; continue; }
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
        if (sheared == 0) return (false, unreached > 0 ? $"I saw {unreached} sheep with wool on but I couldn't get to {(unreached == 1 ? "it" : "them")}." : "I looked for sheep with wool on and didn't find any" + Looked(legs, walked) + ".");
        var got = Have(s => s.Name.EndsWith("_wool")) - wool;
        return (true, $"I sheared {sheared} sheep and got {got} wool.");
    }

    /// <summary>Milks the nearest adult cow with a bucket.</summary>
    public async Task<(bool, string)> MilkCowAsync(CancellationToken ct)
    {
        if (Have(s => s.Name == "minecraft:bucket") == 0) return (false, "I need an empty bucket to milk a cow.");
        Entity? FindCow() => AnimalsNear("minecraft:cow", adultsOnly: true).FirstOrDefault() ?? AnimalsNear("minecraft:mooshroom", adultsOnly: true).FirstOrDefault();
        var (_, legs, walked) = await LookFurtherAsync(() => FindCow() != null, 4, ct);
        var cow = FindCow();
        if (cow == null) return (false, "I looked for a cow to milk and didn't find one" + Looked(legs, walked) + ".");
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
        var lookedForTarget = false;
        var lookedLegs = 0;
        double lookedWalked = 0;
        var down = new HashSet<int>();                                  // a mob stays listed for its death animation: once it is down it is not a target and not counted again
        while (!ct.IsCancellationRequested)
        {
            if (client.Health < 6) return (kills > 0, $"I had to stop shooting, I'm hurt (health {client.Health / 2:0} hearts). {Report(shots, kills)}");
            if (Have(s => s.Name == "minecraft:arrow") == 0) return (kills > 0, $"I ran out of arrows. {Report(shots, kills)}");
            var (px, py, pz) = client.Position;
            Entity? Pick() => client.Entities.Values.Where(e => !e.IsPlayer && matches(e) && !down.Contains(e.Id) && e.Health is not <= 0 && e.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z) < 44)
                .OrderBy(e => e.DistanceTo(client.Position.X, client.Position.Y, client.Position.Z)).FirstOrDefault();
            var target = Pick();
            if (target == null && shots == 0 && kills == 0 && !lookedForTarget)
            {
                lookedForTarget = true;
                (_, lookedLegs, lookedWalked) = await LookFurtherAsync(() => Pick() != null, 4, ct);
                target = Pick();
            }
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
        if (shots == 0 && kills == 0) return (false, $"I looked for {what} to shoot at and didn't find any{Looked(lookedLegs, lookedWalked)}.");
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
            // Only what a player standing here could SEE counts (Sight): the server sends every buried ore and every tracked animal, and reporting those is an X-ray, not exploring.
            var eye = client.EyePosition;
            foreach (var (x, y, z) in client.World.FindBlocks(stateKind.ContainsKey, client.Position, 48, 4096))
            {
                var kind = stateKind[client.World.GetState(x, y, z)];
                if (kind == "a village")
                {
                    if (villages.All(v => (v.X - x) * (v.X - x) + (v.Z - z) * (v.Z - z) > 64 * 64) && Sight.CanSeeBlock(client.World, eye, x, y, z)) villages.Add((x, y, z));
                }
                else if (!seenBlocks.ContainsKey(kind) && Sight.CanSeeBlock(client.World, eye, x, y, z)) seenBlocks[kind] = (x, y, z);
            }
            foreach (var e in client.Entities.Values.Where(e => NotableMobs.Contains(e.Name)))
                if (e.DistanceTo(eye.X, eye.Y, eye.Z) <= 48 && Sight.CanSeeBody(client.World, eye, e.X, e.Y, e.Z))
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

        // He went nowhere (blocked in, or flying with nothing working): that is not "I finished exploring", and nothing seen from the spot he started on is a discovery of the walk. Live
        // 2026-11-02: "I finished exploring north: I explored 0 blocks north" was logged to his memory twice.
        if (far < Math.Min(8, distance / 4.0))
        {
            await TravelAsync(start.Item1, start.Item2, start.Item3, "where I started", ct);
            return (false, $"I couldn't really go anywhere: I only got {far:0} blocks {way}.");
        }

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

    /// <summary>
    /// Lands from flight in survival: holds sneak, which flies DOWN (about 4 blocks a second, and flying takes no fall damage), until his feet touch the ground (the client then ends
    /// the flight by itself) or he is over water (then he ends it himself: water is a soft landing). Live on AubsCraft 2026-11-02: the account has /fly on (EssentialsX "Set fly mode
    /// enabled" at login), the server said "flying", and with no gravity and "jump" meaning "up" he rose from the water at spawn to 50 blocks up and hovered there for an hour: walking,
    /// building and the escape reflexes all assume ground. Not for creative or spectator, where flying is how one moves.
    /// </summary>
    public async Task<(bool, string)> LandAsync(CancellationToken ct)
    {
        if (!client.Flying) return (true, "I'm not flying.");
        if (client.GameMode is not GameMode.Survival and not GameMode.Adventure) return (true, "I may fly here.");
        var c = client.Controls;
        try
        {
            c.Jump = false; c.Forward = false; c.Back = false; c.Left = false; c.Right = false; c.Sprint = false;
            c.Sneak = true;
            for (var tick = 0; tick < 20 * 90; tick++) // 90 seconds: more than enough for a 300 block drop
            {
                ct.ThrowIfCancellationRequested();
                await client.WaitTicksAsync(1, ct);
                if (!client.Flying) return (true, "I landed.");
                if (client.BodySnapshot().InWater) { await client.SetFlyingAsync(false, ct); return (true, "I landed in the water."); }
            }
        }
        finally { c.Sneak = false; }
        return (false, "I could not land.");
    }

    /// <summary>In the water: the body says so, or the blocks he stands in do (the body's reading flickers when he bobs at the surface, and a decision made on one flicker is wrong half the time).</summary>
    private bool InWaterNow() => client.BodySnapshot().InWater || Survey(2).InWater;

    /// <summary>A look around from where he stands: the surface out to `reach` blocks in the eight compass directions (see <see cref="SurroundingsView"/>).</summary>
    public SurroundingsView Survey(int reach) => SurroundingsView.Look(client.World, client.Position, reach);

    /// <summary>The look around as the tool says it: where he is, each direction, and the nearest land, water, lava and drop.</summary>
    public string LookAround() => Survey(96).Describe();

    /// <summary>
    /// One line on his surroundings for the status block, only when it matters (in the water: where land is; lava or a drop close by), else null so an ordinary moment costs nothing in the
    /// prompt. Cheap: a short look first (12 blocks), a long one only when he is in the water.
    /// </summary>
    public string? Awareness()
    {
        var near = Survey(12);
        var line = near.InWater ? Survey(96).Notable() : near.Notable();
        if (!near.InWater && WalkablePatch(TinyPatch + 1) is { Count: > 0 and < TinyPatch } patch)
            line = (line == null ? "" : line + " ") + $"You are on a tiny patch of land ({patch.Count} block{(patch.Count == 1 ? "" : "s")}): water or walls all round. You can swim, dig soft blocks (dirt, sand), climb or build to get off; look_around shows where land is.";
        return line;
    }

    /// <summary>
    /// The hostile mobs within `within` blocks in words, nearest first: what, how far, whether he could see it, and when it is well above or below (through a floor or a cave wall). A
    /// "could not get away from the monsters" every 30 seconds for half an hour (2026-11-03) did not say which monsters or where, so nobody could tell a creeper at his heels from a zombie in a cave.
    /// </summary>
    public string DescribeHostiles(double within)
    {
        var (x, y, z) = client.Position;
        var eye = client.EyePosition;
        var parts = client.Entities.Values.Where(e => IsThreatOnFoot(e.Name) && e.DistanceTo(x, y, z) < within).OrderBy(e => e.DistanceTo(x, y, z)).Take(4)
            .Select(e => $"{e.Name.Bare().Replace('_', ' ')} {e.DistanceTo(x, y, z):0.#} blocks away"
                + (Math.Abs(e.Y - y) >= 3 ? (e.Y > y ? $", {e.Y - y:0} above" : $", {y - e.Y:0} below") : "")
                + (Sight.CanSeeBody(client.World, eye, e.X, e.Y, e.Z) ? "" : ", out of sight"))
            .ToList();
        return parts.Count == 0 ? "none within " + within.ToString("0") : string.Join("; ", parts);
    }

    /// <summary>
    /// The nearest hostile mob on foot that is a threat for choosing to start something: within `regardlessWithin` blocks always, farther (up to `within`) only when he could see it. A skeleton
    /// 13 blocks away behind rock kept him from starting any project for minutes (2026-11-03: "a skeleton is 13 blocks away (needs 16)"): a monster he cannot see cannot see him either.
    /// </summary>
    public Entity? NearestThreat(double within, double regardlessWithin)
    {
        var (x, y, z) = client.Position;
        var eye = client.EyePosition;
        return client.Entities.Values
            .Where(e => IsThreatOnFoot(e.Name) && e.DistanceTo(x, y, z) < within && (e.DistanceTo(x, y, z) < regardlessWithin || Sight.CanSeeBody(client.World, eye, e.X, e.Y, e.Z)))
            .OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
    }

    /// <summary>Why he cannot dig in right here, in words, or null when he can: checked without moving, so a choice is not spent on something that would fail at once (the island he respawns on is mostly sand).</summary>
    public string? DigInProblem()
    {
        var (px, py, pz) = client.Position;
        int x = (int)Math.Floor(px), y0 = (int)Math.Floor(py + 1e-6), z = (int)Math.Floor(pz);
        if (InWaterNow()) return "I can't dig in while I'm in the water.";
        for (var d = 1; d <= 4; d++)
        {
            var s = client.World.GetState(x, y0 - d, z);
            if (s <= 0 || !Standable(s) || client.IsBuiltBlock(D.BlockOf(s)) || D.Hardness[s] < 0 || LavaAround(x, y0 - d, z)) return "The ground here is no good for digging in (not solid, someone's build, or lava near).";
            // Sand and gravel fall into a hole and bury whoever is in it (he "suffocated in a wall", 2026-11-03).
            if (D.BlockOf(s).Name is "minecraft:sand" or "minecraft:red_sand" or "minecraft:gravel" or "minecraft:suspicious_sand" or "minecraft:suspicious_gravel" or "minecraft:concrete_powder" || D.BlockOf(s).Name.EndsWith("_concrete_powder"))
                return "Sand or gravel would fall into the hole.";
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (client.World.GetState(x + dx, y0 - d, z + dz) is var n && n > 0 && (D.StateFlags[n] & GameData.FlagWater) != 0) return "There is water beside the ground here: it would flood the hole.";
        }
        return null;
    }

    /// <summary>The nearest surface water within `reach` blocks, as a block to stand (swim) in, or null: for getting into it when he is on fire.</summary>
    public (int X, int Y, int Z)? NearestWater(int reach)
    {
        var (bx, by, bz) = client.FeetBlock;
        (int X, int Y, int Z)? best = null;
        var bestD = int.MaxValue;
        for (var dx = -reach; dx <= reach; dx++)
        for (var dz = -reach; dz <= reach; dz++)
        {
            var d2 = dx * dx + dz * dz;
            if (d2 > reach * reach || d2 >= bestD || !client.World.IsLoaded(bx + dx, bz + dz)) continue;
            for (var y = by + 2; y >= by - 4; y--)
            {
                var s = client.World.GetState(bx + dx, y, bz + dz);
                if (s > 0 && (D.StateFlags[s] & GameData.FlagWater) != 0) { best = (bx + dx, y, bz + dz); bestD = d2; break; }
                if (s > 0 && D.HasCollision(s)) break;
            }
        }
        return best;
    }

    /// <summary>On fire: into the water, the only thing that puts it out at once (2026-11-03: he burned from 5 hearts to nothing digging a hole one block from the sea). Done the moment the fire is out.</summary>
    public async Task<(bool, string)> PutOutFireAsync(CancellationToken ct)
    {
        for (var hop = 0; hop < 12 && client.OnFire; hop++)
        {
            ct.ThrowIfCancellationRequested();
            if (NearestWater(24) is not { } water) return (false, "I'm on fire and there is no water near.");
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(TimeSpan.FromSeconds(1));
            // The column, not the block: to the path finder the air over water is where a swimmer floats, so a block under the surface is a place it never ends up.
            try { await client.GoToAsync(Goal.XZ(water.X, water.Z), new PathOptions { MaxNodes = 4_000, Sprint = true }, step.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return (!client.OnFire, client.OnFire ? "I'm still on fire." : "I put the fire out in the water.");
    }

    // ---------------------------------------------------------------- where it is hot and where it is not (his own readings)

    private const int HeatCell = 3;     // cells of 8 by 8 blocks
    private readonly Dictionary<(int X, int Z), (double Degrees, DateTime At)> _heat = [];

    /// <summary>
    /// Writes down what his temperature is where he stands. On mc.spawndev.com it goes with the place, not the hour or the water: 46.6 for a whole minute in the water beside the island, 25 in the open sea 40 blocks west (2026-11-03),
    /// so where to cool off is something he has to find out and remember.
    /// </summary>
    public void NoteHeat(double degrees)
    {
        var (x, _, z) = client.FeetBlock;
        lock (_heat) _heat[(x >> HeatCell, z >> HeatCell)] = (degrees, DateTime.UtcNow);
    }

    /// <summary>The middle of the nearest place he has found no hotter than `degrees` in the last half hour, or null.</summary>
    public (int X, int Z)? NearestCool(double degrees)
    {
        var (x, _, z) = client.FeetBlock;
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        lock (_heat)
            return _heat.Where(c => c.Value.Degrees <= degrees && c.Value.At >= cutoff)
                .Select(c => ((c.Key.X << HeatCell) + 4, (c.Key.Z << HeatCell) + 4))
                .OrderBy(c => (c.Item1 - x) * (c.Item1 - x) + (c.Item2 - z) * (c.Item2 - z)).Cast<(int X, int Z)?>().FirstOrDefault();
    }

    /// <summary>Somewhere he has no reading for: the nearest loaded ground or water out along the compass, to go and find out.</summary>
    private (int X, int Z)? NearestUnknownHeat()
    {
        var (x, _, z) = client.FeetBlock;
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        for (var d = 16; d <= 96; d += 8)
            foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, 1), (-1, 1), (1, -1) })
            {
                int nx = x + dx * d, nz = z + dz * d;
                if (!client.World.IsLoaded(nx, nz)) continue;
                lock (_heat) if (_heat.TryGetValue((nx >> HeatCell, nz >> HeatCell), out var known) && known.At >= cutoff) continue;
                return (nx, nz);
            }
        return null;
    }

    /// <summary>
    /// Goes where it is cool and stays until he has cooled and healed, then the job he was on carries on. Goes to the nearest place he knows of; knowing none, he goes and finds out, a place at a time, staying a few seconds at each
    /// to read it. Water alone is no answer (he burned down to nothing treading the warm water beside the island). At most three minutes.
    /// </summary>
    public async Task<(bool, string)> CoolOffAsync(Func<double?> temperature, double coolEnough, float healTo, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var cooled = DateTime.MinValue;
        try
        {
            while (DateTime.UtcNow - started < TimeSpan.FromMinutes(3))
            {
                ct.ThrowIfCancellationRequested();
                var now = temperature();
                if (now is double t && t <= coolEnough)
                {
                    if (cooled == DateTime.MinValue) cooled = DateTime.UtcNow;
                    // Cool: wait here for his health. Natural healing needs a nearly full food bar, so without one a short rest is all there is to wait for.
                    var healed = client.Health >= healTo || (client.Food < 18 && DateTime.UtcNow - cooled > TimeSpan.FromSeconds(15));
                    if (healed && client.Food < 18 && HasFood) { await EatAsync(ct); continue; }      // food first: with it he heals on
                    if (healed) return (true, $"I cooled off ({t:0.#} degrees) and I'm at {client.Health / 2:0.#} hearts.");
                    client.Controls.Jump = client.BodySnapshot().InWater;      // head above the water while he waits
                    await client.WaitTicksAsync(20, ct);
                    continue;
                }
                cooled = DateTime.MinValue;
                var goal = NearestCool(coolEnough) ?? NearestUnknownHeat();
                if (goal is not { } g) return (false, "I don't know anywhere cooler, and there is nowhere left to look.");
                Trace?.Invoke($"too hot ({now:0.#} degrees): heading for {g.X} {g.Z}");
                using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
                step.CancelAfter(TimeSpan.FromSeconds(6));
                try { await client.GoToAsync(Goal.XZ(g.X, g.Z), new PathOptions { MaxNodes = 12_000, Sprint = true }, step.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                // Stay long enough to read the place: the number follows him within a few seconds.
                client.Controls.Jump = client.BodySnapshot().InWater;
                await client.WaitTicksAsync(50, ct);
            }
        }
        finally { client.Controls.Clear(); }
        return (false, $"I couldn't get cool in three minutes ({temperature():0.#} degrees).");
    }

    /// <summary>
    /// Open water to wait in, or null: at least two deep, no land within 8 blocks of it, within `reach` of him, the farther from monsters the better. Walkers and archers do not follow out there (skeletons shoot about
    /// 16 blocks), which on a sand island with nothing to dig in is often the only safe place there is.
    /// </summary>
    public (int X, int Y, int Z)? FindOpenWater(int reach = 40)
    {
        var (bx, by, bz) = client.FeetBlock;
        var (px, py, pz) = client.Position;
        var foes = client.Entities.Values.Where(e => IsThreatOnFoot(e.Name) && e.DistanceTo(px, py, pz) < 48).ToList();
        int? Surface(int x, int z)
        {
            if (!client.World.IsLoaded(x, z)) return null;
            for (var y = by + 3; y >= by - 6; y--)
            {
                var s = client.World.GetState(x, y, z);
                if (s > 0 && (D.StateFlags[s] & GameData.FlagWater) != 0)
                    return client.World.GetState(x, y - 1, z) is var below && below > 0 && (D.StateFlags[below] & GameData.FlagWater) != 0 ? y : null;
                if (s > 0 && D.HasCollision(s)) return null;
            }
            return null;
        }
        (int X, int Y, int Z)? best = null;
        var bestScore = double.MinValue;
        for (var dx = -reach; dx <= reach; dx += 2)
        for (var dz = -reach; dz <= reach; dz += 2)
        {
            var d = Math.Sqrt(dx * dx + dz * dz);
            if (d > reach || Surface(bx + dx, bz + dz) is not int y) continue;
            var open = true;
            foreach (var (ox, oz) in new[] { (8, 0), (-8, 0), (0, 8), (0, -8), (6, 6), (6, -6), (-6, 6), (-6, -6) })
                if (Surface(bx + dx + ox, bz + dz + oz) == null) { open = false; break; }
            if (!open) continue;
            var foeD = foes.Count == 0 ? 48 : foes.Min(e => Math.Sqrt((e.X - bx - dx) * (e.X - bx - dx) + (e.Z - bz - dz) * (e.Z - bz - dz)));
            var score = Math.Min(foeD, 32) - d * 0.75;
            if (score > bestScore) { bestScore = score; best = (bx + dx, y, bz + dz); }
        }
        return best;
    }

    /// <summary>Swims out to open water and treads water until no monster is within 24 blocks and it is day, or for three minutes at most. The leave-water reflex brings him back afterwards.</summary>
    public async Task<(bool, string)> SwimOutAsync(CancellationToken ct)
    {
        if (FindOpenWater() is not { } spot) return (false, "There is no open water near enough.");
        var feet = (X: 0.0, Z: 0.0);
        for (var round = 0; round < 6; round++)
        {
            ct.ThrowIfCancellationRequested();
            var (fx, _, fz) = client.Position;
            feet = (fx, fz);
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(TimeSpan.FromSeconds(10));
            try { if (await client.GoToAsync(Goal.XZ(spot.X, spot.Z), new PathOptions { MaxNodes = 12_000, Sprint = true }, step.Token)) break; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            var (nx, _, nz) = client.Position;
            if (Math.Sqrt((nx - feet.X) * (nx - feet.X) + (nz - feet.Z) * (nz - feet.Z)) < 1) return (false, "I couldn't swim out from here.");
        }
        if (!InWaterNow()) return (false, "I didn't get to the water.");
        var seconds = 0;
        try
        {
            while (seconds < 180)
            {
                ct.ThrowIfCancellationRequested();
                client.Controls.Jump = true;         // treading water: head above it
                await client.WaitTicksAsync(20, ct);
                seconds++;
                var night = client.World.DayTime % 24000 is >= 12600 and < 23400;
                if (!night && NearestHostile(24) == null && NearestFlier(16) == null) break;
            }
        }
        finally { client.Controls.Clear(); }
        return (true, $"I waited {seconds} seconds out in the water.");
    }

    /// <summary>
    /// Takes cover when he is losing a fight he cannot win by running or hitting (phantoms in a swarm killed him 70 seconds after god mode went off, 2026-11-03): digs a 1 by 1 shaft 3 blocks down
    /// into solid ground, seals the top with a block of what he dug, and waits below until nothing hostile is near and his health is back, then digs out and builds himself up to the surface.
    /// Nothing flies or walks into a sealed hole. Refuses (honestly) only where it would be wrong: water or lava round the shaft, no solid ground, no block to seal with.
    /// </summary>
    public async Task<(bool, string)> TakeCoverAsync(CancellationToken ct)
    {
        var (px, py, pz) = client.Position;
        int x = (int)Math.Floor(px), y0 = (int)Math.Floor(py + 1e-6), z = (int)Math.Floor(pz);
        if (DigInProblem() is { } problem) return (false, problem);
        // Down three blocks: the path finder digs straight down (the cheapest way to a goal below).
        await client.EquipBestToolAsync(client.World.GetState(x, y0 - 1, z), ct);
        var down = await client.GoToAsync(Goal.Block(x, y0 - 3, z), new PathOptions { CanDig = true, MaxNodes = 4_000 }, ct);
        if (!down || client.FeetBlock.Y > y0 - 3) return (false, "I couldn't dig down.");
        await PickUpNearbyAsync(x, y0 - 3, z, ct);
        // Seal the hole above: a block of what he dug (or any he carries) in the top cell of the shaft, which has solid ground beside it to click against.
        var (placed, _, _) = await client.BuildAsync([new Placement(x, y0 - 1, z, IsFiller, "something to seal the hole with")], ct);
        var sealed_ = client.World.GetState(x, y0 - 1, z) is var top && top > 0 && Standable(top);
        // Wait below: until nothing hostile is within 12 blocks (the walkers; fliers cannot get in) and health is back to 8 hearts, at most two minutes.
        var waited = 0;
        while (waited < 120 && !ct.IsCancellationRequested && (NearestHostile(12) != null || NearestFlier(16) != null || client.Health < 16))
        {
            await client.WaitTicksAsync(20, ct);
            waited++;
        }
        // Out: break the seal and build himself up three blocks.
        if (sealed_) { await client.EquipBestToolAsync(client.World.GetState(x, y0 - 1, z), ct); await client.DigAsync(x, y0 - 1, z, ct); }
        await client.GoToAsync(Goal.Block(x, y0, z), new PathOptions { CanDig = true, CanPlace = true, MaxNodes = 6_000 }, ct);
        var out_ = client.FeetBlock.Y >= y0;
        return (out_, $"I dug in {(sealed_ ? "and sealed the hole" : "(I had nothing to seal it with)")}, waited {waited} seconds below, and {(out_ ? "came back up" : "am still down there")}.");
    }

    /// <summary>
    /// What is dangerous in the blocks round him, in words, or null: standing IN fire, lava, water or a wall; standing ON magma or a cactus; lava, fire, magma or a cactus within two blocks. The
    /// experience log keeps it so that "burned to death" and "suffocated in a wall" (nine deaths in 37 minutes, 2026-11-03) say what the ground was.
    /// </summary>
    public string? HazardAround()
    {
        var (px, py, pz) = client.Position;
        int x = (int)Math.Floor(px), y = (int)Math.Floor(py + 1e-6), z = (int)Math.Floor(pz);
        string? Name(int bx, int by, int bz) => client.World.GetBlock(bx, by, bz)?.Name.Replace("minecraft:", "");
        static bool Bad(string? n) => n != null && (n is "lava" or "fire" or "soul_fire" or "magma_block" or "campfire" or "soul_campfire" or "cactus" or "sweet_berry_bush" or "powder_snow" or "wither_rose");
        var feet = Name(x, y, z);
        var head = Name(x, y + 1, z);
        var below = Name(x, y - 1, z);
        if (feet != null && Bad(feet)) return $"I was standing in {feet.Replace('_', ' ')}";
        if (head is "lava" or "fire") return $"{head} was at my head";
        if (below != null && Bad(below)) return $"I was standing on {below.Replace('_', ' ')}";
        if (InWaterNow()) return "I was in the water";
        if (head != null && head != "air" && head != "cave_air" && client.World.GetState(x, y + 1, z) is var hs && hs > 0 && D.HasCollision(hs) && !D.IsAir(hs) && (D.StateFlags[hs] & GameData.FlagReplaceable) == 0)
            return $"my head was inside {head.Replace('_', ' ')} (a wall)";
        for (var dx = -2; dx <= 2; dx++)
        for (var dy = -1; dy <= 2; dy++)
        for (var dz = -2; dz <= 2; dz++)
            if (Name(x + dx, y + dy, z + dz) is { } n && Bad(n)) return $"{n.Replace('_', ' ')} {Math.Max(Math.Abs(dx), Math.Abs(dz))} blocks away";
        return null;
    }

    /// <summary>Hostile mobs that shoot or throw from a distance (TJ, 2026-11-03: "skeletons are very good at shooting you from a distance"): stepping away in the open only gives them a clear shot.</summary>
    public static readonly HashSet<string> Ranged = ["minecraft:skeleton", "minecraft:stray", "minecraft:bogged", "minecraft:pillager", "minecraft:witch", "minecraft:piglin"];

    /// <summary>The nearest ranged hostile mob within `within` blocks that he can SEE (it can see him, and an arrow needs the line), if any.</summary>
    public Entity? NearestRanged(double within)
    {
        var (x, y, z) = client.Position;
        var eye = client.EyePosition;
        return client.Entities.Values.Where(e => Ranged.Contains(e.Name) && Hostile.Contains(e.Name) && e.DistanceTo(x, y, z) < within && Sight.CanSeeBody(client.World, eye, e.X, e.Y, e.Z))
            .OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
    }

    /// <summary>Does he carry a sword or an axe?</summary>
    public bool HasWeapon => client.Inventory.Find(s => s.Name.EndsWith("_sword") || s.Name.EndsWith("_axe")).Any();

    /// <summary>The name of the best weapon he carries, or null.</summary>
    public string? BestWeaponName
    {
        get
        {
            string[] order = ["netherite_sword", "diamond_sword", "iron_sword", "stone_sword", "netherite_axe", "diamond_axe", "iron_axe", "stone_axe", "golden_sword", "wooden_sword", "golden_axe", "wooden_axe"];
            foreach (var w in order) if (client.Inventory.Count(w) > 0) return w;
            return null;
        }
    }

    /// <summary>
    /// What a player does with empty pockets (after a death, every item is gone: the lessons he wrote at 18:38 and 18:39 said "I had no weapon. I had no food." and he went back out barehanded to be
    /// killed again): punch trees for logs, make a crafting table, a wooden sword, pickaxe and axe, and, when he can find stone, stone ones; then hunt one or two animals for food. Each step walks to
    /// where the thing is (the search looks 64 blocks and walks further), and says what it got and what it could not.
    /// </summary>
    public async Task<(bool, string)> GetKitAsync(CancellationToken ct, bool hunt = true)
    {
        var got = new List<string>();
        bool Has(string suffix) => client.Inventory.Find(s => s.Name.EndsWith(suffix)).Any();
        if (!Has("_sword") || !Has("_pickaxe") || !Has("_axe"))
        {
            // Five logs make a table (4 planks) and the three wooden tools (2 + 3 + 3 planks and 7 sticks): a little over 4 logs; the crafting makes planks and sticks from logs by itself.
            var wood = Have(s => s.Name.EndsWith("_log")) + Have(s => s.Name.EndsWith("_planks")) / 4;
            if (wood < 5)
            {
                var (_, took) = await CollectAsync("wood", 5 - wood, ct);
                got.Add(took);
            }
            foreach (var tool in new[] { "wooden_sword", "wooden_pickaxe", "wooden_axe" })
            {
                if (Has(tool[6..])) continue;        // "_sword", "_pickaxe", "_axe": with the underscore, because "pickaxe" also ends in "axe"
                var (made, why) = await client.CraftItemAsync("minecraft:" + tool, 1, ct);
                if (!made) { got.Add($"I couldn't make a {tool.Replace('_', ' ')}: {why}."); break; }
            }
        }
        Trace?.Invoke("kit: wooden tools done: " + InventorySummary());
        // Stone is better: with a pickaxe, cobblestone for a stone sword and pickaxe (it looks for stone as it looks for anything: far and wide).
        if (Has("wooden_pickaxe") || Has("stone_pickaxe"))
        {
            if (!client.Inventory.Find(s => s.Name is "minecraft:stone_sword" or "minecraft:iron_sword" or "minecraft:diamond_sword").Any())
            {
                if (Have(s => s.Name == "minecraft:cobblestone") < 3) { var (_, took) = await CollectAsync("cobblestone", 4, ct); got.Add(took); }
                foreach (var tool in new[] { "stone_sword", "stone_pickaxe" })
                    if (Have(s => s.Name == "minecraft:cobblestone") >= 2 && !client.Inventory.Find(s => s.Name == "minecraft:" + tool).Any())
                        await client.CraftItemAsync("minecraft:" + tool, 1, ct);
            }
        }
        return await GetFoodThenSummaryAsync(got, hunt, ct);
    }

    /// <summary>The second half of the kit: food (unless <paramref name="hunt"/> is false), then what he ended up with.</summary>
    public async Task<(bool, string)> GetFoodThenSummaryAsync(List<string> got, bool hunt, CancellationToken ct)
    {
        bool Has(string suffix) => client.Inventory.Find(s => s.Name.EndsWith(suffix)).Any();
        // Food: one or two wild animals, hit with the best weapon he has, their drops picked up.
        // Bare hands will do: a cow takes ten blows, a chicken four, and starving is the alternative (2026-11-03: no wood, so no sword, so no hunting, so no food).
        if (hunt && !HasFood)
        {
            await EquipWeaponAsync(ct);
            for (var animals = 0; animals < 2 && !HasFood; animals++)
            {
                Entity? Find() { var (x, y, z) = client.Position; return client.Entities.Values.Where(e => e.Name is "minecraft:cow" or "minecraft:pig" or "minecraft:chicken" or "minecraft:sheep" or "minecraft:rabbit" && !IsBaby(e)).OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault(); }
                var (found, legs, walked) = await LookFurtherAsync(() => Find() != null, 3, ct);
                if (Find() is not { } animal) { got.Add("I looked for an animal to eat and found none" + Looked(legs, walked) + "."); break; }
                var name = animal.Name.Bare().Replace('_', ' ');
                var won = await FightAsync(animal.Id, null, ct);
                var (hx, hy, hz) = client.Position;
                Trace?.Invoke($"kit: fought the {name}: won {won}, health {client.Health}, at {hx:0} {hy:0} {hz:0}: " + InventorySummary());
                if (won) { await PickUpNearbyAsync((int)Math.Floor(animal.X), (int)Math.Floor(animal.Y), (int)Math.Floor(animal.Z), ct); got.Add($"I killed a {name}."); }
            }
        }
        Trace?.Invoke("kit: food step done: " + InventorySummary());
        var kit = new[] { (Has("_sword"), "a sword"), (Has("_pickaxe"), "a pickaxe"), (Has("_axe"), "an axe"), (HasFood, "food") }.Where(k => k.Item1).Select(k => k.Item2).ToList();
        var missing = new[] { (Has("_sword"), "a sword"), (Has("_pickaxe"), "a pickaxe"), (HasFood, "food") }.Where(k => !k.Item1).Select(k => k.Item2.Replace("a sword", "sword").Replace("a pickaxe", "pickaxe")).ToList();
        var summary = kit.Count == 0 ? "I still have nothing" : "I have " + string.Join(", ", kit);
        return (missing.Count == 0, $"{summary}{(missing.Count > 0 ? $" but no {string.Join(" or ", missing)}" : "")}." + (got.Count > 0 ? " " + string.Join(" ", got.Where(g => g.Length > 0).Select(g => g.Length > 160 ? g[..160] + "..." : g)) : ""));
    }

    /// <summary>The nearest flying hostile mob (a phantom...) within `within` blocks, if any: not one to walk away from, one to hit when it comes down to him.</summary>
    public Entity? NearestFlier(double within)
    {
        var (x, y, z) = client.Position;
        return client.Entities.Values.Where(e => Fliers.Contains(e.Name) && e.DistanceTo(x, y + 1, z) < within).OrderBy(e => e.DistanceTo(x, y + 1, z)).FirstOrDefault();
    }

    /// <summary>The nearest hostile mob within `within` blocks, if any.</summary>
    public Entity? NearestHostile(double within)
    {
        var (x, y, z) = client.Position;
        return client.Entities.Values.Where(e => IsThreatOnFoot(e.Name) && e.DistanceTo(x, y, z) < within).OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
    }

    /// <summary>
    /// Steps away from every hostile mob within 12 blocks: heads for ground at least 14 blocks (plus their spread) from the middle of them, re-aiming every few seconds in case they follow.
    /// Used when idle and a monster is close. (FleeFromAsync, a straight line 12 blocks the other way from ONE monster, failed live on 2026-11-02 with "I couldn't get away from the
    /// monster"; the cause is not known. In the flat test arena it also gets out of a ring of four creepers, in 24 s against 9 s for this, one run each. This one aims at the middle of all
    /// of them and lets the path finder pick any way that ends far enough from them.)
    /// </summary>
    public async Task<(bool, string)> StepAwayFromMonstersAsync(double clearOf, CancellationToken ct)
    {
        double? cx = null, cz = null;
        var spread = 0.0;
        for (var hop = 0; hop < 8; hop++)
        {
            ct.ThrowIfCancellationRequested();
            var (px, py, pz) = client.Position;
            var near = client.Entities.Values.Where(e => IsThreatOnFoot(e.Name) && e.DistanceTo(px, py, pz) < 12).ToList();
            if (near.Count > 0)
            {
                cx = near.Average(e => e.X); cz = near.Average(e => e.Z);
                spread = near.Max(e => Math.Sqrt((e.X - cx.Value) * (e.X - cx.Value) + (e.Z - cz.Value) * (e.Z - cz.Value)));
            }
            // Done when they are not close AND he is on dry ground: out of reach of them but mid-swim is not "stepped away" (the leave-water reflex would then pick the nearest shore, which
            // may be theirs). While swimming he keeps aiming for the far dry land he chose, from where the monsters last were.
            var safe = near.Count == 0 || near.Min(e => e.DistanceTo(px, py, pz)) >= clearOf;
            if (safe && !InWaterNow()) return (true, "I stepped away from the monsters.");
            if (cx is not double awayX || cz is not double awayZ) return (true, "There was nothing to step away from.");
            if (hop == 0) Trace?.Invoke($"stepping away from: {DescribeHostiles(clearOf + 6)}; I am at {px:0} {py:0} {pz:0}{(InWaterNow() ? ", in the water" : "")}");
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(TimeSpan.FromSeconds(4));
            // "Away" ends on DRY LAND (he swam out into a lake with no land in sight, 2026-11-02, because any far-enough place counted, water included). Swimming across to a far shore is fine
            // and wanted; if there is no far shore he can reach, he stays and says so.
            try { await client.GoToAsync(Goal.AwayFrom(awayX, awayZ, 14 + spread, DryStand), new PathOptions { MaxNodes = 8_000 }, step.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        var (x, y, z) = client.Position;
        var closest = client.Entities.Values.Where(e => IsThreatOnFoot(e.Name)).Select(e => e.DistanceTo(x, y, z)).DefaultIfEmpty(double.MaxValue).Min();
        Trace?.Invoke($"could not get away: {DescribeHostiles(clearOf + 6)}; I am at {x:0} {y:0} {z:0}{(InWaterNow() ? ", in the water" : "")}");
        return closest >= clearOf && !InWaterNow() ? (true, "I stepped away from the monsters.") : (false, "I could not get away from the monsters.");
    }

    /// <summary>A line for the log about what a skill is dealing with (the brain shows it in the panel); nothing is said to anyone.</summary>
    public event Action<string>? Trace;

    /// <summary>Is there solid dry ground under (x, y, z) and open air for feet and head? (Water and lava are not open, and neither is a fire or a cactus's spot.)</summary>
    private bool DryStand(int x, int y, int z)
    {
        var w = client.World;
        var below = w.GetState(x, y - 1, z);
        if (below <= 0 || !D.HasCollision(below) || (D.StateFlags[below] & (GameData.FlagWater | GameData.FlagLava)) != 0) return false;
        if (!D.CollisionBoxes(below).Any(b => b.MaxY >= 1.0) || D.BlockOf(below).Name is "minecraft:magma_block" or "minecraft:cactus" or "minecraft:campfire" or "minecraft:soul_campfire") return false;
        return Open(w.GetState(x, y, z)) && Open(w.GetState(x, y + 1, z));
    }

    /// <summary>
    /// Gets out of the water: walks to the nearest dry, solid, open spot within 40 blocks. Live on AubsCraft (2026-11-01) he stood in a river doing nothing: nothing ever told him to leave it.
    /// </summary>
    public async Task<(bool, string)> LeaveWaterAsync(CancellationToken ct)
    {
        if (!InWaterNow()) return (true, "I'm not in the water.");
        var (px, py, pz) = client.Position;
        int bx = (int)Math.Floor(px), bz = (int)Math.Floor(pz), by = (int)Math.Floor(py + 1e-6);
        (int X, int Y, int Z)? best = null;
        // Within 40 blocks (it was 16: out in a lake he said "I can't see dry land close by", the shore being behind him at a distance nobody measured). The nearest dry spot first, but the
        // nearest one he can actually GET to: a bank a whole block above the water is dry and close and no way out of it (live 2026-11-02: he jumped at it forever instead of going around).
        const int reach = 40;
        var spots = new List<(int D2, int X, int Y, int Z)>();
        for (var dx = -reach; dx <= reach; dx++)
        for (var dz = -reach; dz <= reach; dz++)
        {
            var d2 = dx * dx + dz * dz;
            if (d2 > reach * reach || !client.World.IsLoaded(bx + dx, bz + dz)) continue;
            for (var y = by + 3; y >= by - 3; y--)
                if (DryStand(bx + dx, y, bz + dz)) { spots.Add((d2, bx + dx, y, bz + dz)); break; }
        }
        spots.Sort((a, b) => a.D2.CompareTo(b.D2));
        var feet = client.FeetBlock;
        var rejected = new List<(int X, int Z)>();
        var planned = 0;
        foreach (var s in spots)
        {
            if (rejected.Any(r => (r.X - s.X) * (r.X - s.X) + (r.Z - s.Z) * (r.Z - s.Z) < 36) || RecentlyFailedStand(s.X, s.Z)) continue;   // the rest of a bank he cannot climb is no better
            if (++planned > 12) break;
            var (route, complete) = client.Pathfinder.FindPath(feet, Goal.Block(s.X, s.Y, s.Z), new PathOptions { MaxNodes = 12_000 });
            if (complete && route.Count > 0) { best = (s.X, s.Y, s.Z); break; }
            rejected.Add((s.X, s.Z));
        }
        // None had a way he could plan (or all were tried and failed lately): the nearest not tried lately, as before, and the path finder's escalations (a harder fall, digging) may find one.
        if (best == null && spots.FindIndex(s => !RecentlyFailedStand(s.X, s.Z)) is var fi and >= 0)
            best = (spots[fi].X, spots[fi].Y, spots[fi].Z);
        if (best is not var (x, y2, z))
        {
            // Nothing dry within 40: look around, as a player in the water does, and swim toward the nearest land seen at any distance.
            var look = Survey(96);
            if (look.NearestLandSample() is not { } land)
                return (false, $"I'm in the water and I looked around: I can't see any land within {look.Reach} blocks (the world I know goes out about {look.KnownReach}).");
            (int X, int Y, int Z)? stand = null;
            var standD = int.MaxValue;
            for (var ddx = -4; ddx <= 4; ddx++)
            for (var ddz = -4; ddz <= 4; ddz++)
            for (var yy = land.Y + 3; yy >= land.Y - 3; yy--)
                if (ddx * ddx + ddz * ddz < standD && DryStand(land.X + ddx, yy, land.Z + ddz)) { stand = (land.X + ddx, yy, land.Z + ddz); standD = ddx * ddx + ddz * ddz; break; }
            if (stand is not { } st) return (false, $"I can see land {look.NearestLand!.Value.Direction} about {land.Distance} blocks away, but no place to stand on it.");
            (x, y2, z) = st;
        }
        // A long swim is planned and followed in short pieces (each swim step is "interrupted" after a couple of blocks and replanned) and one GoTo gives up after 12 of those: a 56 block swim
        // needs several rounds. Keep going while he is getting closer.
        var ok = false;
        for (var round = 0; round < 8 && !ok; round++)
        {
            var (fromX, _, fromZ) = client.Position;
            var before = Math.Sqrt((fromX - x - 0.5) * (fromX - x - 0.5) + (fromZ - z - 0.5) * (fromZ - z - 0.5));
            ok = await client.GoToAsync(Goal.Block(x, y2, z), new PathOptions { MaxNodes = 25_000 }, ct);
            var (ax, _, az) = client.Position;
            if (!ok && Math.Sqrt((ax - x - 0.5) * (ax - x - 0.5) + (az - z - 0.5) * (az - z - 0.5)) > before - 3) break; // not getting closer: stop
        }
        if (!ok) _failedStands.Add((x, z, DateTime.UtcNow));      // not that one again for a while: the next try goes elsewhere
        return ok ? (true, "I got out of the water.") : (false, "I couldn't get out of the water.");
    }

    private readonly List<(int X, int Z, DateTime At)> _failedStands = [];

    /// <summary>Did he fail to get to a dry spot near here in the last 5 minutes (within 6 blocks)?</summary>
    private bool RecentlyFailedStand(int x, int z)
    {
        _failedStands.RemoveAll(f => DateTime.UtcNow - f.At > TimeSpan.FromMinutes(5));
        return _failedStands.Any(f => (f.X - x) * (f.X - x) + (f.Z - z) * (f.Z - z) < 36);
    }

    // ---------------------------------------------------------------- being stuck

    /// <summary>
    /// Raised when he works something out by trying: a sentence about what he was stuck on, what he tried and what came of it. The brain writes it to his long-term memory as a LESSON, which
    /// the recall tool can find later. (TJ, 2026-11-02: "we can't account for every situation. he needs to be able to learn and try things... when he is playing alone is the best time".)
    /// </summary>
    public event Action<string>? Learned;

    /// <summary>The dry blocks he can WALK to from where he stands (no swimming, digging or building; steps up of one block and drops of three are fine), up to `cap`.</summary>
    public HashSet<(int X, int Y, int Z)> WalkablePatch(int cap = 40)
    {
        var (px, py, pz) = client.Position;
        var start = ((int)Math.Floor(px), (int)Math.Floor(py + 1e-6), (int)Math.Floor(pz));
        var seen = new HashSet<(int X, int Y, int Z)>();
        if (!DryStand(start.Item1, start.Item2, start.Item3)) return seen;
        seen.Add(start);
        var queue = new Queue<(int X, int Y, int Z)>();
        queue.Enqueue(start);
        while (queue.Count > 0 && seen.Count < cap)
        {
            var (x, y, z) = queue.Dequeue();
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                for (var dy = 1; dy >= -3; dy--)
                {
                    var n = (X: x + dx, Y: y + dy, Z: z + dz);
                    if (!DryStand(n.X, n.Y, n.Z)) continue;
                    if (seen.Add(n)) queue.Enqueue(n);
                    break;
                }
        }
        return seen;
    }

    /// <summary>How small a walkable patch has to be for him to count as stranded on it: an island, a pillar, a pit, a ledge with water or a wall all round.</summary>
    public const int TinyPatch = 12;

    /// <summary>
    /// Stranded on a tiny patch (a one-block island with water on three sides and a three-high dirt wall on the fourth, live 2026-11-02, "he has no clue what to do"): looks around, picks the
    /// nearest other land, and TRIES to get there with everything he can do (swim, dig through soft blocks, climb), nearest first, a few candidates. What worked, or what did not, is raised as
    /// <see cref="Learned"/>.
    /// </summary>
    public async Task<(bool, string)> GetOffATinyPatchAsync(CancellationToken ct)
    {
        var patch = WalkablePatch();
        if (patch.Count == 0 || patch.Count >= TinyPatch) return (true, "I'm not stuck.");
        var size = patch.Count;
        var look = Survey(48);
        // The nearest land in each direction that is not part of the patch itself, nearest first.
        var candidates = look.Rays
            .Select(r => r.Samples.Where(s => s.Kind == SurfaceKind.Land && s.Distance >= 2 && !patch.Any(p => p.X == s.X && p.Z == s.Z)).Cast<SurfaceSample?>().FirstOrDefault())
            .Where(s => s != null).Select(s => s!.Value).OrderBy(s => s.Distance).Take(4).ToList();
        var what = $"a tiny patch of land ({size} block{(size == 1 ? "" : "s")}) with water or a wall all round";
        if (candidates.Count == 0)
        {
            var none = $"I was stuck on {what} and I could not see any other land within {look.Reach} blocks to try for.";
            Teach(none, false, ["stuck", "failed"]);
            return (false, none);
        }
        // What he learned the last times (his memory, asked BEFORE trying): the way to try first moves toward what worked and away from what did not. Each candidate is planned (no moving) to
        // see by which ways it would get him there. With no earlier lessons the order is simply nearest first, as before.
        var prior = PriorLessons?.Invoke(TinyPatchKey) ?? [];
        var remembered = "";
        if (prior.Count > 0 && candidates.Count > 1)
        {
            var feet = client.FeetBlock;
            var scored = candidates.Select(c =>
            {
                var (route, complete) = client.Pathfinder.FindPath(feet, Goal.Near(c.X + 0.5, c.Y + 1, c.Z + 0.5, 1.2), new PathOptions { CanDig = true, MaxNodes = 20_000, DigTicks = client.BestDigTicks });
                var ways = MethodsOf(route);                return (Cand: c, Ways: ways, Score: ways.Sum(m => Preference(prior, m)));
            }).ToList();
            var ordered = scored.OrderByDescending(s => s.Score).ThenBy(s => s.Cand.Distance).ToList();
            if (!ordered[0].Cand.Equals(candidates[0]))
            {
                var good = ordered[0].Ways.Where(m => Preference(prior, m) > 0).ToList();
                var bad = scored.First(s => s.Cand.Equals(candidates[0])).Ways.Where(m => Preference(prior, m) < 0).ToList();
                remembered = good.Count > 0 ? $" I remembered that {string.Join(" and ", good.Select(Gerund))} worked before, so I tried that first."
                    : $" I remembered that {string.Join(" and ", bad.Select(Gerund))} had not worked before, so I tried another way first.";
                candidates = ordered.Select(s => s.Cand).ToList();
            }
        }
        var attempts = new List<(string[] Ways, bool Worked)>();
        string[] Tags(bool worked) => ["stuck", worked ? "worked" : "failed", .. attempts.SelectMany(a => a.Ways.Select(w => (a.Worked ? "worked:" : "failed:") + w)).Distinct()];
        foreach (var cand in candidates)
        {
            int swim = 0, dig = 0, climb = 0;
            string[] ways = [];
            void OnPlan(IReadOnlyList<PathStep> steps)
            {
                swim = steps.Count(s => s.Kind == MoveKind.Swim); dig = steps.Sum(s => s.Dig.Length); climb = steps.Count(s => s.Kind is MoveKind.JumpUp or MoveKind.Pillar or MoveKind.Climb);
                if (ways.Length == 0) ways = MethodsOf(steps);      // the first plan: the way he set out by
            }
            var direction = look.Rays.First(r => r.Samples.Contains(cand)).Direction;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(40));
            client.PathPlanned += OnPlan;
            bool arrived;
            try { arrived = await client.GoToAsync(Goal.Near(cand.X + 0.5, cand.Y + 1, cand.Z + 0.5, 1.2), new PathOptions { CanDig = true, MaxNodes = 20_000 }, attempt.Token); }   // 1.2, not more: land 2 blocks away is "within range" of where he already stands
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { arrived = false; }
            finally { client.PathPlanned -= OnPlan; }
            var gotOff = arrived && WalkablePatch().Count >= TinyPatch;
            attempts.Add((ways, gotOff));
            if (gotOff)
            {
                var how = string.Join(" and ", new[] { swim > 0 ? $"swimming about {swim} blocks" : null, dig > 0 ? $"digging through {dig} block{(dig == 1 ? "" : "s")}" : null, climb > 0 ? $"climbing up {climb}" : null }.Where(h => h != null));
                var lesson = $"I was stuck on {what}. I got off by going {direction} to land {cand.Distance} blocks away{(how.Length > 0 ? ", " + how : "")}. That works.{remembered}";
                Teach(lesson, true, Tags(true));
                return (true, lesson);
            }
        }
        var failed = $"I was stuck on {what}. I tried going toward land {candidates.Count} way{(candidates.Count == 1 ? "" : "s")} ({string.Join(", ", candidates.Select(c => $"{look.Rays.First(r => r.Samples.Contains(c)).Direction} {c.Distance}"))}) and could not get off.{remembered}";
        Teach(failed, false, Tags(false));
        return (false, failed);
    }

    /// <summary>Hands a tiny-patch lesson on: structured for the brain's memory (with the ways tried as tags) and as a plain sentence for whoever only wants to read it.</summary>
    private void Teach(string text, bool worked, string[] tags)
    {
        LessonLearned?.Invoke(new Lesson(text, TinyPatchKey, worked, tags));
        Learned?.Invoke(text);
    }

    // ---------------------------------------------------------------- trying things on purpose

    /// <summary>The key of the swim-across experiment (its lesson carries this tag).</summary>
    public const string SwimAcrossKey = "swim-across";

    /// <summary>
    /// Raised with a structured lesson from an experiment: the brain stores it with the tags, so that "have I already tried this?" is an exact lookup, not a guess from similar words.
    /// </summary>
    public event Action<Lesson>? LessonLearned;

    /// <summary>What an experiment taught: the sentence, the experiment's key and whether the thing worked.</summary>
    public sealed record Lesson(string Text, string Key, bool Worked, IReadOnlyList<string>? Tags = null)
    {
        /// <summary>Has the lesson this tag?</summary>
        public bool Has(string tag) => Tags?.Contains(tag) == true;
    }

    /// <summary>The situation key of the tiny-patch lessons.</summary>
    public const string TinyPatchKey = "tiny-patch";

    /// <summary>
    /// His earlier lessons for a situation (by its key), asked before he tries: the brain answers from his memory. Null: no memory, nothing to go on.
    /// </summary>
    public Func<string, IReadOnlyList<Lesson>>? PriorLessons { get; set; }

    /// <summary>
    /// What past lessons say about one way of getting somewhere (swim, dig, climb): +1 when it worked before and not more often failed, -1 when it failed more than it worked, 0 when he
    /// has no experience of it. One lesson is a hint, not a rule: it moves the way to try FIRST, nothing is ever ruled out.
    /// </summary>
    internal static int Preference(IReadOnlyList<Lesson> prior, string method)
    {
        int worked = prior.Count(l => l.Has("worked:" + method)), failed = prior.Count(l => l.Has("failed:" + method));
        return Math.Sign(worked - failed);
    }

    /// <summary>The ways a planned route gets somewhere: swim, dig and/or climb (walking is not a way worth a lesson).</summary>
    internal string[] MethodsOf(IReadOnlyList<PathStep> steps)
    {
        var methods = new List<string>();
        // Swimming is also moving along the top of the water or its bottom (the planner calls that Walk, seen in the 6-block test lake): a step whose feet, or the block under them, is water.
        bool Wet(int x, int y, int z) => client.World.GetState(x, y, z) is > 0 and var st && (D.StateFlags[st] & GameData.FlagWater) != 0;
        if (steps.Any(s => s.Kind == MoveKind.Swim || Wet(s.X, s.Y, s.Z) || Wet(s.X, s.Y - 1, s.Z))) methods.Add("swim");
        if (steps.Any(s => s.Dig.Length > 0)) methods.Add("dig");
        if (steps.Any(s => s.Kind is MoveKind.JumpUp or MoveKind.Pillar or MoveKind.Climb)) methods.Add("climb");
        return methods.ToArray();
    }

    private static string Gerund(string method) => method switch { "swim" => "swimming", "dig" => "digging", _ => "climbing" };

    /// <summary>
    /// A pond or lake with a far shore he can see: where to swim to, how many blocks of water, and which way. Null when nothing fits (no water within 24 blocks, water wider than 30 or narrower
    /// than 6, lava on the way, or no dry place to stand over there).
    /// </summary>
    public (int X, int Y, int Z, int Width, string Direction)? FindSwimAcross()
    {
        var look = Survey(48);
        (int X, int Y, int Z, int Width, string Direction)? best = null;
        foreach (var ray in look.Rays)
        {
            var s = ray.Samples;
            var first = s.ToList().FindIndex(p => p.Kind == SurfaceKind.Water);
            if (first < 0 || s[first].Distance > 24 || s.Take(first).Any(p => p.Kind is not SurfaceKind.Land)) continue;
            var after = first;
            while (after < s.Count && s[after].Kind == SurfaceKind.Water) after++;
            if (after >= s.Count || s[after].Kind != SurfaceKind.Land) continue;     // ends in the unknown, a void or lava: not a shore he can see
            var width = s[after].Distance - s[first].Distance;
            if (width < 6 || width > 30) continue;
            var shore = s[after];
            for (var ddx = -3; ddx <= 3; ddx++)
            for (var ddz = -3; ddz <= 3; ddz++)
            for (var yy = shore.Y + 3; yy >= shore.Y - 3; yy--)
                if (DryStand(shore.X + ddx, yy, shore.Z + ddz))
                {
                    if (best == null || width < best.Value.Width) best = (shore.X + ddx, yy, shore.Z + ddz, width, ray.Direction);
                    goto nextRay;
                }
            nextRay:;
        }
        return best;
    }

    /// <summary>
    /// The experiment: swims across the water in front of him to the far shore, to find out for himself that it can be done (and what it costs). Raises <see cref="LessonLearned"/> either way.
    /// </summary>
    public async Task<(bool, string)> SwimAcrossAsync(CancellationToken ct)
    {
        if (FindSwimAcross() is not { } t) return (false, "There is no pond or lake with a far shore near enough to swim across.");
        var healthBefore = client.Health;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = false;
        for (var round = 0; round < 8 && !ok; round++)
        {
            var (fx, _, fz) = client.Position;
            var before = Math.Sqrt((fx - t.X - 0.5) * (fx - t.X - 0.5) + (fz - t.Z - 0.5) * (fz - t.Z - 0.5));
            ok = await client.GoToAsync(Goal.Block(t.X, t.Y, t.Z), new PathOptions { MaxNodes = 25_000 }, ct);
            var (ax, _, az) = client.Position;
            if (!ok && Math.Sqrt((ax - t.X - 0.5) * (ax - t.X - 0.5) + (az - t.Z - 0.5) * (az - t.Z - 0.5)) > before - 3) break;
        }
        ok = ok && !InWaterNow();
        var lost = Math.Max(0, healthBefore - client.Health) / 2;
        var text = ok
            ? $"I tried swimming across {t.Width} blocks of water to the {t.Direction} shore. It worked, in {sw.Elapsed.TotalSeconds:0} seconds{(lost >= 0.5 ? $", and it cost me {lost:0.#} hearts" : ", and it cost me no health")}. I can cross water that wide."
            : $"I tried swimming across {t.Width} blocks of water to the {t.Direction} shore and I did not get there.";
        var lesson = new Lesson(text, SwimAcrossKey, ok, ["experiment"]);
        LessonLearned?.Invoke(lesson);      // not also Learned: the brain writes each lesson to memory once, and would write this one twice
        if (!ok && InWaterNow()) await LeaveWaterAsync(ct);
        return (ok, text);
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
