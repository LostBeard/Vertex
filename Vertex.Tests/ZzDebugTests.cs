using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Pathing;
using Vertex.Tests.Live;
namespace Vertex.Tests;
public class ZzDebugTests
{
    [Test, Explicit]
    public async Task PathTrace()
    {
        await using var server = await PaperServer.StartAsync("brain", 25936, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await using var c = new MinecraftClient(GameProfile.Offline("Dbg"));
        await c.ConnectAsync("127.0.0.1", 25936);
        await Wait.UntilAsync(() => c.World.LoadedChunks >= 60, TimeSpan.FromSeconds(30), () => "chunks");
        await c.WaitTicksAsync(20);
        var feet = c.FeetBlock;
        TestContext.Out.WriteLine($"pos {c.Position} feet {feet} onGround {c.OnGround}");
        var pf = c.Pathfinder;
        for (var dy = -2; dy <= 2; dy++) TestContext.Out.WriteLine($"feetheight y{feet.Y + dy}: {pf.FeetHeight(feet.X, feet.Y + dy, feet.Z)} block {c.World.GetBlock(feet.X, feet.Y + dy, feet.Z)}");
        // Column profile toward +x for 16 blocks
        for (var x = feet.X; x <= feet.X + 16; x++)
        {
            var col = string.Join(" ", Enumerable.Range(feet.Y - 3, 7).Select(y => $"{y}:{c.World.GetBlock(x, y, feet.Z)?.Name.Replace("minecraft:", "")}{(pf.FeetHeight(x, y, feet.Z) is double f ? $"[{f - y:0.##}]" : "")}"));
            TestContext.Out.WriteLine($"x{x}: {col}");
        }
        var (steps, complete) = pf.FindPath(feet, Goal.Near(feet.X + 13.5, feet.Y + 1, feet.Z - 0.5, 2.5), new PathOptions());
        TestContext.Out.WriteLine($"complete {complete}, {steps.Count} steps: {string.Join(" ", steps.Select(s => $"{s.Kind}({s.X},{s.Y},{s.Z})"))}");
    }
}
