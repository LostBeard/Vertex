using System.Text;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Data;

namespace Vertex.Brain;

/// <summary>
/// The live game as data, for whoever looks after him: where he is, how he is, what he carries, what is near, what hurt him, and a top-down map of the terrain he has loaded. Read-only, and it says nothing
/// the control panel on the same port does not (no sign-in, no chat history). Served at /state and /map.
/// </summary>
public static class StateDump
{
    public static object Snapshot(VertexBrain brain)
    {
        var c = brain.Client;
        var (x, y, z) = c.Position;
        return new
        {
            at = new { x = Math.Round(x, 1), y = Math.Round(y, 1), z = Math.Round(z, 1), dimension = c.World.Dimension },
            biome = brain.Skills.BiomeHere, biomeHeat = brain.Skills.BiomeHeat().Select(b => new { b.Biome, b.Degrees }), hotAt = brain.Reflexes.HotAt,
            health = c.Health, food = c.Food, onFire = c.OnFire, temperature = brain.Temperature, actionBar = brain.LastActionBar, next = brain.Skills.WorkingToward()?.Label, dangerOutcomes = brain.Reflexes.Learner?.Outcomes, onGround = c.OnGround, gameMode = c.GameMode.ToString(), spawned = c.IsSpawned,
            dayTime = c.World.DayTime % 24000, weather = c.Weather,
            task = brain.Tasks.Current, staying = brain.Staying, godMode = brain.GodMode,
            deathsThisRun = brain.DeathsThisRun,
            lastDeath = brain.LastDeath is { } d ? new { x = Math.Round(d.X), y = Math.Round(d.Y), z = Math.Round(d.Z), secondsAgo = (int)(DateTime.UtcNow - d.At).TotalSeconds, d.Message } : null,
            inventory = brain.Skills.InventorySummary(),
            recentPains = brain.RecentPains.Select(p => new { secondsAgo = (int)(DateTime.UtcNow - p.At).TotalSeconds, p.Lost, p.Type, p.Source }),
            entities = c.Entities.Values.Where(e => e.Uuid != c.Profile.Id && !e.IsItem && e.DistanceTo(x, y, z) < 48).OrderBy(e => e.DistanceTo(x, y, z)).Take(40)
                .Select(e => new { name = e.IsPlayer ? e.PlayerName : e.Name.Bare(), distance = Math.Round(e.DistanceTo(x, y, z), 1), x = Math.Round(e.X), y = Math.Round(e.Y), z = Math.Round(e.Z), hostile = Skills.Hostile.Contains(e.Name) }),
            items = c.Entities.Values.Where(e => e.IsItem && e.DistanceTo(x, y, z) < 32).OrderBy(e => e.DistanceTo(x, y, z)).Take(20).Select(e => new { name = e.Item?.Name, distance = Math.Round(e.DistanceTo(x, y, z), 1) }),
        };
    }

    /// <summary>
    /// North is up, x grows to the right. @ him, M a hostile mob, a an animal, P a player; ~ water, ! lava or fire, T a log, t leaves, # stone or ore, . grass or dirt, : sand or gravel, + a built block,
    /// o anything else solid, a blank not loaded. With <paramref name="heights"/>: the last digit of the surface's y in each cell instead, where steps and cliffs show.
    /// </summary>
    public static string Map(VertexBrain brain, int radius, bool heights)
    {
        var c = brain.Client;
        var world = c.World;
        var data = world.Data;
        var (px, py, pz) = c.Position;
        int cx = (int)Math.Floor(px), cy = (int)Math.Floor(py), cz = (int)Math.Floor(pz);
        radius = Math.Clamp(radius, 4, 96);
        var marks = new Dictionary<(int, int), char>();
        foreach (var e in c.Entities.Values)
        {
            if (e.IsItem) continue;
            var mark = e.IsPlayer ? 'P' : Skills.Hostile.Contains(e.Name) ? 'M' : e.Name is "minecraft:cow" or "minecraft:pig" or "minecraft:sheep" or "minecraft:chicken" or "minecraft:rabbit" ? 'a' : '\0';
            if (mark != '\0') marks[((int)Math.Floor(e.X), (int)Math.Floor(e.Z))] = mark;
        }
        marks[(cx, cz)] = '@';
        var sb = new StringBuilder();
        sb.AppendLine($"him {cx} {cy} {cz}; x {cx - radius}..{cx + radius} left to right, z {cz - radius}..{cz + radius} top to bottom (north up)");
        for (var z = cz - radius; z <= cz + radius; z++)
        {
            sb.Append($"{z,6} ");
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                var (cell, y) = Surface(world, data, c, x, cy, z);
                sb.Append(!heights && marks.TryGetValue((x, z), out var m) ? m : heights ? (cell == ' ' ? ' ' : (char)('0' + (Math.Abs(y) % 10))) : cell);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static (char Cell, int Y) Surface(SpawnDev.Minecraft.World.GameWorld world, GameData data, MinecraftClient c, int x, int cy, int z)
    {
        if (!world.IsLoaded(x, z)) return (' ', 0);
        for (var y = cy + 32; y >= cy - 40; y--)
        {
            var s = world.GetState(x, y, z);
            if (s <= 0 || data.IsAir(s)) continue;
            var flags = data.StateFlags[s];
            if ((flags & GameData.FlagLava) != 0) return ('!', y);
            if ((flags & GameData.FlagWater) != 0) return ('~', y);
            var name = data.BlockOf(s).Name;
            if (name is "minecraft:fire" or "minecraft:soul_fire" or "minecraft:magma_block" or "minecraft:campfire") return ('!', y);
            if (!data.HasCollision(s) || (flags & GameData.FlagReplaceable) != 0) continue;     // grass, flowers, snow layers: look at the ground under them
            if (name.EndsWith("_log") || name.EndsWith("_stem")) return ('T', y);
            if (name.EndsWith("_leaves")) return ('t', y);
            if (name is "minecraft:sand" or "minecraft:red_sand" or "minecraft:gravel") return (':', y);
            if (name is "minecraft:grass_block" or "minecraft:dirt" or "minecraft:coarse_dirt" or "minecraft:podzol" or "minecraft:mud" or "minecraft:rooted_dirt" or "minecraft:mycelium" or "minecraft:snow_block") return ('.', y);
            if (name.EndsWith("_ore") || name is "minecraft:stone" or "minecraft:granite" or "minecraft:diorite" or "minecraft:andesite" or "minecraft:deepslate" or "minecraft:tuff" or "minecraft:cobblestone" or "minecraft:terracotta") return ('#', y);
            if (c.IsBuiltBlock(data.BlockOf(s))) return ('+', y);
            return ('o', y);
        }
        return ('?', 0);
    }
}
