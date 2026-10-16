using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Items;

namespace Vertex.Brain;

/// <summary>Getting what a build needs: wood (logs, made into planks) or cobblestone, before saying "I don't have enough".</summary>
public sealed partial class Skills
{
    /// <summary>
    /// Gathers what a build script lacks when that is something Vertex can fetch itself: planks (it cuts logs and makes planks of the same wood) or cobblestone (it mines stone with a pickaxe).
    /// Everything else (glass, doors, torches, other blocks) is left for <see cref="BuildAtAsync"/> to name as missing. Returns (false, why) when gathering failed, else a note on what was
    /// gathered (null when nothing was needed).
    /// </summary>
    private async Task<(bool Ok, string? Note)> GatherForBuildAsync(string scriptText, CancellationToken ct)
    {
        BuildScript script;
        try { script = BuildScript.Parse(scriptText, D); }
        catch (BuildScriptException) { return (true, null); }       // the plan's own problem is reported by the build
        var notes = new List<string>();
        foreach (var (item, need) in script.Needs)
        {
            var have = Have(BuildScript.Matcher(item));
            if (have >= need) continue;
            var missing = need - have;
            var bare = item.Bare();
            if (bare == "planks" || bare.EndsWith("_planks"))
            {
                var logs = (missing + 3) / 4;
                var (cut, cutText) = await CollectAsync("log", logs, ct);
                if (!cut && Have(s => s.Name.EndsWith("_log")) == 0) return (false, $"I need {missing} more planks for that, and I couldn't cut logs for them: {cutText}");
                var log = client.Inventory.Slots.Skip(PlayerInventory.MainStart).FirstOrDefault(s => !s.IsEmpty && s.Name.EndsWith("_log"));
                var planks = log == null ? "minecraft:oak_planks" : log.Name.Replace("_log", "_planks");
                var (made, why) = await client.CraftItemAsync(planks, missing, ct);
                if (!made) return (false, $"I cut logs but couldn't make planks of them: {why}");
                notes.Add($"cut {logs} log{(logs == 1 ? "" : "s")} for planks");
            }
            else if (bare == "cobblestone")
            {
                var (mined, minedText) = await CollectAsync("cobblestone", missing, ct);
                if (!mined && Have(BuildScript.Matcher(item)) < need) return (false, $"I need {missing} more cobblestone for that, and I couldn't mine it: {minedText}");
                notes.Add($"mined {missing} cobblestone");
            }
        }
        return (true, notes.Count == 0 ? null : "I " + string.Join(" and ", notes) + " first.");
    }
}
