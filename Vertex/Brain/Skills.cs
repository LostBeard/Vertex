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
    public ItemStack? FindItem(string what) => MatchStack(client.Inventory.Slots.Skip(PlayerInventory.MainStart), what);

    /// <summary>The stack a player meant by a name: exact item name, then a name containing it, then a custom name.</summary>
    public static ItemStack? MatchStack(IEnumerable<ItemStack> slots, string what)
    {
        var w = Simplify(what.TrimEnd('s'));
        var stacks = slots.Where(s => !s.IsEmpty).ToList();
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
            if (client.AttackReady) await client.AttackAsync(t, ct);
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
        var pretty = (name ?? what).Replace("minecraft:", "").Replace('_', ' ');
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
        var inv = client.Inventory.Slots.Skip(PlayerInventory.MainStart).Where(s => !s.IsEmpty).ToList();
        int Have(Func<ItemStack, bool> m) => inv.Where(m).Sum(s => s.Count);
        string? chosen = null;
        if (!string.IsNullOrWhiteSpace(material))
        {
            var m = material.Trim().ToLowerInvariant().Replace(' ', '_').TrimEnd('s');
            chosen = Blueprints.HouseMaterials.FirstOrDefault(x => x.Contains(m) || m.Contains(x.Replace("minecraft:", "")));
            if (chosen == null) return (false, $"I can't build a house out of {material}.");
        }
        chosen ??= Blueprints.HouseMaterials.OrderByDescending(x => Have(Blueprints.MaterialMatch(x))).First();
        var wall = Blueprints.MaterialMatch(chosen);
        var glass = Have(Blueprints.IsGlass) >= 3;
        var size = Have(wall) >= Blueprints.HouseBlocks(7, glass) ? 7 : 5;
        var need = Blueprints.HouseBlocks(size, glass);
        var pretty = chosen == "planks" ? "wooden" : chosen.Replace("minecraft:", "").Replace('_', ' ');
        if (Have(wall) < need)
            return (false, $"I need {need} {(chosen == "planks" ? "planks" : pretty)} to build a house and I only have {Have(wall)}. (Wood works: one log makes 4 planks.)");

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

    /// <summary>
    /// The nearest spot (within 14 blocks) for a size x size house: solid full ground under every cell at one level,
    /// four blocks of air (or grass and flowers) above it, and nobody standing there.
    /// </summary>
    public (int X, int Y, int Z)? FindHouseSite(double nearX, double nearY, double nearZ, int size, Entity? keepClear)
    {
        var r = size / 2;
        var w = client.World;
        bool Open(int s) => s >= 0 && (D.IsAir(s) || (D.StateFlags[s] & GameData.FlagReplaceable) != 0) && (D.StateFlags[s] & (GameData.FlagWater | GameData.FlagLava)) == 0;
        bool Ground(int s) => s > 0 && D.HasCollision(s) && !D.IsAir(s) && (D.StateFlags[s] & GameData.FlagReplaceable) == 0
            && D.CollisionBoxes(s).Any(b => b.MaxY >= 1.0) && !client.IsBuiltBlock(D.BlockOf(s)) && D.BlockOf(s).Name is not ("minecraft:sand" or "minecraft:gravel");
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
                for (var dy = 0; dy < 4 && ok; dy++)
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
