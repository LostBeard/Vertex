using Vertex.Host;

namespace Vertex.Tests;

/// <summary>The clock on the Minecraft tab (TJ, 2026-11-03: "server time of day and weather"): tick 0 is 6:00 in the morning and a day is 24000 ticks.</summary>
public class GameClockTests
{
    [TestCase(0L, "6:00 AM")]
    [TestCase(5700L, "11:42 AM")]
    [TestCase(6000L, "12:00 PM")]
    [TestCase(12000L, "6:00 PM")]
    [TestCase(18000L, "12:00 AM")]
    [TestCase(23999L, "5:59 AM")]
    [TestCase(24000L + 1000L, "7:00 AM")]       // the day counter keeps counting: only the time of day matters
    public void TheServerClock_IsReadFromTheTicks(long ticks, string expected) => Assert.That(VertexHost.ClockText(ticks), Is.EqualTo(expected));

    /// <summary>EssentialsX's /whois says it in a line; the client's abilities flag does not (god mode cancels damage inside the plugin).</summary>
    [TestCase(" - God mode: true", true)]
    [TestCase("God mode: false", false)]
    [TestCase(" - God mode: enabled", true)]
    [TestCase(" - Fly mode: true", null)]
    [TestCase("HereticSpawn joined the game", null)]
    public void TheServersGodModeLine_IsRead(string line, bool? expected) => Assert.That(Vertex.Brain.VertexBrain.GodModeIn(line), Is.EqualTo(expected));
}
